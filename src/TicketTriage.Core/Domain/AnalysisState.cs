namespace TicketTriage.Core.Domain;

/// <summary>Analysis progress of one stored ticket; <see cref="Suggestion"/> is null while <see cref="IsPending"/>.</summary>
public sealed record AnalysisState(int TicketId, Ticket Ticket, bool IsPending, TriageSuggestion? Suggestion);
