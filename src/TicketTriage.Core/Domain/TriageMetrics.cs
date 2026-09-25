namespace TicketTriage.Core.Domain;

/// <summary>Review quality figures over all non-training tickets with a stored suggestion (FR-25).</summary>
/// <param name="AcceptanceRate">Approved without edits / decided; null while nothing is decided.</param>
public sealed record TriageMetrics(
    int Suggested,
    int Decided,
    int Approved,
    int ApprovedWithoutEdits,
    int Rejected,
    double? AcceptanceRate,
    IReadOnlyDictionary<SuggestionField, int> EditsPerField,
    TimeSpan? MedianIngestToFirstOpen,
    TimeSpan? MedianIngestToDecision);
