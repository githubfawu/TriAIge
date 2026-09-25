namespace TicketTriage.Batch;

/// <summary>The Web analysis worker is not running, so the tickets cannot be analysed; distinct from bad input (exit code 3).</summary>
public sealed class BatchWorkerUnavailableException(string message) : Exception(message);
