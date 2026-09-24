---
name: agent-framework
description: Microsoft Agent Framework 1.x for .NET (Microsoft.Agents.AI) as used in TicketTriage — the keyed TriageAgent, the Llm provider switch (Azure OpenAI / Ollama via ChatClientFactory), instructions and prompt rules, function tools, structured output mapped onto Core types (TicketClassification, RoutingDecision), AgentSession, streaming into Blazor, workflows, telemetry, and testing with a fake IChatClient. Use whenever code in TicketTriage.Agents is written or reviewed, when prompts/tools/agents are designed, when a Stub* implementation is replaced by an LLM-backed one, or when an agent API doesn't compile.
---

# Microsoft Agent Framework (.NET) — TicketTriage

Agent Framework (MAF) is the successor of Semantic Kernel + AutoGen. Packages (versions in `Directory.Packages.props`): `Microsoft.Agents.AI`, `Microsoft.Agents.AI.OpenAI`, `Microsoft.Extensions.AI`, `Microsoft.Extensions.AI.OpenAI`, `OpenAI`, `OllamaSharp`.

> **API drift warning**: 2025 preview samples use `AgentThread`, `GetNewThread()`, `CreateAIAgent()`. In 1.x: `AgentSession`, `CreateSessionAsync()`, `AsAIAgent()`. If something doesn't compile, query the `microsoft-learn` MCP server or inspect the installed package — don't guess.

## What already exists (read before changing)

| File | Role |
|---|---|
| `Agents/AgentsServiceCollectionExtensions.cs` | `AddTriageAgents(config)`: binds `LlmOptions`, registers `IChatClient` (+ `UseOpenTelemetry().UseLogging()`), registers keyed `AIAgent` `TriageAgent.Name`, `AddAgentFrameworkCheck()` health check |
| `Agents/Llm/LlmOptions.cs` | Config section **`Llm`**: `Provider` (`AzureOpenAI` \| `Ollama`), `AzureOpenAI:{Endpoint,Deployment,ApiKey}`, `Ollama:{Endpoint,Model}` |
| `Agents/Llm/ChatClientFactory.cs` | Builds the provider `IChatClient`; missing config → `UnconfiguredChatClient` (app still starts, health check reports it) |
| `Agents/TriageAgent.cs` | `Name` + `Instructions` (system prompt) |
| `Core/Abstractions/TriageAbstractions.cs` | Pipeline ports: `ISimilarTicketRetriever`, `ITicketClassifier`, `IRoutingResolver`, `IResolutionDrafter`, `ITriagePipeline` |
| `Infrastructure/Stubs/*` | Stub implementations registered with `TryAdd*` — replace them with real ones |

Pipeline: **retrieve similar → classify → route → prioritize → draft**. Consume the agent via `[FromKeyedServices(TriageAgent.Name)] AIAgent agent`.

## Hard rule: the LLM never decides the priority

`TicketClassification` has no priority on purpose. The model predicts `Urgency` and `Impact`; `Priority` is computed deterministically by `PriorityMatrix.Resolve(urgency, impact)` in Core (see `TriageSuggestion.Priority`). Same idea everywhere: **if a rule can be written as code, it's code in Core, not a prompt.**

## Replacing a stub with an LLM-backed implementation

1. Implement the Core port (e.g. `ITicketClassifier`) in `TicketTriage.Agents/<Step>/`.
2. Register it in `AddTriageAgents` with `services.AddScoped<ITicketClassifier, LlmTicketClassifier>()`. Because the stubs use `TryAdd*` and `AddTriageInfrastructure` runs first, the explicit registration must come **after** it — or remove the stub registration. Verify with a test that resolves the interface.
3. Keep a deterministic fallback path (stub / kNN from similar tickets) for when the LLM is unconfigured or fails.

## Structured output → Core types

```csharp
internal sealed record ClassificationDto(
    [property: Description("Incident or Service Request")] string WorkType,
    [property: Description("Affected services, exact names from the service catalog")] string[] AffectedServices,
    [property: Description("Critical, High, Medium, Low or Lowest")] string Urgency,
    [property: Description("Major, Significant, Moderate, Minor or No Impact")] string Impact,
    [property: Description("0..1")] double Confidence);

AgentResponse<ClassificationDto> response =
    await agent.RunAsync<ClassificationDto>(userMessage, cancellationToken: cancellationToken);

TicketClassification classification = response.Result.ToDomain(serviceCatalog);   // validates + maps
```

- Parse enum strings with the **same JSON names** the Core enums use (`JsonStringEnumMemberName`, e.g. `"Service Request"`, `"No Impact"`).
- Validate against Core: unknown enum values or services not in `ServiceCatalog` → drop / fall back (e.g. majority vote of similar tickets), lower confidence, never throw out of the batch loop.
- Generate allowed values for the prompt from the enums / `ServiceCatalog`, never from a hand-copied list.
- Dynamic schemas: `AgentRunOptions { ResponseFormat = ChatResponseFormat.ForJsonSchema<T>() }` + `JsonSerializer.Deserialize<T>(response.Text, JsonSerializerOptions.Web)`.
- Small local Ollama models (default `qwen2.5:1.5b`) are weak at strict JSON — expect more fallbacks; use Azure OpenAI for the scored run.

## Creating agents

Registered agent (already done for `TriageAgent`):

