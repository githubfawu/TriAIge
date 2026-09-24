# TicketTriage (TriAIge)

AI hackathon project: triage IT service-desk tickets (insurance company, Jira export) with AI agents. Historical tickets (`data/training.json`, ~20k) are imported into SQLite and used as knowledge; the 20 challenge tickets (`data/challenge.json`) are triaged by `TicketTriage.Batch`, which writes `data/result.json` for scoring. `TicketTriage.Web` is the Blazor Server UI where analysts browse tickets and review (approve / edit / reject) AI suggestions.

> Keep this file under ~200 lines. Deep technology knowledge lives in `.claude/skills/*`, not here.
> When you learn something non-obvious (gotcha, convention, changed command), update this file or the matching skill in the same PR.

## Stack

| Concern | Choice |
|---|---|
| Runtime | .NET 10 (LTS, SDK pinned in `global.json`), C# 14, nullable on, **warnings are errors** |
| UI | Blazor Web App, **global Interactive Server** render mode, MudBlazor |
| Orchestration | Aspire 13 (`Aspire.AppHost.Sdk`), ServiceDefaults for OpenTelemetry / health / resilience |
| Persistence | SQLite + EF Core 10 (`CommunityToolkit.Aspire.Hosting.Sqlite` in the AppHost), `dotnet-ef` as local tool |
| AI | Microsoft Agent Framework 1.x on `Microsoft.Extensions.AI` `IChatClient`; provider **Azure OpenAI** or local **Ollama** |
| Tests | xUnit v3 on **Microsoft.Testing.Platform**, FluentAssertions (add bUnit / NSubstitute / `Aspire.Hosting.Testing` when needed) |
| Packages | Central Package Management — versions only in `Directory.Packages.props` |

## Solution layout

```
TicketTriage.slnx · global.json · dotnet-tools.json · Directory.Build.props · Directory.Packages.props · .editorconfig
data/                         local-only inputs/outputs (gitignored, see data/README.md)
src/
  TicketTriage.Core/            Domain — NO references. Domain/ (Ticket, enums, PriorityMatrix, ServiceCatalog,
                                TriageSuggestion, …), Abstractions/ (pipeline ports)
  TicketTriage.Infrastructure/  → Core. Persistence/ (TriageDbContext, entities, Migrations/), Import/ (training.json),
                                Stubs/ (placeholder pipeline implementations), DatabaseInitializer
  TicketTriage.Agents/          → Infrastructure. Llm/ (LlmOptions, ChatClientFactory), TriageAgent, Health/
  TicketTriage.Web/             → Agents, ServiceDefaults. Components/Pages: Home, Tickets, Review
  TicketTriage.Batch/           → Agents, Infrastructure. challenge.json → pipeline → result.json
  TicketTriage.ServiceDefaults/ OTel (incl. M.E.AI + Agent Framework sources), health checks (ReadyTag), resilience
  TicketTriage.AppHost/         wires triage-db, web, batch, LLM parameters
tests/
  TicketTriage.Core.Tests/      xUnit v3 + FluentAssertions
```

Dependency direction: `Core ← Infrastructure ← Agents ← {Web, Batch}`. Never reference outward.

## Triage pipeline (Core ports → implementations)

`ITriagePipeline`: **retrieve similar** (`ISimilarTicketRetriever`) → **classify** (`ITicketClassifier`: work type, affected services, urgency, impact) → **route** (`IRoutingResolver`: service teams, assignee) → **prioritize** (`PriorityMatrix`, deterministic) → **draft** (`IResolutionDrafter`). Result: `TriageSuggestion`, reviewed by a human (`ReviewDecision`).

Currently the ports are served by `Infrastructure/Stubs/*` (registered with `TryAdd*`). Replacing a stub = implement the port (LLM-backed ones in `Agents`), register it explicitly, keep a deterministic fallback.

## Commands

```bash
dotnet tool restore                                   # once: dotnet-ef
aspire run                                            # or: dotnet run --project src/TicketTriage.AppHost
dotnet build TicketTriage.slnx
dotnet test --solution TicketTriage.slnx              # MTP runner: --solution/--project, not a positional path
dotnet test --solution TicketTriage.slnx --filter "Category!=Integration"
dotnet format TicketTriage.slnx --verify-no-changes

dotnet ef migrations add <Name> --project src/TicketTriage.Infrastructure --startup-project src/TicketTriage.Web --output-dir Persistence/Migrations
dotnet ef migrations list        --project src/TicketTriage.Infrastructure --startup-project src/TicketTriage.Web
```

In Development, Web applies migrations and imports `training.json` on startup (`InitializeTriageDatabaseAsync`, idempotent).

## Configuration & secrets

LLM config is the `Llm` section (`LlmOptions`): `Provider` = `AzureOpenAI` | `Ollama`, `AzureOpenAI:{Endpoint,Deployment,ApiKey}`, `Ollama:{Endpoint,Model}`. Missing config does **not** crash the app — `UnconfiguredChatClient` + the `ready` health check report it.

Keys never go into `appsettings*.json` or code. Use user secrets (AppHost parameters once wired, see `aspire` skill; Web standalone meanwhile):

```bash
dotnet user-secrets set "Llm:AzureOpenAI:Endpoint"   "https://<resource>.openai.azure.com/" --project src/TicketTriage.Web
dotnet user-secrets set "Llm:AzureOpenAI:Deployment" "<deployment>"                         --project src/TicketTriage.Web
dotnet user-secrets set "Llm:AzureOpenAI:ApiKey"     "<key>"                                --project src/TicketTriage.Web
```

## Conventions

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
- SQLite: EF can't translate `OrderBy`/comparisons on `DateTimeOffset` (`TrainingTicketEntity.Created`, `ImportedAt`) or `decimal` → runtime exception. Single writer — keep transactions short.
- `**/Migrations/*.cs` is generated — never hand-edit; the `protect-files` hook blocks Designer/Snapshot edits.
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
