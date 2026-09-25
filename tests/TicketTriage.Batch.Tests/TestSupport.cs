using TicketTriage.Core.Abstractions;
using TicketTriage.Core.Domain;

namespace TicketTriage.Batch.Tests;

internal static class Suggestions
{
    /// <summary>Canned suggestion; the assignee carries the ticket key so tests can see which suggestion landed where.</summary>
    public static Func<int, Ticket, TriageSuggestion> Standard(IEnumerable<string>? fallbackKeys = null)
    {
        var fallback = new HashSet<string>(fallbackKeys ?? []);
        return (_, ticket) => new TriageSuggestion
        {
            TicketKey = ticket.Key,
            WorkType = WorkType.ServiceRequest,
            AffectedServices = ["Service A"],
            ServiceTeams = ["Team A"],
            Assignee = ticket.Key,
            Urgency = Urgency.High,
            Impact = Impact.NoImpact,
            ResolutionStatus = (ResolutionStatus)(ticket.Summary.Length % 4),
            DraftComment = fallback.Contains(ticket.Key) ? null : "Draft.",
        };
    }

    /// <summary>Cycles through all 25 (urgency, impact) pairs and the four resolution statuses in input order.</summary>
    public static TriageSuggestion Cycling(int index, Ticket ticket) => new()
    {
        TicketKey = ticket.Key,
        WorkType = WorkType.Incident,
        AffectedServices = ["Service A"],
        ServiceTeams = ["Team A"],
        Assignee = "Jane Doe",
        Urgency = (Urgency)(index / 5 % 5),
        Impact = (Impact)(index % 5),
        ResolutionStatus = (ResolutionStatus)(index % 4),
        DraftComment = "Draft.",
    };
}

/// <summary>Ingestor and monitor in one: ids are 1-based by first occurrence of a key; pending is scripted per poll.</summary>
internal sealed class FakeAnalysis : ITicketIngestor, IAnalysisMonitor
{
    private readonly List<Ticket> _tickets = [];
    private readonly Dictionary<string, int> _idsByKey = [];
    private readonly Func<int, Ticket, TriageSuggestion> _suggest;

    public FakeAnalysis(Func<int, Ticket, TriageSuggestion>? suggest = null) => _suggest = suggest ?? Suggestions.Standard();

    /// <summary>(1-based poll number, ticket id) -> still pending.</summary>
    public Func<int, int, bool> IsPending { get; set; } = (_, _) => false;

    public Func<DateTime?> Heartbeat { get; set; } = () => null;

    public Action<int>? OnPoll { get; set; }

    public bool ThrowOnIngest { get; set; }

    public int IngestCalls { get; private set; }

    public int Polls { get; private set; }

    public TicketOrigin? LastOrigin { get; private set; }

    public List<Ticket> LastIngested { get; } = [];

    public Task<IReadOnlyList<IngestResult>> IngestAsync(
        IReadOnlyList<Ticket> tickets,
        TicketOrigin origin,
        CancellationToken cancellationToken)
    {
        if (ThrowOnIngest)
        {
            throw new InvalidOperationException("ingestor must not run");
        }

        IngestCalls++;
        LastOrigin = origin;
        LastIngested.AddRange(tickets);
        List<IngestResult> results = [];
        foreach (var ticket in tickets)
        {
            if (!_idsByKey.TryGetValue(ticket.Key, out var id))
            {
                _tickets.Add(ticket);
                _idsByKey[ticket.Key] = id = _tickets.Count;
            }

            results.Add(new IngestResult(id, IngestOutcome.Created));
        }

        return Task.FromResult<IReadOnlyList<IngestResult>>(results);
    }

    public Task<IReadOnlyList<AnalysisState>> GetStatesAsync(IReadOnlyList<int> ticketIds, CancellationToken cancellationToken)
    {
        Polls++;
        OnPoll?.Invoke(Polls);
        cancellationToken.ThrowIfCancellationRequested();
        IReadOnlyList<AnalysisState> states = [.. ticketIds.Select(id =>
        {
            var ticket = _tickets[id - 1];
            var pending = IsPending(Polls, id);
            return new AnalysisState(id, ticket, pending, pending ? null : _suggest(id - 1, ticket));
        })];
        return Task.FromResult(states);
    }

    public Task<DateTime?> GetWorkerHeartbeatAsync(CancellationToken cancellationToken) => Task.FromResult(Heartbeat());
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

/// <summary>Manual clock whose delays complete at once and advance the clock, so waiting logic runs without real waits.</summary>
internal sealed class TestTimeProvider : TimeProvider
{
    private DateTimeOffset _now = new(2026, 1, 1, 12, 0, 0, TimeSpan.Zero);

    public DateTime UtcNow => _now.UtcDateTime;

    public override DateTimeOffset GetUtcNow() => _now;

    public void Advance(TimeSpan by) => _now += by;

    public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
    {
        if (dueTime != Timeout.InfiniteTimeSpan)
        {
            Advance(dueTime);
            ThreadPool.QueueUserWorkItem(_ => callback(state));
        }

        return new ImmediateTimer();
    }

    private sealed class ImmediateTimer : ITimer
    {
        public bool Change(TimeSpan dueTime, TimeSpan period) => true;

        public void Dispose()
        {
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}

internal sealed class TempDir : IDisposable
{
    public string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "batch-tests-" + Guid.NewGuid().ToString("N"));

    public TempDir() => Directory.CreateDirectory(Path);

    public string Combine(string name) => System.IO.Path.Combine(Path, name);

    public void Dispose() => Directory.Delete(Path, recursive: true);
}
