# TicketTriage (TriAIge)

Swiss {ai} Weeks hackathon (Swiss Life challenge): triage IT service-desk tickets (Jira export) with AI agents. Historical tickets (`data/training.json`, ~20k) are imported into SQLite and used as knowledge; the 20 challenge tickets (`data/challenge.json`) are triaged by `TicketTriage.Batch`, which writes `data/result.json` for scoring. `TicketTriage.Web` is the Blazor Server UI where analysts browse tickets and review (approve / edit / reject) AI suggestions.

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
  TicketTriage.Infrastructure/  → Core. Persistence/ (TriageDbContext, entities; no Migrations/), Import/ (training.json),
                                Pipeline/ (TriagePipeline), Retrieval/ (TF-IDF), Sources/ (DbTicketSource, mapper),
                                Stubs/ (remaining placeholders: routing), DatabaseInitializer
  TicketTriage.Agents/          → Infrastructure. Llm/ (LlmOptions, ChatClientFactory), Classification/, Drafting/, Prompting/,
                                Services/ (service catalog abstraction), TriageAgent, Health/
  TicketTriage.Web/             → Agents, ServiceDefaults. Components/Pages: Home, Tickets, Review
  TicketTriage.Batch/           → Agents, Infrastructure. challenge.json → pipeline → result.json
  TicketTriage.ServiceDefaults/ OTel (incl. M.E.AI + Agent Framework sources), health checks (ReadyTag), resilience
  TicketTriage.AppHost/         wires triage-db, web, batch, LLM parameters
tests/
  TicketTriage.Core.Tests/      xUnit v3 + FluentAssertions
  TicketTriage.Infrastructure.Tests/  pipeline, failure store, retrieval, sources, importer (SQLite in-memory)
  TicketTriage.Agents.Tests/    classifier / drafter with a fake IChatClient; live smoke tests are `Category=Integration`
  TicketTriage.Batch.Tests/     BatchRunner / BatchCommand with a fake ITriagePipeline (temp files, no LLM, no DB)
```

Dependency direction: `Core ← Infrastructure ← Agents ← {Web, Batch}`. Never reference outward.

## Triage pipeline (Core ports → implementations)

`ITriagePipeline` is stream-based (`TriageAsync(IAsyncEnumerable<Ticket>)` → one suggestion per ticket, sequential, in order; input from `ITicketSource` = `DbTicketSource`, streams `New` tickets, registered but no caller yet) and implemented in `Infrastructure/Pipeline` (normalize, timeout, retry, validation, fallback, failure log via `ITriageFailureStore`; details in `docs/features/triage-pipeline/README.md`). Per ticket: **retrieve similar** (`ISimilarTicketSource`) → **classify** (`ITicketClassifier`: work type, affected services, urgency, impact) → **route** (`IRoutingResolver`: service teams, assignee) → **prioritize** (`PriorityMatrix`, deterministic) → **draft** (`IResolutionDrafter`). Result: `TriageSuggestion`, reviewed by a human (`ReviewDecision`). The pipeline only **analyses**. **Planned, not implemented yet** (ADR-0002): `ITicketIngestor` (saves tickets as `New`), a `BackgroundService` worker in Web that pre-computes suggestions, and `IReviewService` (persists decisions). Opening a ticket never calls the LLM. Batch calls the pipeline directly **only transitionally**; the target (decided 2026-09-25) is challenge tickets → ingest → worker → `result.json` exported from the stored suggestions, so they also show up in the Web UI ([ADR-0002](docs/adr/0002-background-analysis-worker.md), architecture §5.4). Today `BatchRunner` reads `challenge.json`, streams it through the pipeline and writes `result.json` atomically (exit codes 0/1/2, console summary; fallback = blank `DraftComment`; [batch-runner](docs/features/batch-runner/README.md)). Scoring runs need `Triage__StopSystemOnFailure=false` (Batch `appsettings.json` default is `true`).

Implemented ports: `ISimilarTicketSource` = `DbSimilarTicketSource` (in-memory TF-IDF + cosine over `Description` only, index built once per process, self-exclusion by `Id`, keys `DB-{Id}`; [similar-ticket-retrieval](docs/features/similar-ticket-retrieval/README.md)); `ITicketClassifier` = `LlmTicketClassifier` and `IResolutionDrafter` = `LlmResolutionDrafter` in Agents ([triage-agent](docs/features/triage-agent/README.md)). Still a stub: `IRoutingResolver` (`Infrastructure/Stubs/StubRoutingResolver`, registered with `TryAdd*`; Agents registers with `Add*` after Infrastructure and overrides). Replacing a stub = implement the port, register it explicitly, keep a deterministic fallback. Status per component: [architecture.md](docs/architecture.md).

## Commands

```bash
dotnet tool restore                                   # once: dotnet-ef
aspire run                                            # or: dotnet run --project src/TicketTriage.AppHost
dotnet build TicketTriage.slnx
dotnet test --solution TicketTriage.slnx              # MTP runner: --solution/--project, not a positional path
dotnet test --solution TicketTriage.slnx --filter "Category!=Integration"
dotnet format TicketTriage.slnx --verify-no-changes
```

There are **no EF migrations** (see Gotchas and the `sqlite-efcore` skill). In Development, Web creates the schema (`EnsureCreatedAsync`) and imports `training.json` on startup (`InitializeTriageDatabaseAsync`, idempotent).

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

- **Training data is noisy**: priority / urgency / impact in `training.json` are **random** — never use them as labels, few-shot examples or statistics. Team + assignee come from routing statistics, not the LLM (FR-13). See ADR-0001.
- **Core decides, agents suggest**: business rules live in Core. The LLM predicts urgency + impact; **priority is always `PriorityMatrix.Resolve(urgency, impact)`**, never model output. Service names are validated against `ServiceCatalog`.
- **Structured output** (`RunAsync<T>`) mapped onto Core records (`TicketClassification`, `RoutingDecision`) with validation + fallback — never regex-parse model text.
- **Ticket text is untrusted** (prompt injection): user message only, never inside instructions. Never render model output as unsanitised `MarkupString`. Don't log ticket bodies/prompts at Information (personal data).
- **Async everywhere** with `CancellationToken`. Never `.Result` / `.Wait()`.
- **Blazor**: `IDbContextFactory<TriageDbContext>` per operation; no LLM calls during prerender; `InvokeAsync(StateHasChanged)` from callbacks.
- Enum JSON names follow the Jira export (`"Service Request"`, `"No Impact"`) — use the Core enums' converters, don't hand-map strings.
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
- `ServiceCatalog` still holds `TODO …` placeholder names. The real 20 names are in `docs/requirements.md` §6 and the DB `AffectedBusinessOrITServices` seed.
- `Triage:StopSystemOnFailure` stops the host on a ticket's first failure and the pipeline throws `OperationCanceledException`. Dev/Batch only; never in production Web.
- `PriorityMatrix` depends on the **declaration order** of `Urgency` and `Impact` — never reorder those enums.
- Agent Framework 1.x renamed preview APIs (`AgentThread` → `AgentSession`, `CreateAIAgent` → `AsAIAgent`) — old samples won't compile.
- `TriageAgent` is a **keyed** singleton: inject `[FromKeyedServices(TriageAgent.Name)] AIAgent`.
- Resource name `triage-db` = connection string name (`InfrastructureServiceCollectionExtensions.ConnectionStringName`).
- `result.json` shape is dictated by the organizers — don't change it without checking the challenge spec.

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
