using TicketTriage.Core.Abstractions;
using TicketTriage.Core.Domain;

namespace TicketTriage.Web.Tests;

/// <summary>Ingestor and monitor in one: ids are 1-based by first occurrence of a key; pending is scripted.</summary>
internal sealed class FakeAnalysis : ITicketIngestor, IAnalysisMonitor
{
    private readonly List<Ticket> _tickets = [];
    private readonly Dictionary<string, int> _idsByKey = [];

    /// <summary>Ticket id -> still pending.</summary>
    public Func<int, bool> IsPending { get; set; } = _ => false;

    public Func<DateTime?> Heartbeat { get; set; } = () => null;

    public HashSet<string> LockedKeys { get; } = [];

    public bool BlankDraft { get; set; }

    public int IngestCalls { get; private set; }

    public TicketOrigin? LastOrigin { get; private set; }

    public List<Ticket> LastIngested { get; } = [];

    public Task<IReadOnlyList<IngestResult>> IngestAsync(
        IReadOnlyList<Ticket> tickets,
        TicketOrigin origin,
        CancellationToken cancellationToken)
    {
        IngestCalls++;
        LastOrigin = origin;
        LastIngested.AddRange(tickets);
        List<IngestResult> results = [];
        foreach (var ticket in tickets)
        {
            var outcome = IngestOutcome.Unchanged;
            if (!_idsByKey.TryGetValue(ticket.Key, out var id))
            {
                _tickets.Add(ticket);
                _idsByKey[ticket.Key] = id = _tickets.Count;
                outcome = IngestOutcome.Created;
            }
            else if (LockedKeys.Contains(ticket.Key))
            {
                outcome = IngestOutcome.Locked;
            }

            results.Add(new IngestResult(id, outcome));
        }

        return Task.FromResult<IReadOnlyList<IngestResult>>(results);
    }

    public Task<IReadOnlyList<AnalysisState>> GetStatesAsync(IReadOnlyList<int> ticketIds, CancellationToken cancellationToken)
    {
        IReadOnlyList<AnalysisState> states = [.. ticketIds.Select(id =>
        {
            var ticket = _tickets[id - 1];
            var pending = IsPending(id);
            return new AnalysisState(id, ticket, pending, pending ? null : Suggest(ticket));
        })];
        return Task.FromResult(states);
    }

    public Task<DateTime?> GetWorkerHeartbeatAsync(CancellationToken cancellationToken) => Task.FromResult(Heartbeat());

    private TriageSuggestion Suggest(Ticket ticket) => new()
    {
        TicketKey = ticket.Key,
        WorkType = WorkType.ServiceRequest,
        AffectedServices = ["Service A"],
        ServiceTeams = ["Team A"],
        Assignee = ticket.Key,
        Urgency = Urgency.High,
        Impact = Impact.NoImpact,
        ResolutionStatus = ResolutionStatus.Done,
        DraftComment = BlankDraft ? null : "Draft.",
    };
}

internal sealed class FakeFallback : IFallbackSuggestionProvider
{
    public List<Ticket> Calls { get; } = [];

    public Task<TriageSuggestion> CreateAsync(Ticket ticket, CancellationToken cancellationToken)
    {
        Calls.Add(ticket);
        return Task.FromResult(new TriageSuggestion
        {
            TicketKey = ticket.Key,
            WorkType = WorkType.Incident,
            AffectedServices = [],
            ServiceTeams = [],
            Assignee = "fallback",
            Urgency = Urgency.Medium,
            Impact = Impact.Moderate,
            ResolutionStatus = ResolutionStatus.Done,
            DraftComment = null,
        });
    }
}

internal sealed class ManualTimeProvider : TimeProvider
{
    private DateTimeOffset _now = new(2026, 1, 1, 12, 0, 0, TimeSpan.Zero);

    public DateTime UtcNow => _now.UtcDateTime;

    public override DateTimeOffset GetUtcNow() => _now;

    public void Advance(TimeSpan by) => _now += by;
}
