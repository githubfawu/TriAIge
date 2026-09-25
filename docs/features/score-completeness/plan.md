# Plan: score-completeness

**Date**: 2026-09-25 · **Source of truth**: [requirements.md](requirements.md) · **Projects**: Core, Infrastructure, Agents, Batch, AppHost (+ tests). No git commits or branches are made by the workflow (the user commits manually). Web is not touched.

## 1. Findings that shape the plan (code + real data)

Data was checked read-only: `data/jira_first_20000_requested_fields_synthetic.json` (JSON array, 20 000 records) and `data/jira_hackathon_blind_eval_challenge_20260923083915-1141.json` (envelope `{fetchedAtUtc, runId, baseUrl, requestedMaxResults, actualIssueCount, jql, requestedColumns, missingColumnsInJiraFieldCatalog, mappedFieldKeys, assetNamesResolved, resolvedAssetObjectsCount, records[20]}`).

| # | Finding | Consequence |
|---|---|---|
| F1 | `Ticket.Key` is `required` with `[JsonPropertyName("Issue key")]`. **Neither file has `Issue key`**, so System.Text.Json throws "missing required property". Both `TrainingDataImporter.ImportAsync` and `BatchRunner.ReadTicketsAsync` fail on the real files today. | `Key` becomes optional (`= ""`). Batch assigns a positional key when it is blank. The importer ignores `Key`, because DB tickets are keyed `DB-{Id}` by `TicketEntityMapper.KeyFor`. |
| F2 | Where the key is used: pipeline logs, `TriageFailure.TicketKey` (max 50, required), `TriageSuggestion.TicketKey`, `SimilarTicketKeys` (`DB-{Id}`), the fake pipelines in the Batch tests (keyed by `ticket.Key`), and the `BatchRunner` duplicate-key warning. Self-exclusion in retrieval uses `Ticket.Id`, not `Key`. | A positional key `#<1-based index>` covers every internal use. It is never written to `result.json`. `DB-{Id}` keys stay unchanged. |
| F3 | Training fields: `Work type, Summary, Description, Affected Business or IT Services[1], Business Entity[1], Service Team(s)[1], Reporter, Assignee, Priority, Urgency, Impact, Created date, Status, Resolution, Resolution date, All Comments[1..5]`. Challenge records also have `Request type, Business Critical for Entity, Severity, Linked issues, Due date`. `Assignee` / `Service Team(s)` are null / empty, `Resolution` is null. | Output must **mirror** each record with all its extra fields, so it is built from `JsonObject`s, not from a typed record. `Created date` (`"2026-03-03 19:36"`, not ISO) stays unmapped (`Ticket.Created` maps `"Created"`), so the importer keeps setting `CreatedDate = now`. This is harmless. |
| F4 | **The vocabulary does not match the Core enums.** Training `Resolution` is `done / cancelled / clarification / cannot reproduce` (lowercase) or null (3 031). Core `ResolutionStatus` serialises `Done`, `Cannot Reproduce`. Urgency and Impact in both files use `highest/high/medium/low/lowest` (challenge capitalised: `Highest`…). Core `Urgency` is `Critical…Lowest` and `Impact` is `Major…No Impact`. The CLAUDE.md claim "enum JSON names follow the Jira export" is wrong for the real export. | (a) Rename the `ResolutionStatus` JSON names to the lowercase training vocabulary. (b) Keep the `Urgency` / `Impact` enums (prompt semantics and matrix doc) and add one Core mapping for output: `JiraVocabulary` (Critical↔Highest; Major↔Highest, Significant↔High, Moderate↔Medium, Minor↔Low, NoImpact↔Lowest; requirements §7 no. 1 assumption). `Priority` names already match (`Highest…Lowest`). |
| F5 | Training `Resolution` is the **status**, not resolution text. The real resolution notes are in `All Comments` as `"<email>: Resolution: …"` (5 814 comments, **21 distinct texts**), next to templates (`Problem fixed.`, `Resolution recorded: …`). The drafter prompt renders `Resolution: done` and never renders comments. | Slice 3 relabels this as the status and adds one cleaned resolution note per similar ticket to the drafter prompt: the minimum of FR-02 the drafter needs, which is allowed by Out of Scope. |
| F6 | Routing signal. Service → team is **1:1 in all 20 services** (e.g. Outlook & Email → Enterprise Applications 805/805). Assignee is **near-random**: each service has all 30 assignees, the top one has about 5% (in-sample majority accuracy 4.6%). Adding Business Entity (6.2%) or Work type (4.9%) does not help. | FR4/FR5 majority vote is implemented as specified. Team accuracy will equal service accuracy; expected assignee accuracy is about 5% (risk R3). This answers requirements §7 no. 4 with data. |
| F7 | Status signal. Classes are near-uniform (4 168–4 322 each), independent of work type. There are only **173 distinct descriptions** (groups of 15–4 212). The per-description majority status reaches only 29.2% in-sample, the noise ceiling for about 100 samples over 4 classes. | FR9 will probably show "no signal". Status then rests on LLM semantics of the ticket text (challenge records carry a telling `Request type`, see §8). Slice 5 is scheduled before slice 3 so the result can steer the prompt. |
| F8 | The Batch output type `TriageResult` has `"Issue key"` and no `Urgency` / `Impact` / `Resolution`. `BatchRunner` serialises `List<TriageResult>`. | `TriageResult` is reshaped into "predicted fields only, Jira vocabulary, no key". Batch merges it into each cloned input record. |
| F9 | `ServiceCatalog` holds `TODO` names. The classifier already canonicalises case-insensitively against `IServiceCatalogProvider` and throws on unknown names. The fallback only keeps catalog names, so today it never yields a service. `FallbackSuggestionFactoryTests.Classify_FullTie_UsesEnumOrderAndOrdinalName` assumes `All[0]` sorts ordinally before `All[1]`. | Real names in the order of requirements §6. That test must derive the expected winner ordinally, because "Trading Platform" > "Order Management". |
| F10 | `SuggestionValidator` is static and gets only the suggestion. "Team consistent with the resolver" needs to know whether the service is known to the statistics. `PipelineTypes_DoNotDependOnEntityFrameworkCore` forbids EF types in constructors and fields in `…Infrastructure.Pipeline`. | The pipeline gets an `IRoutingStatisticsSource` (interface in `…Infrastructure.Routing`, no EF in its signature) and passes the snapshot to `Validate`. `new TriagePipeline(...)` appears in 6 test sites; update them. |
| F11 | Pipeline test fakes classify to service `"Email"`, which is not in any catalog. | They must switch to a catalog name once the validator checks the catalog (slice 4). |
| F12 | `data/triage.db` (49 KB) exists from a run without training data. The importer skips when any ticket exists. | Delete `data/triage.db*` once before the first real run so the real 20 000 rows are imported. No schema change is needed (§5). |
| F13 | TF-IDF ties are broken by the lowest `Id`. With duplicate descriptions, the top 10 are usually 10 copies of one template. | The fallback status majority is effectively the majority of 10 random labels. Accepted; dedupe is out of scope (R6). |

