# TicketTriage (TriAIge)

Swiss {ai} Weeks hackathon (Swiss Life challenge): triage IT service-desk tickets (Jira export) with AI agents. Historical tickets (`data/jira_first_20000_requested_fields_synthetic.json`, JSON array, ~20k, no `Issue key`) are imported into SQLite and used as knowledge; the 20 challenge tickets (`data/jira_hackathon_blind_eval_challenge_20260923083915-1141.json`, envelope with a `records` array) are triaged by `TicketTriage.Batch`, which writes `data/result.json` for scoring. `TicketTriage.Web` is the Blazor Server UI where analysts browse tickets and review (approve / edit / reject) AI suggestions.

> Keep this file under ~200 lines. Deep technology knowledge lives in `.claude/skills/*`, not here.
> When you learn something non-obvious (gotcha, convention, changed command), update this file or the matching skill in the same PR.

**Read first:** [docs/requirements.md](docs/requirements.md) (FR/NFR IDs, scoring, priority matrix, open questions) · [docs/architecture.md](docs/architecture.md) (diagrams, **component status: implemented vs planned**) · [docs/adr/](docs/adr/) (decisions). Feature work goes to `docs/features/<feature>/`.

## Stack

| Concern | Choice |
|---|---|
| Runtime | .NET 10 (LTS, SDK pinned in `global.json`), C# 14, nullable on, **warnings are errors** |
| UI | Blazor Web App, **global Interactive Server** render mode, MudBlazor |
| Orchestration | Aspire 13 (`Aspire.AppHost.Sdk`), ServiceDefaults for OpenTelemetry / health / resilience |
| Persistence | SQLite + EF Core 10 (`CommunityToolkit.Aspire.Hosting.Sqlite` in the AppHost), `dotnet-ef` as local tool |
| AI | Microsoft Agent Framework 1.x on `Microsoft.Extensions.AI` `IChatClient`; provider **Azure OpenAI**, **OpenAI**, **Apertus** (Swisscom, Swiss AI Weeks) or local **Ollama** |
| Tests | xUnit v3 on **Microsoft.Testing.Platform**, FluentAssertions (add bUnit / NSubstitute / `Aspire.Hosting.Testing` when needed) |
| Packages | Central Package Management — versions only in `Directory.Packages.props` |

## Solution layout

```
TicketTriage.slnx · global.json · dotnet-tools.json · Directory.Build.props · Directory.Packages.props · .editorconfig
data/                         local-only inputs/outputs (gitignored, see data/README.md)
src/
  TicketTriage.Core/            Domain — NO references. Domain/ (Ticket, enums, PriorityMatrix, ServiceCatalog,
                                TriageSuggestion, …), Abstractions/ (pipeline ports)
  TicketTriage.Infrastructure/  → Core. Persistence/ (TriageDbContext, entities; no Migrations/), Import/ (training file),
                                Challenge/ (ChallengeDocument: stream/JSON parse + mirrored output, ChallengeResults, shared by Batch and Web),
                                Pipeline/ (TriagePipeline), Retrieval/ (TF-IDF), Sources/ (DbTicketSource, mapper),
                                Routing/ (statistics, resolver), Stubs/ (no routing stub any more), DatabaseInitializer
  TicketTriage.Agents/          → Infrastructure. Llm/ (LlmOptions, ChatClientFactory), Classification/, Drafting/, Prompting/,
                                Services/ (service catalog abstraction), TriageAgent, Health/
  TicketTriage.Web/             → Agents, ServiceDefaults. Components/Pages: Home, Tickets, Review, Upload (`/upload`: upload Jira JSON →
                                ingest → AnalysisWorker → download result.json; service in Upload/, [docs](docs/features/upload-frontend/README.md))
  TicketTriage.Batch/           → Agents, Infrastructure. challenge file → pipeline → result.json (mirrored input)
  TicketTriage.ServiceDefaults/ OTel (incl. M.E.AI + Agent Framework sources), health checks (ReadyTag), resilience
  TicketTriage.AppHost/         wires triage-db, web, batch, LLM parameters
tests/
  TicketTriage.Core.Tests/      xUnit v3 + FluentAssertions
  TicketTriage.Infrastructure.Tests/  pipeline, failure store, retrieval, sources, importer (SQLite in-memory)
  TicketTriage.Agents.Tests/    classifier / drafter with a fake IChatClient; live smoke tests are `Category=Integration`
  TicketTriage.Batch.Tests/     BatchRunner / BatchCommand with a fake ITriagePipeline (temp files, no LLM, no DB)
  TicketTriage.Web.Tests/       ChallengeUploadService with fake ingestor/monitor (no bUnit yet, Upload.razor is checked manually)
```

