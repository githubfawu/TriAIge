using TicketTriage.Core.Domain;

namespace TicketTriage.Core.Abstractions;

/// <summary>Supplies the tickets to triage as a stream.</summary>
public interface ITicketSource
{
    IAsyncEnumerable<Ticket> GetTicketsAsync(CancellationToken cancellationToken);
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