## 2. Architecture overview

```
Batch
  ChallengeDocument.ReadAsync(path)                       (new)
    root = JsonNode: JsonArray -> records | JsonObject with "records": JsonArray -> envelope + records | else BatchInputException
    per record i: JsonObject (kept) + Ticket (Deserialize, Key blank -> "#{i+1}")
  BatchRunner: tickets -> ITriagePipeline.TriageAsync (unchanged stream) -> TriageResult.From(suggestion)
  ChallengeDocument.ToOutput(results): clone each record, overwrite predicted fields from
    JsonSerializer.SerializeToNode(TriageResult), keep all other fields/order, never add "Issue key";
    envelope in -> same envelope with records replaced; array in -> array out
  atomic write (unchanged)

Core
  Ticket.Key optional · TriageResult (predicted fields, Jira vocabulary, no key) · JiraVocabulary (new)
  ServiceCatalog (20 real names) · ResolutionStatus JSON names lowercase
  IResolutionDrafter.DraftAsync(ticket, classification, routing, similar, ct) -> ResolutionDraft(Status, Comment)

Infrastructure
  Routing/RoutingStatistics (pure: majority, tie rule, known services)
  Routing/RoutingStatisticsProvider : IRoutingStatisticsSource (load once per process, GROUP BY in SQLite)
  Routing/StatisticsRoutingResolver : IRoutingResolver (replaces StubRoutingResolver)
  Pipeline: route -> draft(routing) -> suggestion.ResolutionStatus -> SuggestionValidator.Validate(suggestion, stats)
  FallbackSuggestionFactory: + status = majority of similar tickets' Resolution, else Done
  TrainingDataImporter: unchanged logic, real default path

Agents
  LlmResolutionDrafter implements the new port (drafter-v2: lowercase vocabulary, routed assignee's voice,
  similar tickets' status + cleaned resolution note); IResolutionDraftAgent and Agents' ResolutionDraft removed
```

Who decides: service = LLM, validated by the catalog. Team / assignee = statistics (code). Urgency / impact = LLM. Priority = `PriorityMatrix`. Status + comment = LLM, validated, with a similar-majority fallback.

## 3. Design decisions

