using TicketTriage.Core.Abstractions;
using TicketTriage.Core.Domain;

namespace TicketTriage.Infrastructure.Tests;

internal static class Tickets
{
    public static Ticket Make(string key, string? description = "Printer is broken") => new()
    {
        Key = key,
        Summary = $"Summary {key}",
        Description = description,
    };

    public static async IAsyncEnumerable<Ticket> Stream(params Ticket[] tickets)
    {
        foreach (var ticket in tickets)
        {
            await Task.Yield();
            yield return ticket;
        }
    }
}

internal sealed class CallLog
{
    public List<string> Steps { get; } = [];
}

internal sealed class RecordingSimilarSource(CallLog log) : ISimilarTicketSource
{
    public List<(Ticket Ticket, int Top)> Calls { get; } = [];

    public Task<IReadOnlyList<SimilarTicket>> FindSimilarAsync(Ticket ticket, int top, CancellationToken cancellationToken)
    {
        log.Steps.Add("similar");
        Calls.Add((ticket, top));
        return Task.FromResult<IReadOnlyList<SimilarTicket>>([new SimilarTicket(Tickets.Make("OLD-1"), 0.9)]);
    }
}

internal sealed class FakeClassifier(CallLog log) : ITicketClassifier
{
    public Task<TicketClassification> ClassifyAsync(Ticket ticket, IReadOnlyList<SimilarTicket> similarTickets, CancellationToken cancellationToken)
    {
        log.Steps.Add("classify");
        return Task.FromResult(new TicketClassification(WorkType.Incident, ["Email"], Urgency.High, Impact.Significant));
    }
}

internal sealed class FakeRouter(CallLog log) : IRoutingResolver
{
    public Task<RoutingDecision> ResolveAsync(Ticket ticket, TicketClassification classification, IReadOnlyList<SimilarTicket> similarTickets, CancellationToken cancellationToken)
    {
        log.Steps.Add("route");
        return Task.FromResult(new RoutingDecision(["Team A"], "alice"));
    }
}

internal sealed class FakeDrafter(CallLog log) : IResolutionDrafter
{
    public Task<string> DraftAsync(Ticket ticket, TicketClassification classification, IReadOnlyList<SimilarTicket> similarTickets, CancellationToken cancellationToken)
    {
        log.Steps.Add("draft");
        return Task.FromResult("draft text");
    }
}

internal sealed class RecordingFailureStore(CallLog? log = null) : ITriageFailureStore
{
    public List<TriageFailure> Calls { get; } = [];

    public int? PersistedRetries { get; set; }

    public bool Throw { get; set; }

    public List<int> ResetCalls { get; } = [];

    public bool ThrowOnReset { get; set; }

    public Task ResetRetriesAsync(int ticketId, CancellationToken cancellationToken)
    {
        ResetCalls.Add(ticketId);
        return ThrowOnReset ? throw new InvalidOperationException("db down") : Task.CompletedTask;
    }

    public Task<int?> RecordFailureAsync(TriageFailure failure, CancellationToken cancellationToken)
    {
        log?.Steps.Add("store");
        Calls.Add(failure);
        return Throw ? throw new InvalidOperationException("db down") : Task.FromResult(PersistedRetries);
    }
}

/// <summary>Fails the first <c>failures</c> calls (or always when null), using the supplied behaviour, then succeeds.</summary>
internal sealed class ScriptedClassifier(int? failures = 0, Func<CancellationToken, Task>? onFail = null) : ITicketClassifier
{
    public int Calls { get; private set; }

    public async Task<TicketClassification> ClassifyAsync(Ticket ticket, IReadOnlyList<SimilarTicket> similarTickets, CancellationToken cancellationToken)
    {
        Calls++;
        if (failures is null || Calls <= failures)
        {
            if (onFail is not null)
            {
                await onFail(cancellationToken);
            }

            throw new InvalidOperationException(ticket.Summary);
        }

        return new TicketClassification(WorkType.Incident, ["Email"], Urgency.High, Impact.Significant);
    }
}

internal sealed class ScriptedDrafter(int failures = 0, string draft = "draft text") : IResolutionDrafter
{
    public int Calls { get; private set; }

    public Task<string> DraftAsync(Ticket ticket, TicketClassification classification, IReadOnlyList<SimilarTicket> similarTickets, CancellationToken cancellationToken)
    {
        Calls++;
        return Calls <= failures ? throw new InvalidOperationException("draft failed") : Task.FromResult(draft);
    }
}

internal sealed class FixedSimilarSource(params SimilarTicket[] similar) : ISimilarTicketSource
{
    public int Calls { get; private set; }

    public Task<IReadOnlyList<SimilarTicket>> FindSimilarAsync(Ticket ticket, int top, CancellationToken cancellationToken)
    {
        Calls++;
        return Task.FromResult<IReadOnlyList<SimilarTicket>>(similar);
    }
}

internal sealed class ThrowingRouter : IRoutingResolver
{
    public Task<RoutingDecision> ResolveAsync(Ticket ticket, TicketClassification classification, IReadOnlyList<SimilarTicket> similarTickets, CancellationToken cancellationToken) =>
        throw new InvalidOperationException("router down");
}
