using Microsoft.Extensions.AI;

namespace TicketTriage.Agents.Llm;

/// <summary>
/// Placeholder used when the LLM provider is not configured. Lets the application start;
/// every call fails with a descriptive error, which the agent health check surfaces as Unhealthy.
/// </summary>
internal sealed class UnconfiguredChatClient(string reason) : IChatClient
{
    public Task<ChatResponse> GetResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        CancellationToken cancellationToken = default) =>
        throw new InvalidOperationException(reason);

    public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        CancellationToken cancellationToken = default) =>
        throw new InvalidOperationException(reason);

    public object? GetService(Type serviceType, object? serviceKey = null) =>
        serviceKey is null && serviceType.IsInstanceOfType(this) ? this : null;

    public void Dispose()
    {
    }
}