| ID | Decision | Reason / alternative |
|---|---|---|
| D1 | `Ticket.Key` becomes `public string Key { get; init; } = "";` (doc: Jira key if present, else the reader assigns a positional identity). | STJ enforces C# `required`. A JSON contract modifier to relax it would be hidden magic. |
| D2 | Positional key format `#<n>` (1-based), assigned only in Batch (`ChallengeDocument`). Duplicate-key warning only for non-positional keys. | FR1. Cannot collide with Jira keys or `DB-{Id}`. Short enough for `TriageFailure.TicketKey` (50). |
| D3 | Output mirrors the **container**: envelope in → envelope out (metadata kept, `records` replaced); array in → array out. | FR2 "mirror records" plus FR-30 "same schema". Only `ChallengeDocument.ToOutput` changes if the organizers publish another schema. |
| D4 | The predicted field set is defined once, in Core `TriageResult` (`[JsonPropertyName]`s, Jira vocabulary). Batch merges it generically (`foreach property: record[name] = value.DeepClone()`). | Field names and vocabulary are business rules (Core). Batch only does JSON plumbing. |
| D5 | `All Comments` = `[drafted comment]` (replaces the reporter comments); `[]` for a fallback. | Literal FR2 and today's rule. Alternative (append `"<assignee>: <comment>"`) stays open, see §8. |
| D6 | `ResolutionStatus` JSON names become `done`, `cancelled`, `clarification`, `cannot reproduce`. `Urgency` / `Impact` enums unchanged, with output mapping in `JiraVocabulary`. | Status vocabulary is scored verbatim (requirements §2). Renaming urgency/impact would change classifier prompts and many tests for no scoring gain. |
| D7 | Routing statistics live **in memory**, built once per process (lazy, semaphore, failed or cancelled load publishes nothing, like `SimilarTicketIndexProvider`), from one `GROUP BY service, team, assignee` query. No table. | FR4 "computed once and cached", NFR3, no schema change. |
| D8 | Tie rule: highest count, then `StringComparer.OrdinalIgnoreCase` alphabetical (team and assignee). Uses the **first** classified service. Unknown service → `([], null)`. Service lookup is case-insensitive. | FR5, architecture §4.1 and §7. |
| D9 | The drafter port receives `RoutingDecision` and writes in the routed assignee's voice. `DraftPrompts.InferAssignee` is removed. | FR-16 "voice of the assignee" becomes consistent with field 4. The port changes anyway in slice 3. |
| D10 | An invalid status from the model **throws in the agent** (as the classifier does, reason `Draft:Exception`). The validator also rejects a null or undefined status (`InvalidResolutionStatus`) for any drafter implementation. Retries exhausted → full fallback with status = similar-ticket majority (ties: summed score, then enum order), else `Done`. | AC4, the existing agent convention and one retry model. Alternative (keep the LLM classification and comment and only substitute the status) is recorded as R8. |
| D11 | The validator gets a `RoutingStatistics` snapshot: `Validate(TriageSuggestion, RoutingStatistics)`. The pipeline awaits `IRoutingStatisticsSource.GetAsync` in the `Validate` step. | F10. Keeps the validator pure and testable. |
| D12 | FR9 is an `Integration`-category test in `TicketTriage.Infrastructure.Tests` (reads the real file, skips when it is missing, reuses the internal `TfIdfIndex`). Numbers go to the feature README and to CLAUDE.md. | Reproducible, no extra project, excluded from the default test run. |
| D13 | File names: `TrainingDataOptions.Path` default, Batch `appsettings.Development.json`, Batch `launchSettings.json` and AppHost point at the real names. The AppHost reads them from its `appsettings.json` (`Data:TrainingFile`, `Data:ChallengeFile`) with the real names as defaults. | The requirement allows config or renaming. A new challenge file (new runId) needs only a config value or `--input`. |

## 4. Slices

| # | Slice | Goal | ACs | Complexity |
|---|---|---|---|---|
| 1 | Real-data I/O | Batch reads the envelope or array of keyless records and writes a mirrored `result.json`. The importer imports the real 20k array. Real file names are configured. | AC1, AC2 (priority part + batch-level test), AC6, AC8 | M |
| 2 | Real ServiceCatalog + routing statistics | 20 real services. `StatisticsRoutingResolver` replaces the stub. | AC3, AC5, AC8 | M |
| 3 | Resolution status end to end | Core port carries the status. Drafter structured output, fallback majority, `Resolution` in `result.json`. | AC2 (Resolution part), AC4, AC8 | M-L |
| 4 | Full 7-field validator | `SuggestionValidator` covers catalog, team, assignee, priority, status and comment. | FR8 (supports AC2/AC3), AC8 | M |
| 5 | FR9 signal measurement + docs | Held-out kNN status (and assignee) accuracy vs baselines, recorded in the docs. | AC7, AC8 | S |

Every slice ends with a green build and green `Category!=Integration` tests.

### Slice 1: Real-data I/O

