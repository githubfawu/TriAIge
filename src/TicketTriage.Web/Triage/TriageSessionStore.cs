using Microsoft.Extensions.Logging;
using TicketTriage.Core.Domain;

namespace TicketTriage.Web.Triage;

/// <summary>RAM-only phase of a queued ticket (Leitplanke 1). Pending/Approved/Rejected are derived from the
/// DB instead - see <see cref="TicketDisplayStateMapper"/> - so they never appear here.</summary>
public enum QueuePhase
{
    Queued,
    Analysing,
    Failed,
}

/// <summary>
/// Everything about a triaged ticket that intentionally never touches the DB (Technical Constraints: RAM-Store).
/// Immutable: every state change replaces the dictionary entry, so readers never see a half-updated record.
/// Fields beyond Slice 1 (first-opened/decided timestamps, reject reason, edited fields) are already present
/// so Slices 2-3 can populate them without changing this shape.
/// </summary>
public sealed record TriageSessionEntry
{
    public required int TicketId { get; init; }

    public required string IssueKey { get; init; }

    public required Ticket Ticket { get; init; }

    public required int UploadId { get; init; }

    public required QueuePhase Phase { get; init; }

    public int Attempts { get; init; }

    public string? FailureReason { get; init; }

    public TriageSuggestion? Suggestion { get; init; }

    public IReadOnlyList<string> NotInCatalog { get; init; } = [];

    // Slice 2/3 fields (kept here now so the record shape doesn't change later):
    public DateTimeOffset? FirstOpenedAt { get; init; }

    public DateTimeOffset? DecidedAt { get; init; }

    public string? RejectReason { get; init; }

    public IReadOnlyList<string> EditedFields { get; init; } = [];
}

public sealed record UploadProgress(int Analysed, int Total);

/// <summary>
/// Singleton RAM store: one entry per triaged ticket id, plus the analysis queue. Shared by the
/// <see cref="TriageWorker"/> and every Blazor circuit (NFR7), so every mutation happens under <see cref="_gate"/>
/// and the <see cref="TicketChanged"/> event fires outside the lock, with each handler isolated (Leitplanke 8).
/// </summary>
public sealed class TriageSessionStore(ILogger<TriageSessionStore> logger)
{
    private readonly object _gate = new();
    private readonly Dictionary<int, TriageSessionEntry> _entries = [];
    private readonly TriageWorkQueue _queue = new();
    private readonly SemaphoreSlim _signal = new(0);

    /// <summary>Raised (outside the lock) whenever a ticket's RAM state changes.</summary>
    public event Action<int>? TicketChanged;

    /// <summary>Enqueues a newly-saved ticket. No-op if it is already Queued or Analysing (FR6: never process a
    /// ticket twice concurrently).</summary>
    public void Register(int ticketId, string issueKey, Ticket ticket, int uploadId)
    {
        bool enqueued;
        lock (_gate)
        {
            if (_entries.TryGetValue(ticketId, out var existing) && existing.Phase is QueuePhase.Queued or QueuePhase.Analysing)
            {
                return;
            }

            _entries[ticketId] = new TriageSessionEntry
            {
                TicketId = ticketId,
                IssueKey = issueKey,
                Ticket = ticket,
                UploadId = uploadId,
                Phase = QueuePhase.Queued,
            };
            enqueued = _queue.TryEnqueue(ticketId);
        }

        if (enqueued)
        {
            _signal.Release();
        }

        RaiseChanged(ticketId);
    }

    /// <summary>Moves a still-queued ticket to the front (FR10: opening its review page jumps the line). No-op
    /// once it has left the Queued phase.</summary>
    public void Prioritize(int ticketId)
    {
        lock (_gate)
        {
            if (_entries.TryGetValue(ticketId, out var entry) && entry.Phase == QueuePhase.Queued)
            {
                _queue.MoveToFront(ticketId);
            }
        }
    }

