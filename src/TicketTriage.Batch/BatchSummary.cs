namespace TicketTriage.Batch;

/// <summary>Outcome of one batch run.</summary>
public sealed record BatchSummary(int Total, int Fallbacks, TimeSpan Duration, string OutputPath);