| File | Project | New/Mod | Change |
|---|---|---|---|
| `src/TicketTriage.Core/Domain/Ticket.cs` | Core | Mod | `Key` optional (D1). Remark: verified property names; `Created date` intentionally unmapped. |
| `src/TicketTriage.Core/Domain/TriageResult.cs` | Core | Mod | Predicted fields only (no `Issue key`): `Work type`, `Affected Business or IT Services`, `Service Team(s)`, `Assignee`, `Priority`, `Urgency` (string, `JiraVocabulary`), `Impact` (string), `Resolution` (`ResolutionStatus?`, null until slice 3), `All Comments`. `From(TriageSuggestion)` keeps the blank-comment fallback rule. |
| `src/TicketTriage.Core/Domain/JiraVocabulary.cs` | Core | New | `ToJira(Urgency)`, `ToJira(Impact)`, `TryParseUrgency`, `TryParseImpact` (case-insensitive). The *why* comment cites requirements §7 no. 1. |
| `src/TicketTriage.Batch/ChallengeDocument.cs` | Batch | New | `ReadAsync(path, ct)` → envelope / array detection, per-record `JsonObject` + `Ticket`, positional keys. `ToOutput(IReadOnlyList<TriageResult>)` → `JsonNode` (D3–D5). Errors → `BatchInputException` with index, path and JSON position only (never values): missing or non-array `records`, empty, null or non-object record, record not deserialisable (e.g. no `Summary`). |
| `src/TicketTriage.Batch/BatchRunner.cs` | Batch | Mod | Uses `ChallengeDocument`; writes the output node atomically (unchanged temp + move). Count check unchanged. Duplicate warning for real keys only. |
| `src/TicketTriage.Batch/BatchCommand.cs` | Batch | Mod | Usage text: "challenge file (envelope or array)". |
| `src/TicketTriage.Batch/appsettings.Development.json`, `Properties/launchSettings.json` | Batch | Mod | Real training and challenge file names. |
| `src/TicketTriage.Infrastructure/Import/TrainingDataImporter.cs` | Infrastructure | Mod | Default `Path` = `../../data/jira_first_20000_requested_fields_synthetic.json`. Drop the "verify the file shape" TODO (array verified). Optionally `ChangeTracker.AutoDetectChangesEnabled = false` around the bulk insert (R5). No logic change otherwise: lowercase `priority` / `impact` resolve case-insensitively, `highest` urgency resolves to null (unused, ADR-0001), null `Resolution` → `New`. |
| `src/TicketTriage.AppHost/AppHost.cs`, `appsettings.json` | AppHost | Mod | `Data:TrainingFile` / `Data:ChallengeFile` (defaults = real names) for `TrainingData__Path` and the batch `--input`. |
| `data/README.md`, `README.md`, `docs/features/batch-runner/README.md`, `CLAUDE.md` | docs | Mod | Real file names, input shapes, mirrored output, keyless records / positional identity, corrected enum-name convention (F4), delete `data/triage.db*` before the first real import. |

Tests:
- `tests/TicketTriage.Batch.Tests/ChallengeDocumentTests.cs` (new): envelope → records read, and the output keeps all envelope metadata; plain array in → array out; keyless records get `#1..#n` internally and **no `Issue key` appears in the output**; a record that has `Issue key` keeps it; extra fields (`Request type`, `Severity`, `Linked issues`, …) are preserved in place; prefilled `Assignee` / `Priority` / `Urgency` / `Impact` / `Service Team(s)` are overwritten; missing `records`, `records: []`, `records: null`, non-object element → `BatchInputException` without record text (marker test).
- `BatchRunnerTests.cs` (mod): 20-record keyless envelope (shape copied from the real file, synthetic text) → 20 entries in input order, exit 0 (AC1 in miniature). **Batch-level consistency (AC2):** a fake pipeline cycling through all 25 (urgency, impact) pairs; for every output record, `JiraVocabulary.TryParse*` of `Urgency` / `Impact` gives values whose `PriorityMatrix.Resolve` equals `Priority`.
- `BatchCommandTests.cs` (mod): envelope without `records` → exit 1, nothing written, existing output untouched.
- `tests/TicketTriage.Core.Tests/JiraVocabularyTests.cs` (new): mapping of all values, case-insensitive round-trip.
- `TrainingDataImporterTests.cs` (mod): keyless array in the exact real shape (all 16 fields, lowercase values, null `Resolution`, `Created date` string) → imported count; a second `ImportAsync` returns 0 and the row count is unchanged (AC6). New `[Trait("Category","Integration")]` test: imports the real file when present (`Assert.Skip` otherwise), expects 20 000, then idempotent.
- `FallbackContractTests.cs` (mod): adapt to the reshaped `TriageResult` (key removed).

Manual (AC1 end to end, needs slices 2–4 for full scoring value): see §6.

### Slice 2: Real ServiceCatalog + routing statistics + RoutingResolver