    /// <summary>Failed -&gt; Queued, attempts reset to 0 (FR9). Returns false if the ticket wasn't Failed.</summary>
    public bool Requeue(int ticketId)
    {
        bool enqueued;
        lock (_gate)
        {
            if (!_entries.TryGetValue(ticketId, out var entry) || entry.Phase != QueuePhase.Failed)
            {
                return false;
            }

            _entries[ticketId] = entry with { Phase = QueuePhase.Queued, Attempts = 0, FailureReason = null };
            enqueued = _queue.TryEnqueue(ticketId);
        }

        if (enqueued)
        {
            _signal.Release();
        }

        RaiseChanged(ticketId);
        return true;
    }

    /// <summary>Waits for and atomically dequeues the next ticket, marking it Analysing and incrementing its
    /// attempt count (FR6/FR8). Only the <see cref="TriageWorker"/> calls this.</summary>
    public async Task<TriageSessionEntry> DequeueAsync(CancellationToken cancellationToken)
    {
        while (true)
        {
            await _signal.WaitAsync(cancellationToken);

            TriageSessionEntry? dequeued = null;
            lock (_gate)
            {
                if (_queue.TryDequeue(out var ticketId) && _entries.TryGetValue(ticketId, out var entry))
                {
                    dequeued = entry with { Phase = QueuePhase.Analysing, Attempts = entry.Attempts + 1 };
                    _entries[ticketId] = dequeued;
                }
            }

            if (dequeued is not null)
            {
                RaiseChanged(dequeued.TicketId);
                return dequeued;
            }

            // The signal count always matches enqueued ids in practice; loop defensively instead of throwing.
        }
    }

    /// <summary>Records a successful analysis (the suggestion is already persisted by <see cref="ISuggestionWriter"/>
    /// at this point - FR7). The DB now reports Pending, so the RAM phase itself is no longer consulted for this ticket.</summary>
    public void CompleteAnalysis(int ticketId, TriageSuggestion suggestion, IReadOnlyList<string> notInCatalog)
    {
        lock (_gate)
        {
            if (_entries.TryGetValue(ticketId, out var entry))
            {
                _entries[ticketId] = entry with { Suggestion = suggestion, NotInCatalog = notInCatalog };
            }
        }

        RaiseChanged(ticketId);
    }

    /// <summary>Records a failed attempt (exception or timeout, FR8): re-queues at the back, or gives up as
    /// Failed once <paramref name="maxAttempts"/> is reached. Never stores a partial suggestion.</summary>
    public void FailAttempt(int ticketId, string reason, int maxAttempts)
    {
        var enqueued = false;
        lock (_gate)
        {
            if (!_entries.TryGetValue(ticketId, out var entry))
            {
                return;
            }

            if (entry.Attempts >= maxAttempts)
            {
                _entries[ticketId] = entry with { Phase = QueuePhase.Failed, FailureReason = reason };
            }
            else
            {
                _entries[ticketId] = entry with { Phase = QueuePhase.Queued, FailureReason = reason };
                enqueued = _queue.TryEnqueue(ticketId);
            }
        }

        if (enqueued)
        {
            _signal.Release();
        }

        RaiseChanged(ticketId);
    }

    public TriageSessionEntry? Get(int ticketId)
    {
        lock (_gate)
        {
            return _entries.GetValueOrDefault(ticketId);
        }
    }

    public IReadOnlyCollection<TriageSessionEntry> Snapshot()
    {
        lock (_gate)
        {
            return [.. _entries.Values];
        }
    }

    public UploadProgress GetUploadProgress(int uploadId)
    {
        lock (_gate)
        {
            var total = 0;
            var analysed = 0;
            foreach (var entry in _entries.Values)
            {
                if (entry.UploadId != uploadId)
                {
                    continue;
                }

                total++;
                if (entry.Suggestion is not null || entry.Phase == QueuePhase.Failed)
                {
                    analysed++;
                }
            }

            return new UploadProgress(analysed, total);
        }
    }

    private void RaiseChanged(int ticketId)
    {
        var handlers = TicketChanged;
        if (handlers is null)
        {
            return;
        }

        foreach (var handler in handlers.GetInvocationList().Cast<Action<int>>())
        {
            try
            {
                handler(ticketId);
            }
            catch (Exception ex)
            {
                // Isolate one bad subscriber from the rest; never log ticket content (NFR6).
                logger.LogWarning(ex, "TicketChanged subscriber failed for ticket {TicketId}.", ticketId);
            }
        }
    }
}
