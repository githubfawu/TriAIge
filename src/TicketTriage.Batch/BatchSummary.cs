namespace TicketTriage.Batch;

/// <summary>Outcome of one batch run; <paramref name="NotAnalysed"/> tickets were exported with the deterministic fallback.</summary>
public sealed record BatchSummary(int Total, int Fallbacks, TimeSpan Duration, string OutputPath, int NotAnalysed = 0);
