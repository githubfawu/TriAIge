using TicketTriage.Core.Domain;

namespace TicketTriage.Core.Abstractions;

/// <summary>Supplies the tickets to triage as a stream.</summary>
public interface ITicketSource
{
    IAsyncEnumerable<Ticket> GetTicketsAsync(CancellationToken cancellationToken);
}

/// <summary>Stores received tickets (challenge file, intake) as rows the analysis worker picks up.</summary>
public interface ITicketIngestor
{
    /// <summary>
    /// Upserts by (<paramref name="origin"/>, <see cref="Ticket.Key"/>) and returns one result per input position, in order.
    /// </summary>
    /// <exception cref="ArgumentException">A ticket has no key, or <paramref name="origin"/> is <see cref="TicketOrigin.Training"/>.</exception>
    Task<IReadOnlyList<IngestResult>> IngestAsync(
        IReadOnlyList<Ticket> tickets,
        TicketOrigin origin,
        CancellationToken cancellationToken);
}

/// <summary>Finds the most similar historical tickets from the training set.</summary>
public interface ISimilarTicketSource
{
    /// <summary>Returns up to <paramref name="top"/> similar tickets; never contains the ticket itself.</summary>
    Task<IReadOnlyList<SimilarTicket>> FindSimilarAsync(Ticket ticket, int top, CancellationToken cancellationToken);
}

/// <summary>Classifies a ticket (work type, affected services, urgency, impact).</summary>
public interface ITicketClassifier
{
    Task<TicketClassification> ClassifyAsync(
        Ticket ticket,
        IReadOnlyList<SimilarTicket> similarTickets,
        CancellationToken cancellationToken);
}

/// <summary>Determines service team(s) and assignee for a classified ticket.</summary>
public interface IRoutingResolver
{
    Task<RoutingDecision> ResolveAsync(
        Ticket ticket,
        TicketClassification classification,
        IReadOnlyList<SimilarTicket> similarTickets,
        CancellationToken cancellationToken);
}

/// <summary>Drafts the resolution status and comment for the analyst, in the voice of the routed assignee.</summary>
public interface IResolutionDrafter
{
    Task<ResolutionDraft> DraftAsync(
        Ticket ticket,
        TicketClassification classification,
        RoutingDecision routing,
        IReadOnlyList<SimilarTicket> similarTickets,
        CancellationToken cancellationToken);
}

/// <summary>Persists failed triage attempts and counts retries on the ticket.</summary>
public interface ITriageFailureStore
{
    /// <summary>Returns the persisted retry count, or null when the ticket has no Id or no row matches.</summary>
    Task<int?> RecordFailureAsync(TriageFailure failure, CancellationToken cancellationToken);

    /// <summary>Sets the ticket's persisted retry count back to zero after a successful triage.</summary>
    Task ResetRetriesAsync(int ticketId, CancellationToken cancellationToken);
}

/// <summary>End-to-end triage: retrieve -> classify -> route -> prioritize -> draft.</summary>
public interface ITriagePipeline
{
    Task<TriageSuggestion> TriageAsync(Ticket ticket, CancellationToken cancellationToken);

    /// <summary>Triages the stream sequentially and yields one suggestion per ticket in input order.</summary>
    IAsyncEnumerable<TriageSuggestion> TriageAsync(
        IAsyncEnumerable<Ticket> tickets,
        CancellationToken cancellationToken);
}

/// <summary>Read side of the background analysis, used to wait for stored suggestions.</summary>
public interface IAnalysisMonitor
{
    /// <summary>One state per requested id, in input order.</summary>
    Task<IReadOnlyList<AnalysisState>> GetStatesAsync(IReadOnlyList<int> ticketIds, CancellationToken cancellationToken);

    /// <summary>UTC time of the last analysis worker tick, or null when no worker ever ran against this database.</summary>
    Task<DateTime?> GetWorkerHeartbeatAsync(CancellationToken cancellationToken);
}

/// <summary>Persists the analyst's review of a stored suggestion; never runs an analysis.</summary>
public interface IReviewService
{
    /// <summary>Returns the review view (null for unknown or training tickets) and records the first opening once.</summary>
    Task<TicketReview?> OpenAsync(int ticketId, CancellationToken cancellationToken);

    /// <summary>Stores the edits and moves the ticket to Reviewed.</summary>
    Task<ReviewResult> SaveEditsAsync(int ticketId, long expectedVersion, ReviewEdits edits, CancellationToken cancellationToken);

    /// <summary>Applies optional edits, then approves.</summary>
    Task<ReviewResult> ApproveAsync(int ticketId, long expectedVersion, ReviewEdits? edits, CancellationToken cancellationToken);

    /// <summary>Rejects the suggestion; a reason of 1-500 characters is required.</summary>
    Task<ReviewResult> RejectAsync(int ticketId, long expectedVersion, string reason, CancellationToken cancellationToken);

    /// <summary>Resets a failed ticket (retries exhausted) to New so the worker analyses it again; drops its suggestion and edits.</summary>
    Task<ReviewResult> RequeueFailedAsync(int ticketId, long expectedVersion, CancellationToken cancellationToken);
}

/// <summary>Builds the deterministic (no LLM) suggestion for a ticket the worker did not analyse.</summary>
public interface IFallbackSuggestionProvider
{
    Task<TriageSuggestion> CreateAsync(Ticket ticket, CancellationToken cancellationToken);
}

/// <summary>Aggregates review outcomes of stored suggestions (acceptance rate, edits per field, latencies).</summary>
public interface ITriageMetricsService
{
    Task<TriageMetrics> GetAsync(CancellationToken cancellationToken);
}
