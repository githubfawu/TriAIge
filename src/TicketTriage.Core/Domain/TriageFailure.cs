namespace TicketTriage.Core.Domain;

/// <summary>One failed triage attempt for a ticket, kept for diagnostics.</summary>
public sealed record TriageFailure(
    int? TicketId,
    string TicketKey,
    int Attempt,
    string Reason,
    string ExceptionType,
    string? StackTrace);