Dependency direction: `Core ← Infrastructure ← Agents ← {Web, Batch}`. Never reference outward.

## Triage pipeline (Core ports → implementations)

`ITriagePipeline` is stream-based (`TriageAsync(IAsyncEnumerable<Ticket>)` → one suggestion per ticket, sequential, in order; input from `ITicketSource` = `DbTicketSource`, streams `New` tickets, registered but no caller yet) and implemented in `Infrastructure/Pipeline` (normalize, timeout, retry, validation of all 7 fields incl. routing consistency with `RoutingStatistics`, fallback (routing from the same statistics), failure log via `ITriageFailureStore`; details in `docs/features/triage-pipeline/README.md`). Per ticket: **retrieve similar** (`ISimilarTicketSource`) → **classify** (`ITicketClassifier`: work type, affected services, urgency, impact) → **route** (`IRoutingResolver`: service teams, assignee) → **prioritize** (`PriorityMatrix`, deterministic) → **draft** (`IResolutionDrafter`). Result: `TriageSuggestion`, reviewed by a human (`ReviewDecision`). The pipeline only **analyses**. **Planned, not implemented yet** (ADR-0002): `ITicketIngestor` (saves tickets as `New`), a `BackgroundService` worker in Web that pre-computes suggestions, and `IReviewService` (persists decisions). Opening a ticket never calls the LLM. Batch calls the pipeline directly **only transitionally**; the target (decided 2026-09-25) is challenge tickets → ingest → worker → `result.json` exported from the stored suggestions, so they also show up in the Web UI ([ADR-0002](docs/adr/0002-background-analysis-worker.md), architecture §5.4). Today `BatchRunner` reads the challenge file (envelope or array, records keyless, content-hash keys internally), streams it through the pipeline and writes `result.json` atomically as a mirror of the input with the predicted fields filled and no `Issue key` added (exit codes 0/1/2, console summary; fallback = blank `DraftComment`; [batch-runner](docs/features/batch-runner/README.md)). Scoring runs need `Triage__StopSystemOnFailure=false` (Batch `appsettings.json` default is `true`).

Implemented ports: `ISimilarTicketSource` = `DbSimilarTicketSource` (in-memory TF-IDF + cosine over `Description` only, index built once per process, self-exclusion by `Id`, keys `DB-{Id}`; [similar-ticket-retrieval](docs/features/similar-ticket-retrieval/README.md)); `ITicketClassifier` = `LlmTicketClassifier` and `IResolutionDrafter` = `LlmResolutionDrafter` in Agents (returns `ResolutionDraft(Status, Comment)`, gets the `RoutingDecision`; unknown status throws, fallback status = similar-ticket majority else `done`; [triage-agent](docs/features/triage-agent/README.md)). `IRoutingResolver` = `StatisticsRoutingResolver` (`Infrastructure/Routing`: majority vote per service from one in-memory GROUP BY, built once per process; ties alphabetical; unknown service -> no team, no assignee; registered with `TryAdd*`, Agents does not override it). Data fact: service -> team is 1:1 in all 20 services, the assignee is near-random (top one ~5% per service, ~30 assignees), so expect team accuracy = service accuracy and assignee accuracy near chance. Replacing a stub = implement the port, register it explicitly, keep a deterministic fallback. Status per component: [architecture.md](docs/architecture.md).

## Commands

```bash
dotnet tool restore                                   # once: dotnet-ef
aspire run                                            # or: dotnet run --project src/TicketTriage.AppHost
dotnet build TicketTriage.slnx
dotnet test --solution TicketTriage.slnx              # MTP runner: --solution/--project, not a positional path
dotnet test --solution TicketTriage.slnx --filter "Category!=Integration"
dotnet format TicketTriage.slnx --verify-no-changes
```