| File | Project | New/Mod | Change |
|---|---|---|---|
| `src/TicketTriage.Core/Domain/ServiceCatalog.cs` | Core | Mod | The 20 names of requirements §6 (14 Critical, 6 Non-Critical). Remove the TODO. |
| `src/TicketTriage.Infrastructure/Routing/RoutingStatistics.cs` | Infrastructure | New | Immutable. `Build(IEnumerable<(string Service, string Team, string? Assignee, int Count)>)`; `Resolve(IReadOnlyList<string> services)` → `RoutingDecision` for the first service (D8); `TryGetRoute(service, out team, out assignee)`; `IsKnownService`. Rows without service or team are ignored; null assignee rows count for the team only. |
| `src/TicketTriage.Infrastructure/Routing/RoutingStatisticsProvider.cs` | Infrastructure | New | `internal interface IRoutingStatisticsSource { ValueTask<RoutingStatistics> GetAsync(ct); }` plus the provider: one grouped query, ids mapped to names through `LookupNamesProvider`, built once (D7). Internal constructor with a `Func<ct, Task<rows>>` for tests. Logs counts and elapsed ms at Information, never names. |
| `src/TicketTriage.Infrastructure/Routing/StatisticsRoutingResolver.cs` | Infrastructure | New | `IRoutingResolver`: `(await source.GetAsync(ct)).Resolve(classification.AffectedServices)`. Ticket text is not used (FR4). |
| `src/TicketTriage.Infrastructure/InfrastructureServiceCollectionExtensions.cs` | Infrastructure | Mod | `TryAddSingleton<IRoutingStatisticsSource, RoutingStatisticsProvider>()`, `TryAddScoped<IRoutingResolver, StatisticsRoutingResolver>()`. |
| `src/TicketTriage.Infrastructure/Stubs/StubRoutingResolver.cs` | Infrastructure | Delete | Replaced. |
| docs: `docs/architecture.md` (§3 routing stats, §4 step 3), `docs/requirements.md` (FR-04, FR-13 implemented), `CLAUDE.md` (stub list, ServiceCatalog gotcha removed, the data fact "service→team 1:1, assignee near-random"), `docs/features/triage-agent/README.md` | docs | Mod | Status updates. |

Tests:
- `tests/TicketTriage.Infrastructure.Tests/RoutingStatisticsTests.cs` (new, pure): majority team; majority assignee within (service, team); team tie → alphabetical; assignee tie → alphabetical, case-insensitive; unknown service → `[]` / null; service lookup case-insensitive; first listed service wins; null-assignee rows ignored.
- `StatisticsRoutingResolverTests.cs` (new, SQLite in-memory via `SqliteTestDatabase.AddTicketAsync(serviceId, teamId, assignee)`): crafted DB → expected team and assignee, **including the tie rule (AC3)**; loader called once for two resolves (NFR3); a cancelled first load is retried.
- `tests/TicketTriage.Core.Tests/ServiceCatalogTests.cs` (mod): exactly the 20 names of requirements §6 (set equality, AC5); `Find` is case-insensitive and returns the canonical name; unknown → null.
- `tests/TicketTriage.Agents.Tests/LlmTicketClassifierTests.cs` (mod, AC5): with `CoreServiceCatalogProvider`, `"trading PLATFORM"` → `"Trading Platform"`; `"Mainframe"` → throws.
- `tests/TicketTriage.Infrastructure.Tests/RegistrationTests.cs` (mod): `IRoutingResolver` resolves to `StatisticsRoutingResolver`.
- `TriageRetryTests.FallbackSuggestionFactoryTests.Classify_FullTie_UsesEnumOrderAndOrdinalName` (mod): compute the expected winner with `string.CompareOrdinal` (F9).

### Slice 3: Resolution status end to end

