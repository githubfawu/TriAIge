# TriAIge — AI ticket triage for the service desk

Hackathon project: an AI support agent that triages Jira-style service desk tickets.
It classifies each ticket (work type, affected service, service team, assignee), derives the
**priority deterministically** from Urgency × Impact, retrieves similar historical tickets from the
~20k training set, drafts a resolution comment, and lets a human analyst approve / edit / reject
the suggestion. Final scoring runs in batch mode over 20 challenge tickets (JSON in → JSON out).

> **Status: scaffold.** Only the priority matrix is implemented. Classification, embeddings / kNN retrieval,
> routing statistics, draft generation, HITL persistence and the batch output format are stubs marked `TODO: implement`.

## Prerequisites

- .NET SDK 10.0.100 or newer (`global.json` rolls forward to the latest 10.0.x feature band)
- HTTPS dev certificate: `dotnet dev-certs https --trust`
- One LLM provider:
  - **Azure OpenAI** (default): an endpoint, a chat model deployment and an API key
  - **Ollama** (local dev): running on `http://localhost:11434` with a model pulled, e.g. `ollama pull qwen2.5:1.5b`
- No Docker needed (SQLite is a local file).
- `dotnet-ef` is a local tool: run `dotnet tool restore` once.

## Configure secrets (AppHost user-secrets)

Secrets are never in code or appsettings. The AppHost reads Aspire parameters from its user-secrets
and passes them to Web and Batch as `Llm__*` environment variables.

```bash
# Azure OpenAI (default provider)
dotnet user-secrets set "Parameters:azure-openai-endpoint"   "https://<resource>.openai.azure.com/" --project src/TicketTriage.AppHost
dotnet user-secrets set "Parameters:azure-openai-deployment" "<deployment-name>"                    --project src/TicketTriage.AppHost
dotnet user-secrets set "Parameters:azure-openai-apikey"     "<api-key>"                            --project src/TicketTriage.AppHost
```

Every parameter is optional. If one is missing the app still starts, and the `agent-framework` health check reports **Unhealthy** with the missing keys.

## Switch LLM provider (Azure OpenAI ↔ Ollama)

```bash
# Use local Ollama
dotnet user-secrets set "Parameters:llm-provider" "Ollama"        --project src/TicketTriage.AppHost
dotnet user-secrets set "Parameters:ollama-model" "qwen2.5:1.5b"  --project src/TicketTriage.AppHost   # optional (default)
dotnet user-secrets set "Parameters:ollama-endpoint" "http://localhost:11434" --project src/TicketTriage.AppHost  # optional (default)

# Back to Azure OpenAI
dotnet user-secrets remove "Parameters:llm-provider" --project src/TicketTriage.AppHost
```

| AppHost parameter | App config key | Default |
|---|---|---|
| `llm-provider` | `Llm:Provider` | `AzureOpenAI` (`AzureOpenAI` \| `Ollama`) |
| `azure-openai-endpoint` | `Llm:AzureOpenAI:Endpoint` | – |
| `azure-openai-deployment` | `Llm:AzureOpenAI:Deployment` | – |
| `azure-openai-apikey` (secret) | `Llm:AzureOpenAI:ApiKey` | – |
| `ollama-endpoint` | `Llm:Ollama:Endpoint` | `http://localhost:11434` |
| `ollama-model` | `Llm:Ollama:Model` | `qwen2.5:1.5b` |

When you run Web or Batch **without** the AppHost, set the `Llm:*` keys directly, e.g.
`dotnet user-secrets set "Llm:Provider" "Ollama" --project src/TicketTriage.Web`.

## Run

```bash
dotnet build                                   # zero warnings (TreatWarningsAsErrors)
dotnet test                                    # Microsoft.Testing.Platform runner (see global.json)
dotnet run --project src/TicketTriage.AppHost  # Aspire dashboard: https://localhost:17210 (login URL in console)
```

- **web**: Blazor UI, started automatically. Endpoints:
  - `/` shows the dashboard with the health of `sqlite` and `agent-framework`
  - `/tickets` lists suggestions pending review
  - `/review/{id}` shows a suggestion next to editable fields
  - `/health` returns every check as detailed JSON
  - `/alive` runs liveness checks only
- **batch**: has an explicit start, so launch it from the dashboard (▶). It reads `data/challenge.json` and writes `data/result.json`.
- **triage-db**: the SQLite file `data/triage.db`. In Development, migrations and the training import run on startup.

Batch without Aspire:

```bash
dotnet run --project src/TicketTriage.Batch -- --input ../../data/challenge.json --output ../../data/result.json
```

(Relative paths resolve against `src/TicketTriage.Batch`.)

### Health checks

| Check | Tags | Behaviour |
|---|---|---|
| `self` | `live` | always Healthy (`/alive`) |
| `sqlite` | `ready` | EF Core `CanConnect` |
| `agent-framework` | `ready` | Sends "Reply with OK" to `TriageAgent`: 10 s timeout, result cached 60 s. **Healthy** if a reply arrives; **Degraded** if it takes over 5 s; **Unhealthy** on error, timeout or missing config |