There are **no EF migrations** (see Gotchas and the `sqlite-efcore` skill). In Development, Web creates the schema (`EnsureCreatedAsync`) and imports the training file on startup (`InitializeTriageDatabaseAsync`, idempotent).

## Configuration & secrets

LLM config is the `Llm` section (`LlmOptions`): `Provider` = `AzureOpenAI` (default) | `OpenAI` | `Apertus` | `Ollama`, with per-provider sub-sections (`AzureOpenAI:{Endpoint,Deployment,ApiKey}`, `OpenAI:{ApiKey,Model}`, `Apertus:{Endpoint,ApiKey,Model}`, `Ollama:{Endpoint,Model}`; full table in `README.md`). Missing config does **not** crash the app — `UnconfiguredChatClient` + the `ready` health check report it.

Keys never go into `appsettings*.json` or code. They are AppHost parameters in the AppHost's user secrets, mapped to `Llm__*` env vars by `WithLlmConfiguration` in `AppHost.cs` (full table in `README.md`):

```bash
dotnet user-secrets set "Parameters:azure-openai-endpoint"   "https://<resource>.openai.azure.com/" --project src/TicketTriage.AppHost
dotnet user-secrets set "Parameters:azure-openai-deployment" "<deployment>"                         --project src/TicketTriage.AppHost
dotnet user-secrets set "Parameters:azure-openai-apikey"     "<key>"                                --project src/TicketTriage.AppHost
```

Pipeline behaviour is the `Triage` section (`TriageOptions`, in Web/Batch `appsettings.json`): `RetryCount` (3), `StopSystemOnFailure` (false), `TicketTimeoutSeconds` (60), `SimilarTicketCount` (10), `RetryDelayMilliseconds` (500). Invalid values fail startup.

## Conventions