| File | Project | New/Mod | Change |
|---|---|---|---|
| `src/TicketTriage.Core/Domain/Enums.cs` | Core | Mod | `ResolutionStatus` JSON names `done`, `cancelled`, `clarification`, `cannot reproduce` (D6). Update the header comment. |
| `src/TicketTriage.Core/Domain/TriageModels.cs` | Core | Mod | `public sealed record ResolutionDraft(ResolutionStatus Status, string Comment);` (moved from Agents, without `Language`). |
| `src/TicketTriage.Core/Abstractions/TriageAbstractions.cs` | Core | Mod | `IResolutionDrafter.DraftAsync(Ticket, TicketClassification, RoutingDecision, IReadOnlyList<SimilarTicket>, ct)` → `Task<ResolutionDraft>` (D9). |
| `src/TicketTriage.Core/Domain/TriageResult.cs` | Core | Mod | `Resolution = suggestion.ResolutionStatus`. |
| `src/TicketTriage.Infrastructure/Pipeline/TriagePipeline.cs` | Infrastructure | Mod | Pass `routing` to the drafter; set `ResolutionStatus = draft.Status`, `DraftComment = draft.Comment`. |
| `src/TicketTriage.Infrastructure/Pipeline/FallbackSuggestionFactory.cs` | Infrastructure | Mod | `ResolutionStatus` = majority over the similar tickets' `Resolution` (parsed through the enum converter; reuse `Ranked`; ties by summed score, then enum order), default `Done` (D10). |
| `src/TicketTriage.Infrastructure/Pipeline/SuggestionValidator.cs` | Infrastructure | Mod | Add `InvalidResolutionStatus` (null or undefined). |
| `src/TicketTriage.Infrastructure/Stubs/StubResolutionDrafter.cs` | Infrastructure | Mod | New signature; returns `Done` + TODO text. |
| `src/TicketTriage.Agents/Drafting/LlmResolutionDrafter.cs` | Agents | Mod | Implements the new port only. The DTO status description lists the lowercase vocabulary; parsing stays strict (throws on unknown). `Language` stays in the DTO (prompt compliance) and is logged at Debug. |
| `src/TicketTriage.Agents/Drafting/DraftPrompts.cs` | Agents | Mod | `drafter-v2`: status definitions in the lowercase vocabulary; voice = `routing.Assignee` (neutral when null); remove `InferAssignee`. Guidance on status evidence depends on the slice 5 outcome (if no signal: decide from the ticket text, do not copy similar statuses). |
| `src/TicketTriage.Agents/Prompting/TicketPromptFormatter.cs` | Agents | Mod | Similar tickets: label `Resolution status:` instead of `Resolution:`. Drafter-only helper renders one `Resolution note:` per similar ticket: the last comment whose body (after the `"<email>: "` prefix) starts with `Resolution:`; `Resolution recorded:` and `Problem fixed.` are excluded (F5). |
| `src/TicketTriage.Agents/Drafting/ResolutionDraft.cs` | Agents | Delete | `IResolutionDraftAgent` and the Agents `ResolutionDraft` are gone. |
| `src/TicketTriage.Agents/AgentsServiceCollectionExtensions.cs` | Agents | Mod | Drop the `IResolutionDraftAgent` registration. |
| docs: `docs/architecture.md` §4/§4.1, `docs/requirements.md` (FR-16, resolution status paragraph), `docs/features/triage-agent/README.md`, `docs/features/triage-pipeline/README.md` (open points 4 and 6 resolved), `docs/features/batch-runner/README.md` | docs | Mod | Status now implemented. |

Tests:
- `tests/TicketTriage.Agents.Tests/LlmResolutionDrafterTests.cs` (mod): the port returns status + comment; lowercase and capitalised model output both parse; `"Fixed"` throws; the routed assignee's name is in the user message; no persona when the assignee is null; the resolution note is rendered and the template comments are not; injection text stays out of the instructions.
- `tests/TicketTriage.Agents.Tests/ResolutionStatusFallbackTests.cs` (new, **AC4**): DI with `AddTriageInfrastructure` (temp-file SQLite, `RetryCount=2`, delay 0) + `AddTriageAgents`. Replace `IChatClient` with `FakeChatClient(ValidClassification, draftWithStatus "fixed")` and `ISimilarTicketSource` with a fixed source (statuses clarification, clarification, done). Expect the suggestion to be a fallback with `ResolutionStatus == Clarification` and the failure reasons `Draft:Exception`. Second case: no similar tickets → `Done`.
- `TriageRetryTests` / `FallbackSuggestionFactoryTests` (mod): status majority, tie by score, then enum order, unparsable values ignored, default `Done`. Validator: `InvalidResolutionStatus`.
- `TriagePipelineTests` (mod): the status from the drafter lands in the suggestion (replaces `ResolutionStatus == null`); the drafter receives the routing decision.
- `tests/TicketTriage.Infrastructure.Tests/TestSupport.cs`, `Agents.Tests/TestSupport.cs`, `Agents.Tests/RegistrationTests.cs` (mod): new drafter signature; `IResolutionDraftAgent` assertion removed.
- `tests/TicketTriage.Batch.Tests/BatchRunnerTests.cs` (mod, **AC2**): every output record has the seven fields; `Resolution` is one of the four lowercase values; fallback records also carry a status.

### Slice 4: Full 7-field validator

| File | Project | New/Mod | Change |
|---|---|---|---|
| `src/TicketTriage.Infrastructure/Pipeline/SuggestionValidator.cs` | Infrastructure | Mod | `Validate(TriageSuggestion, RoutingStatistics)`. Codes: `InvalidWorkType`; `NoAffectedServices`; **`UnknownService`** (name is not the canonical `ServiceCatalog` name); **`MissingTeam` / `InconsistentTeam`** (first service known to the statistics → teams must equal `[statsTeam]`; unknown → teams must be empty); **`MissingAssignee` / `InconsistentAssignee`** (known → equals the statistics assignee; unknown → null); `InvalidUrgency`, `InvalidImpact`, **`PriorityMismatch`** (defensive `Priority != PriorityMatrix.Resolve`); `InvalidResolutionStatus`; `EmptyComment`. |
| `src/TicketTriage.Infrastructure/Pipeline/TriagePipeline.cs` | Infrastructure | Mod | Constructor gets `IRoutingStatisticsSource`; the `Validate` step awaits the snapshot (D11). |
| docs: `docs/requirements.md` (FR-33 implemented), `docs/architecture.md` (§4 step 5, §4.1 note), `docs/features/triage-pipeline/README.md` (validation table) | docs | Mod | |

