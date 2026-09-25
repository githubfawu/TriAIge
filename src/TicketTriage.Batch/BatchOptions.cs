namespace TicketTriage.Batch;

public sealed class BatchOptions
{
    public const string SectionName = "Batch";

    /// <summary>Path to the challenge tickets JSON.</summary>
    public string Input { get; set; } = string.Empty;

    /// <summary>Path of the result JSON to write.</summary>
    public string Output { get; set; } = string.Empty;

    /// <summary>How often the stored analysis state is polled.</summary>
    public int PollIntervalSeconds { get; set; } = 5;

    /// <summary>Longest wait for a live worker to finish; afterwards still-new tickets get the deterministic fallback.</summary>
    public int WaitTimeoutSeconds { get; set; } = 1200;

    /// <summary>A worker heartbeat older than this counts as "not running".</summary>
    public int WorkerHeartbeatMaxAgeSeconds { get; set; } = 90;

    /// <summary>How long a missing or stale heartbeat is tolerated (the worker may be starting) before the run is aborted.</summary>
    public int WorkerStartGraceSeconds { get; set; } = 120;

    public static bool IsValid(BatchOptions options) =>
        options.PollIntervalSeconds > 0
        && options.WaitTimeoutSeconds > 0
        && options.WorkerHeartbeatMaxAgeSeconds > 0
        && options.WorkerStartGraceSeconds > 0;
}