- **Delete `data/triage.db*` once** before the first run with the real training file (importer skips when any ticket exists).
- **Training data is noisy**: priority / urgency / impact in the training file are **random** — never use them as labels, few-shot examples or statistics. Team + assignee come from routing statistics, not the LLM (FR-13). See ADR-0001. **Resolution status and assignee are also ~random** (held-out kNN status 25.3% vs 25% chance, assignee 3.5% vs 3.3%; [score-completeness](docs/features/score-completeness/README.md)): status comes from LLM semantics of the ticket text, never from similar-ticket statuses as evidence; assignee is a majority vote (≈ chance).
- **Core decides, agents suggest**: business rules live in Core. The LLM predicts urgency + impact; **priority is always `PriorityMatrix.Resolve(urgency, impact)`**, never model output. Service names are validated against `ServiceCatalog`.
- **Structured output** (`RunAsync<T>`) mapped onto Core records (`TicketClassification`, `RoutingDecision`) with validation + fallback — never regex-parse model text.
- **Ticket text is untrusted** (prompt injection): user message only, never inside instructions. Never render model output as unsanitised `MarkupString`. Don't log ticket bodies/prompts at Information (personal data).
- **Async everywhere** with `CancellationToken`. Never `.Result` / `.Wait()`.
- **Blazor**: `IDbContextFactory<TriageDbContext>` per operation; no LLM calls during prerender; `InvokeAsync(StateHasChanged)` from callbacks.
- Core enum JSON names (`"Service Request"`, `"No Impact"`) do **not** all match the real export: it uses `highest/high/medium/low/lowest` for urgency and impact. Output uses `JiraVocabulary` for urgency/impact; never hand-map strings elsewhere. `ResolutionStatus` JSON names are the export's lowercase `done`, `cancelled`, `clarification`, `cannot reproduce`.
- Real files carry no `Issue key`: `Ticket.Key` is optional, `ChallengeDocument` assigns a content key (`#` + 16 hex of the record's SHA-256), DB tickets are `DB-{Id}`. `result.json` mirrors the input records and never adds a key.
- `record` for DTOs/value objects, file-scoped namespaces, primary constructors, collection expressions, `_camelCase` private fields.
- Comments explain *why*, never *what*. `// TODO: implement - …` marks known stubs.

## Gotchas

- `TreatWarningsAsErrors` — a new analyzer warning breaks the build. Fix the cause; don't blanket-suppress.
- Blazor prerender runs `OnInitializedAsync` **twice** — use `[PersistentState]` or `OnAfterRenderAsync(firstRender)` for expensive/LLM work.
- SQLite can't `ORDER BY` `DateTimeOffset`/`decimal` natively — existing `DateTimeOffset` columns are stored as sortable 64-bit integers via a converter; new ones need the same. Single writer — keep transactions short.
- The schema is created by `EnsureCreatedAsync` — **there are no migrations** (by decision; `sqlite-efcore` skill explains how to reintroduce them; then `**/Migrations/*.cs` is generated and must not be hand-edited, the `protect-files` hook blocks it). It never alters an existing DB, so **delete `data/triage.db*` after any schema change or seed change** (e.g. `Ticket.Retries`, table `TriageFailure`, status seed `New/Reviewing/Reviewed/HumanRejected/HumanApproved`), else "no such column/table" or an importer failure on the old `Finished` status.
- Imported training tickets **without a `Resolution` get status `New`**, the same status as future intake. A worker on `DbTicketSource` would re-triage history — add a filter before wiring it (planned: a source/batch marker on `Ticket`, also used for the challenge tickets; ADR-0002).
- A suggestion is a **fallback** when its `DraftComment` is null, empty or whitespace (the validator rejects blank comments on every success path). Batch counts fallbacks that way and `TriageResult.From` writes no comment for them; a contract test pins it against the real pipeline.
- A solution-wide `dotnet format TicketTriage.slnx` (without `--include`) once rewrote CRLF line endings in ~20 unrelated files. After a slice, format only the files you changed (`dotnet format TicketTriage.slnx --include <files>`).
- `Triage:StopSystemOnFailure` stops the host on a ticket's first failure and the pipeline throws `OperationCanceledException`. Dev/Batch only; never in production Web.
- `PriorityMatrix` depends on the **declaration order** of `Urgency` and `Impact` — never reorder those enums.
- Agent Framework 1.x renamed preview APIs (`AgentThread` → `AgentSession`, `CreateAIAgent` → `AsAIAgent`) — old samples won't compile.
- `TriageAgent` is a **keyed** singleton: inject `[FromKeyedServices(TriageAgent.Name)] AIAgent`.
- Resource name `triage-db` = connection string name (`InfrastructureServiceCollectionExtensions.ConnectionStringName`).
- `result.json` shape is dictated by the organizers — don't change it without checking the challenge spec. Batch and the Web upload page both write it through `ChallengeDocument.WriteAsync`; keep it the only writer. Input cap: 10 MB / 500 records.
- Keyless challenge records are keyed by **content hash**, not position (positional `#n` let a different file overwrite earlier tickets). Same file → `Unchanged`; identical records share one row. Never key them by position again; `PositionalKey` is display only (architecture §5, "Challenge ticket identity").
- MudBlazor 9.10 `MudFileUpload` has no `ActivatorContent`; use `CustomContent` with a button calling `picker.OpenFilePickerAsync`.

## Claude Code setup (shared, committed)

| Path | What |
|---|---|
| `.claude/settings.json` | Team permissions, hooks, enabled MCP servers |
| `.claude/settings.local.json` · `CLAUDE.local.md` | **Your** personal overrides / notes (gitignored) |
| `.claude/agents/` | `implementer`, `build-fixer`, `code-reviewer`, `security-reviewer`, `test-writer`, `docs-writer` |
| `.claude/skills/` | Stack: `blazor-server`, `dotnet10`, `sqlite-efcore`, `agent-framework`, `aspire` · Workflow: `grill-me`, `orchestrate` |
| `.claude/hooks/` | `protect-files.sh` (blocks secrets, db, `data/*.json`, generated files, bin/obj), `format-csharp.sh` (formats edited `.cs`) |
| `.mcp.json` | `microsoft-learn` (official .NET/Azure docs), `aspire` (live resources, logs, traces — needs Aspire CLI) |

Workflow: `/grill-me <idea>` → `/orchestrate <feature>` (or `/orchestrate <feature> fast`). Small changes: just ask. Built-ins worth using: `/code-review`, `/security-review`, `/init` (don't overwrite this file).