Tests:
- `TriageRetryTests` (validator section, mod): one test per new code; a valid suggestion passes; unknown service with empty routing passes; unknown service with a team fails.
- `tests/TicketTriage.Infrastructure.Tests/TestSupport.cs` (mod): `FakeClassifier` / `ScriptedClassifier` use a catalog service; `FakeRouter` returns the fake statistics' route; new `FakeRoutingStatisticsSource`. Update the 6 `new TriagePipeline(...)` sites (`TriagePipelineTests` ×2, `TriageRetryTests` ×3, `DbSimilarTicketSourceTests` ×1). `StopSystemOnFailureTests` / `TicketNormalizerTests` `"Email"` → catalog name where they reach the validator.
- `TriagePipelineTests` (mod): a router returning a team that disagrees with the statistics → retry → fallback with consistent routing.
- The existing `PipelineTypes_DoNotDependOnEntityFrameworkCore` test must stay green.

### Slice 5: FR9 signal measurement + feature docs

| File | Project | New/Mod | Change |
|---|---|---|---|
| `tests/TicketTriage.Infrastructure.Tests/SignalMeasurementTests.cs` | Tests | New | `[Trait("Category","Integration")]`, skipped when the training file is missing (path from env `TRIAGE_TRAINING_FILE`, else walk up to `TicketTriage.slnx` + `data/…`). Loads with the `Ticket` contract (needs slice 1). Deterministic split: resolved tickets, `index % 5 == 0` held out. Builds `TfIdfIndex` over the train descriptions. Reports: **status**: kNN top-10 majority accuracy vs uniform 25%, the majority-class baseline and the work-type-majority baseline, with n and a 95% CI. **Assignee** (informs R3): service-majority (what FR5 does) and kNN-majority vs 1/30. Output through `TestContext.Current.TestOutputHelper`; only sanity assertions. |
| `docs/features/score-completeness/README.md` | docs | New | Feature doc (how it works, config, tests, limitations) + **FR9 table with the measured numbers** (AC7) + the data facts of §1 (F4–F7). |
| `CLAUDE.md` | docs | Mod | If there is no signal: a convention line like the one for priority ("resolution status and assignee in training are ~random; status comes from LLM semantics, assignee from majority vote ≈ chance"). |

Pre-measurement (in-sample, this plan): per-description majority status 29.2% (noise ceiling) and per-service assignee majority 4.6%. Expect a held-out result of about 25% (no signal).

## 5. Schema changes

**None.** No entity, column, table or seed change: routing statistics are in memory (D7), the resolution status is not persisted, and the `Resolution` lookup seed stays as is (unused). `EnsureCreated` stays.

Operational: **delete `data/triage.db*` once** before the first real run. The existing 49 KB DB was created without training data, and the importer skips as soon as any ticket exists. Development startup (Web or Batch) then recreates the schema and imports the real 20 000-ticket file.

## 6. Build and test

```bash
dotnet build TicketTriage.slnx
dotnet test --solution TicketTriage.slnx --filter "Category!=Integration"          # AC8, after every slice
dotnet format TicketTriage.slnx --verify-no-changes --include <changed files>      # format only touched files (CLAUDE.md gotcha)

# AC6 / AC7 (real data, local only)
dotnet test --project tests/TicketTriage.Infrastructure.Tests --filter "Category=Integration"
```

AC1 smoke run (PowerShell, real provider configured, after slices 1–4):

```powershell
Remove-Item ..\..\data\triage.db* -ErrorAction SilentlyContinue   # from src/TicketTriage.Batch; or delete data/triage.db* at the repo root
$env:Triage__StopSystemOnFailure="false"
$env:DOTNET_ENVIRONMENT="Development"          # DB init + import only in Development
dotnet run --project src/TicketTriage.Batch -- --input ../../data/jira_hackathon_blind_eval_challenge_20260923083915-1141.json --output ../../data/result.json
```

Relative paths resolve against `src/TicketTriage.Batch` (batch-runner R2), so AC1's literal `data/result.json` means `../../data/result.json`. Check: exit 0, 20 records in input order, no `Issue key`, every record has the seven fields, `Resolution` is in the vocabulary, `Priority` matches the matrix. Record duration per ticket (NFR2) and the first-start import time.

## 7. Dependencies and recommended order

| Slice | Depends on | Why |
|---|---|---|
| 1 | – | Unblocks everything: without the `Ticket.Key` fix neither real file loads. |
| 2 | – (independent of 1) | Catalog and routing touch different files. |
| 5 | 1 | Needs keyless `Ticket` deserialization. Cheap; its outcome steers the drafter prompt of slice 3. |
| 3 | 1 (TriageResult shape), 2 (routed assignee for the voice; works with the stub but is meaningless) | |
| 4 | 2 (statistics source), 3 (status) | |

**Recommended order: 1 → 2 → 5 → 3 → 4.** Slices 1 and 2 can be built in parallel, but both edit `CLAUDE.md` and architecture docs, so sequential is simpler.

