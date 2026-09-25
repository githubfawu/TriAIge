namespace TicketTriage.Core.Domain;

/// <summary>A historical ticket returned by similarity search (higher score = more similar).</summary>
public sealed record SimilarTicket(Ticket Ticket, double Score);

/// <summary>Classification output. Priority is intentionally absent (see <see cref="PriorityMatrix"/>).</summary>
public sealed record TicketClassification(
    WorkType WorkType,
    IReadOnlyList<string> AffectedServices,
    Urgency Urgency,
    Impact Impact);

/// <summary>Drafter output: the resolution status and the comment for the analyst.</summary>
public sealed record ResolutionDraft(ResolutionStatus Status, string Comment);

/// <summary>Routing target for a ticket.</summary>
public sealed record RoutingDecision(IReadOnlyList<string> ServiceTeams, string? Assignee);
