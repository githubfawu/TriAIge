using TicketTriage.Core.Abstractions;
using TicketTriage.Core.Domain;

namespace TicketTriage.Web.Tests.Support;

public enum FakeBehavior
{
    Succeed,
    Throw,
    Hang,
}

/// <summary>Test double for <see cref="ITriagePipeline"/> (never a real LLM in unit tests): counts calls and can
/// succeed, throw or hang until cancelled, to drive <see cref="TriageWorker"/> tests (AC11).</summary>
public sealed class FakeTriagePipeline : ITriagePipeline
{
    private int _calls;

    public int Calls => Volatile.Read(ref _calls);

    public FakeBehavior Behavior { get; set; } = FakeBehavior.Succeed;

    public Func<Ticket, TriageSuggestion>? SuggestionFactory { get; set; }

    public async Task<TriageSuggestion> TriageAsync(Ticket ticket, CancellationToken cancellationToken)
    {
        Interlocked.Increment(ref _calls);

        switch (Behavior)
        {
            case FakeBehavior.Throw:
                throw new InvalidOperationException("Fake pipeline failure.");
            case FakeBehavior.Hang:
                await Task.Delay(Timeout.Infinite, cancellationToken);
                break;
        }

        return SuggestionFactory?.Invoke(ticket) ?? new TriageSuggestion
        {
            TicketKey = ticket.Key,
            WorkType = WorkType.Incident,
            Urgency = Urgency.Medium,
            Impact = Impact.Moderate,
        };
    }
}