## 8. Requirement ↔ code/data conflicts and open points

| # | Conflict | Resolution in this plan |
|---|---|---|
| C1 | FR1 / FR3 vs `Ticket.Key required` (F1). | D1 / D2. |
| C2 | FR6 / AC2 vocabulary (`done`…`cannot reproduce`) vs `ResolutionStatus` JSON names (`Done`, `Cannot Reproduce`). | D6, slice 3. |
| C3 | FR2 writes `Urgency` / `Impact`, but the Core enum names (`Critical`, `Major`, `No Impact`) are not the data vocabulary (`Highest`…`Lowest`). CLAUDE.md's enum-name convention is wrong for the real export. | `JiraVocabulary` (assumption requirements §7 no. 1, Critical = Highest); CLAUDE.md corrected. **Organizers to confirm** the casing (challenge: `Highest`; training: `highest`). |
| C4 | Architecture / FR-16 speak of "cleaned resolution templates"; the training `Resolution` field is only the status, and the texts are in `All Comments` (F5). | Slice 3 minimal note extraction; full FR-02 stays a follow-up. |
| C5 | FR2 "no key invented" vs `TriageResult."Issue key"`. | Reshape (slice 1). |
| C6 | FR2 `All Comments` = drafted comment drops the reporter's original comments; requirements §7 no. 2 is open. | D5 (replace). Alternative: append `"<assignee>: <comment>"` (one line in `TriageResult.From`). |
| C7 | FR2 "mirror records" vs the envelope's metadata. | D3 (mirror the container). Alternative: always write a plain array. |
| C8 | AC2 "each record carries all seven fields" vs the existing fallback contract (blank comment = fallback, pinned by `FallbackContractTests`). | Kept: fallback records have status, routing and priority, but `All Comments: []`. The degraded case is accepted per Edge Cases. |
| C9 | FR8 "team consistent with the resolver output" is tautological while the resolver is the only source. | Implemented as consistency with the statistics snapshot (guards regressions and future resolvers). |
| C10 | FR4 "(service, team) → assignee" is specified, but the data shows the assignee is ~random (F6). §7 no. 4 is answered: Business Entity / Work type do not help. | Implemented as specified; expected field-4 accuracy is about 5%, documented in slice 5. |
| C11 | README / AppHost / launchSettings use `training.json` / `challenge.json`. | D13. |

Not in the requirements, noted for the user: challenge records carry `Request type` (e.g. `Nonsense / Unclear Input`, `Misclassified Incident Title`, `Access Removal`), a strong hint for work type and status. Adding it to `Ticket` and rendering it as an *unverified hint* in both prompts is a small, generalising change (not hardcoding) that could matter a lot for field 6. Recommend deciding before slice 3.

## 9. Risks

| ID | Risk | Mitigation |
|---|---|---|
| R1 | The output schema (container, casing of `Urgency` / `Impact` / `Resolution`, `All Comments` semantics) is not confirmed by the organizers. | All in `TriageResult` + `JiraVocabulary` + `ChallengeDocument.ToOutput`; one place each. |
| R2 | The `IResolutionDrafter` signature change ripples into Infrastructure / Agents fakes, the stub, registration tests. | Done in one slice (3); the compiler finds every site. |
| R3 | Assignee and status have near-zero learnable signal (F6, F7). Scores on fields 4 and 6 stay low whatever the pipeline does. | Measured and documented (slice 5); status relies on LLM semantics; see the `Request type` note. |
| R4 | Validator catalog / team checks (slice 4) break tests that use `"Email"` or `"Team A"`. | Listed explicitly in slice 4. |
| R5 | First import of 20k tickets + ~55k comments in one `SaveChanges` may take tens of seconds (Web and Batch startup). | Disable `AutoDetectChanges` for the bulk add, measure in the smoke run; batching only if needed. |
| R6 | Retrieval returns 10 copies of one description template (173 distinct descriptions), so there is little diversity for the drafter and the fallback. | Accepted; dedupe by description is a follow-up. |
| R7 | Renaming the catalog changes ordinal tie-breaks in the fallback tests (F9). | Fix the test in slice 2. |
| R8 | With temperature 0, an invalid status is likely repeated on every retry, so the whole ticket falls back and the LLM classification + comment are lost. | Structured output makes this rare. If the smoke run shows it, switch D10 to a status-only substitution (a small change in the pipeline). |
| R9 | `TicketNormalizer` flags the challenge's `Highest` urgency / `High` impact as unknown (log noise only; hints are nulled anyway). | Optional: parse through `JiraVocabulary` in the normalizer. |
| R10 | `data/triage.db` from earlier runs blocks the real import. | §5: delete `data/triage.db*`; documented in CLAUDE.md and data/README. |
| R11 | `StopSystemOnFailure=true` (Batch default) aborts scoring runs. | Unchanged guidance: set `Triage__StopSystemOnFailure=false`. |
