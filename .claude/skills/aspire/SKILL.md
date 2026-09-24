---
name: aspire
description: Aspire 13 orchestration for TicketTriage — AppHost resource model (SQLite, Web, Batch, AI parameters/secrets, optional Ollama), ServiceDefaults (OpenTelemetry, health checks, resilience, service discovery), running with the Aspire CLI and dashboard, using the Aspire MCP server to read logs/traces, and integration testing with Aspire.Hosting.Testing. Use when AppHost.cs, ServiceDefaults, configuration/secrets wiring, or startup/connectivity problems are involved.
---

# Aspire 13 — TicketTriage

AppHost: `src/TicketTriage.AppHost` (`Aspire.AppHost.Sdk/13.x`, `UserSecretsId: ticket-triage-apphost`). DCP and dashboard come from NuGet, so `dotnet run --project src/TicketTriage.AppHost` works without the CLI; `aspire run` is nicer (auto-finds the AppHost, prints the dashboard URL).

## The AppHost (implemented — read `AppHost.cs` before changing)

| Resource | Wiring |
|---|---|
| `triage-db` | `AddSqlite("triage-db", <repo>/data, "triage.db")` |
| `web` | `WithReference(sqlite).WaitFor(sqlite)`, `TrainingData__Path`, `WithLlmConfiguration(llm)`, `WithHttpHealthCheck("/health")` |
| `batch` | same DB + LLM wiring, `--input data/challenge.json --output data/result.json`, `WithExplicitStart()` |

LLM parameters are created with `AddOptionalParameter(name, fallback, secret)` (missing → fallback/empty, app still starts) and mapped to `Llm__*` env vars by `WithLlmConfiguration`. Add new settings the same way instead of new ad-hoc env vars.

Key rules:
- **Resource name = connection string name** in the consumer (`"triage-db"` → `GetConnectionString("triage-db")`).
- `WithReference` injects connection strings / service-discovery URLs; `WaitFor` orders startup. Never hardcode URLs or ports.
- `WithExplicitStart()` keeps the batch from running on every `aspire run`; start it from the dashboard (▶).
- Check the exact `AddSqlite` signature of the installed CommunityToolkit version if it doesn't compile (`aspire` MCP `list_integrations` / `search_docs`).

## Secrets & parameters

Parameters are read from AppHost configuration `Parameters:<name>`: `llm-provider`, `azure-openai-endpoint`, `azure-openai-deployment`, `azure-openai-apikey` (secret), `openai-apikey` (secret), `openai-model`, `apertus-endpoint`, `apertus-apikey` (secret), `apertus-model`, `ollama-endpoint`, `ollama-model` (table in `README.md`):

```bash
dotnet user-secrets set "Parameters:azure-openai-apikey" "<key>" --project src/TicketTriage.AppHost
```

Never put keys in `appsettings*.json`, `launchSettings.json`, or code. Running Web/Batch without the AppHost: set `Llm:*` keys in that project's user secrets.

Missing LLM config does **not** fail startup — `ChatClientFactory` returns an `UnconfiguredChatClient` and the `ready` health check reports it (visible in the dashboard).

## Optional: local models with Ollama

Simplest: run Ollama locally and set `Llm:Provider=Ollama` (defaults: `http://localhost:11434`, `qwen2.5:1.5b`). Container-managed alternative: `CommunityToolkit.Aspire.Hosting.Ollama` (`builder.AddOllama("ollama").WithDataVolume().AddModel(...)`) — needs Docker, and the endpoint must then be passed into `Llm__Ollama__Endpoint`. Good for free iterations; use a larger hosted model (Azure OpenAI, OpenAI or Apertus) for the scored run.

## ServiceDefaults

Every service project (Web, Batch) calls:

```csharp
builder.AddServiceDefaults();     // OTel, health checks, service discovery, HttpClient resilience
...
app.MapDefaultEndpoints();        // /health, /alive — web apps only (Development by default)
```

`TicketTriage.ServiceDefaults/Extensions.cs` already subscribes to `Experimental.Microsoft.Extensions.AI` (chat client telemetry, incl. token usage) and `*Microsoft.Agents.AI*`, and defines `Extensions.ReadyTag` (`"ready"`) for readiness checks (SQLite, agent). Add new sources/meters there if you create your own `ActivitySource`/`Meter`.

For Batch (a console app) use `Host.CreateApplicationBuilder(args)` + `AddServiceDefaults()` so it gets config, logging and OTel like the web app.

## Debugging with the dashboard & Aspire MCP

- Dashboard: resources, console logs, structured logs, traces (incl. LLM calls with token counts), metrics.
- `.mcp.json` registers the Aspire MCP server (`aspire agent mcp`, needs the Aspire CLI: `dotnet tool install -g Aspire.Cli` or the install script from aspire.dev). With the app running, Claude can call `list_resources`, `list_console_logs`, `list_structured_logs`, `list_traces` — **use these before guessing why something fails at runtime.**
- `aspire doctor` checks the local environment (SDK, container runtime, certificates).

## Integration tests

Separate project `tests/TicketTriage.IntegrationTests` with `Aspire.Hosting.Testing`:

```csharp
[Fact, Trait("Category", "Integration")]
public async Task Web_Starts_And_Home_Returns200()
{
    var ct = TestContext.Current.CancellationToken;
    var appHost = await DistributedApplicationTestingBuilder.CreateAsync<Projects.TicketTriage_AppHost>(ct);
    await using var app = await appHost.BuildAsync(ct);
    await app.StartAsync(ct);
    await app.ResourceNotifications.WaitForResourceHealthyAsync("web", ct);

    using var http = app.CreateHttpClient("web");
    var response = await http.GetAsync("/", ct);

    response.StatusCode.Should().Be(HttpStatusCode.OK);
}
```

Configure a fake/cheap AI provider for these tests (parameters/env overrides) — never spend real tokens in CI.

## Troubleshooting

| Symptom | Check |
|---|---|
| `Connection string 'triage-db' missing` | resource name mismatch, or project started without AppHost |
| Web waits forever | `WaitFor` target unhealthy → dashboard logs of that resource |
| Port/cert errors | `dotnet dev-certs https --trust`, `aspire doctor` |
| DB locked | Batch and Web writing simultaneously — shorten transactions, WAL mode (see `sqlite-efcore`) |