`/health` returns HTTP 503 when any check is Unhealthy. Aspire probes `/health` for the web resource.
So **without LLM config, web shows as Unhealthy in the dashboard, even though it runs and serves pages.**
The first Ollama probe after a cold start is often Degraded while the model loads.

## Project overview

```
TicketTriage.slnx
src/
  TicketTriage.AppHost/         Aspire orchestration: SQLite resource, LLM parameters, web + batch
  TicketTriage.ServiceDefaults/ OpenTelemetry (incl. Microsoft.Extensions.AI / Agent Framework sources),
                                health endpoints + JSON writer, resilience, service discovery
  TicketTriage.Core/            Domain: records, enums, PriorityMatrix, ServiceCatalog, interfaces (no dependencies)
  TicketTriage.Infrastructure/  EF Core SQLite (TriageDbContext + migrations), TrainingDataImporter, stub services
  TicketTriage.Agents/          IChatClient per provider, TriageAgent (Agent Framework), AgentFrameworkHealthCheck
  TicketTriage.Web/             Blazor Web App (Interactive Server) + MudBlazor, HITL pages
  TicketTriage.Batch/           Console app (Generic Host): --input challenge.json --output result.json
tests/
  TicketTriage.Core.Tests/      xUnit v3 + FluentAssertions: all 25 priority combinations, service catalog
data/                           gitignored: training.json, challenge.json, result.json, triage.db
```

Dependencies point one way: Web / Batch → Agents → Infrastructure → Core.

### Priority matrix (Urgency × Impact)

| Urgency \ Impact | Major | Significant | Moderate | Minor | No Impact |
|---|---|---|---|---|---|
| Critical | Highest | Highest | High | Medium | Medium |
| High | Highest | High | High | Medium | Low |
| Medium | High | High | Medium | Low | Low |
| Low | Medium | Medium | Low | Low | Lowest |
| Lowest | Medium | Low | Low | Lowest | Lowest |

### Database

- Tables: `TrainingTickets` (list fields are JSON columns) and `TriageSuggestions` (suggestion JSON, `Decision`, analyst edits, timestamps).
- `DateTimeOffset` values are stored as sortable 64-bit integers, because SQLite can't `ORDER BY` them natively.
- Add a migration: `dotnet ef migrations add <Name> --project src/TicketTriage.Infrastructure --output-dir Persistence/Migrations`

## Package versions (pinned in `Directory.Packages.props`)

| Package | Version | Note |
|---|---|---|
| **Microsoft.Agents.AI** (Agent Framework) | **1.22.0** | stable release, no prerelease pin needed |
| Microsoft.Extensions.AI / .OpenAI | 10.10.0 | |
| OpenAI | 2.13.0 | also used for Azure OpenAI through the `/openai/v1/` endpoint |
| OllamaSharp | 5.4.30 | `OllamaApiClient` implements `IChatClient` |
| Aspire.AppHost.Sdk | 13.5.4 | |
| CommunityToolkit.Aspire.Hosting.Sqlite | 13.5.0 | |
| Microsoft.EntityFrameworkCore.Sqlite | 10.0.12 | |
| MudBlazor | 9.10.0 | |
| xunit.v3 / FluentAssertions | 4.0.1 / 8.11.0 | |

### Deviations from the original spec

- **SQLite client integration:** `CommunityToolkit.Aspire.Microsoft.EntityFrameworkCore.Sqlite` is stable only as 9.7.2,
  which targets EF Core 9; the 13.x line is beta-only. The apps therefore use plain EF Core 10 `UseSqlite` with the connection
  string Aspire injects (`ConnectionStrings:triage-db`). The AppHost still uses the toolkit's hosting package.
- **Azure OpenAI client:** the latest stable `Azure.AI.OpenAI` is 2.1.0 and depends on an old `OpenAI` SDK that conflicts with
  `Microsoft.Extensions.AI.OpenAI` 10.10. Azure OpenAI is called with the `OpenAI` SDK against `<endpoint>/openai/v1/` instead,
  which is Microsoft's recommended v1 API path and needs no `api-version`.
- **Test runner:** xunit.v3 4.x only runs on Microsoft.Testing.Platform. `global.json` opts `dotnet test` into MTP, so
  `Microsoft.NET.Test.Sdk` and `xunit.runner.visualstudio` are not referenced.
- **FluentAssertions licensing:** v8 is under the Xceed license, which requires a paid license for commercial use.
  If that's a concern, [AwesomeAssertions](https://www.nuget.org/packages/AwesomeAssertions) is a drop-in, Apache-2.0 fork.
- **ServiceCatalog:** the 20 service names are placeholders (`TODO Critical Service 01` …); the 14/6 split is correct.
  Replace them with the real catalog. Likewise, check the `Ticket` JSON property names against the real data files.
