using TicketTriage.Core.Domain;

namespace TicketTriage.Core.Abstractions;

/// <summary>Finds the most similar historical tickets from the training set.</summary>
public interface ISimilarTicketRetriever
{
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

/// <summary>Drafts a resolution comment for the analyst.</summary>
public interface IResolutionDrafter
{
    Task<string> DraftAsync(
        Ticket ticket,
        TicketClassification classification,
        IReadOnlyList<SimilarTicket> similarTickets,
        CancellationToken cancellationToken);
}

/// <summary>End-to-end triage: retrieve -> classify -> route -> prioritize -> draft.</summary>
public interface ITriagePipeline
{
    Task<TriageSuggestion> TriageAsync(Ticket ticket, CancellationToken cancellationToken);
}