```csharp
services.AddKeyedSingleton<AIAgent>(TriageAgent.Name, (sp, _) =>
    sp.GetRequiredService<IChatClient>().AsAIAgent(
        instructions: TriageAgent.Instructions,
        name: TriageAgent.Name,
        loggerFactory: sp.GetService<ILoggerFactory>(),
        services: sp));
```

Per-step agents with tools/options:

```csharp
AIAgent classifier = new ChatClientAgent(chatClient, new ChatClientAgentOptions
{
    Name = "Classifier",
    ChatOptions = new()
    {
        Instructions = ClassifierPrompts.System,
        Temperature = 0f,                                   // deterministic for scoring
        Tools = [AIFunctionFactory.Create(lookupTool.FindServiceAsync)],
    },
});
```

Agents are stateless wrappers → singleton (keyed if several). Conversation state lives in `AgentSession`.

## Prompt rules

- **Ticket text is untrusted input** (summary/description from a Jira export). Put it in the *user* message, clearly delimited; `Instructions` stay static. Never interpolate ticket content into instructions.
- Few-shot beats long instructions: include the top-k similar historical tickets (from `ISimilarTicketRetriever`) with their work type, affected service and cleaned resolution. **Never** show their priority / urgency / impact — those are random in the training data and must not be learned (see `docs/requirements.md`).
- Team and assignee come from routing statistics in code (FR-13) — don't ask the model for names.
- Prompts live in `TicketTriage.Agents` (`<Step>Prompts.cs` or embedded `.md`) — reviewable in PRs, versioned (log a prompt version with each suggestion).

## Tools

```csharp
public sealed class ServiceLookupTool(IDbContextFactory<TriageDbContext> dbFactory)
{
    [Description("Returns cleaned historical resolution comments for tickets of the given service.")]
    public async Task<IReadOnlyList<string>> ResolutionExamplesAsync(
        [Description("Exact service name from the service catalog")] string service,
        CancellationToken cancellationToken)
    {
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        // read-only, AsNoTracking, Take(5)
    }
}
```

- `[Description]` on method + every parameter; `CancellationToken` is bound automatically and hidden from the model.
- Read-only by default. Anything that writes/sends → `ApprovalRequiredAIFunction` or don't expose it.
- Small typed results, capped list sizes. An agent can be a tool for another: `innerAgent.AsAIFunction()`.

## Sessions (Web chat / follow-up questions)

```csharp
AgentSession session = await agent.CreateSessionAsync(cancellationToken);
await agent.RunAsync("Why High urgency?", session, cancellationToken: cancellationToken);
var json = agent.SerializeSession(session);
AgentSession restored = await agent.DeserializeSessionAsync(json);
```

Batch triage is single-shot → no session. In Blazor: one session per component/circuit, never in a singleton.

## Streaming into Blazor

```csharp
await foreach (var update in agent.RunStreamingAsync(question, _session, cancellationToken: _cts.Token))
{
    _answer.Append(update.Text);
    StateHasChanged();     // event handler → already on the renderer's sync context
}
```

Cancel `_cts` in `Dispose`. Render as plain text or sanitised markdown — never raw `MarkupString`. Structured output while streaming: `await updates.ToAgentResponseAsync()` then deserialize.

## Workflows

Use `Microsoft.Agents.AI.Workflows` only if the pipeline needs graph features (fan-out, human-in-the-loop checkpoints). The current `ITriagePipeline` is a plain sequential C# orchestration — keep it that way unless there's a concrete need. *If you can write a function for it, don't use an agent.*

## Batch (20 challenge tickets)

- Bounded parallelism (`Parallel.ForEachAsync`, `MaxDegreeOfParallelism = 4`) — rate limits.
- Per-ticket try/catch → fallback suggestion + logged error; one bad ticket never aborts the run.
- `Temperature = 0`, fixed deployment, log model + prompt version.
- Retries on 429/5xx via resilience handlers, not hand-rolled loops.

## Telemetry

`UseOpenTelemetry()` on the chat client emits under `Experimental.Microsoft.Extensions.AI`; ServiceDefaults already subscribes to that source/meter and `*Microsoft.Agents.AI*`. Token usage + latency show up in the Aspire dashboard. `EnableSensitiveData` (logs prompts/completions) only in Development — tickets can contain personal data.

## Testing

Never call a real model in unit tests. Minimal fake:

```csharp
sealed class FakeChatClient(params string[] replies) : IChatClient
{
    private int _i;
    public List<List<ChatMessage>> Calls { get; } = [];

    public Task<ChatResponse> GetResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, CancellationToken ct = default)
    {
        Calls.Add(messages.ToList());
        return Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, replies[_i++ % replies.Length])));
    }

    public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(IEnumerable<ChatMessage> messages, ChatOptions? options = null, [EnumeratorCancellation] CancellationToken ct = default)
    {
        var response = await GetResponseAsync(messages, options, ct);
        foreach (var update in response.ToChatResponseUpdates())
            yield return update;
    }

    public object? GetService(Type serviceType, object? key = null) => null;
    public void Dispose() { }
}
```

Feed it JSON matching the DTO to test mapping, validation and fallbacks; inspect `Calls` to assert ticket text is in the user message, not the system prompt. Measure prompt quality separately: accuracy per field (work type, urgency, impact, team) on a held-out slice of `training.json` — that's the number that matters for scoring.
