using System.Runtime.CompilerServices;
using Microsoft.Extensions.AI;
using TicketTriage.Agents.Services;
using TicketTriage.Core.Domain;

namespace TicketTriage.Agents.Tests;

internal sealed class FakeChatClient(params string[] replies) : IChatClient
{
    private int _next;

    public List<List<ChatMessage>> Calls { get; } = [];

    public List<ChatOptions?> Options { get; } = [];

    public Task<ChatResponse> GetResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Calls.Add([.. messages]);
        Options.Add(options);
        return Task.FromResult(new ChatResponse(new ChatMessage(ChatRole.Assistant, replies[_next++ % replies.Length])));
    }

    public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var response = await GetResponseAsync(messages, options, cancellationToken);
        foreach (var update in response.ToChatResponseUpdates())
        {
            yield return update;
        }
    }

    public object? GetService(Type serviceType, object? serviceKey = null) => null;

    public void Dispose()
    {
    }
}

internal sealed class ThrowingChatClient : IChatClient
{
    public Task<ChatResponse> GetResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        CancellationToken cancellationToken = default) =>
        throw new HttpRequestException("LLM unreachable.");

    public IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        CancellationToken cancellationToken = default) =>
        throw new HttpRequestException("LLM unreachable.");

    public object? GetService(Type serviceType, object? serviceKey = null) => null;

    public void Dispose()
    {
    }
}

internal sealed class TestCatalog : IServiceCatalogProvider
{
    public IReadOnlyList<ServiceDefinition> GetServices() =>
    [
        new("Trading Platform", ServiceRating.Critical),
        new("Outlook & Email", ServiceRating.NonCritical),
    ];
}

internal static class Samples
{
    public static Ticket Ticket(string summary = "Cannot log in to trading screen", string? description = "Login fails since morning.") =>
        new() { Key = "SD-1", Summary = summary, Description = description };

    public static SimilarTicket Similar(string key, string assignee, string service, string resolution, double score = 0.9) =>
        new(new Ticket
        {
            Key = key,
            Summary = "similar " + key,
            Assignee = assignee,
            AffectedServices = [service],
            WorkType = "Incident",
            Resolution = resolution,
        }, score);

    public const string ValidClassification =
        """{"workType":"Incident","affectedServices":["Trading Platform"],"urgency":"High","impact":"No Impact"}""";

    public const string ValidDraft =
        """{"resolutionStatus":"cannot reproduce","language":"English","comment":"Login worked on our side; please retry and report if it persists."}""";
}
