namespace TicketTriage.Infrastructure.Analysis;

public static class WorkerLiveness
{
    /// <summary>True when the worker ticked within <paramref name="maxAge"/>; no heartbeat means it never ran.</summary>
    public static bool IsAlive(DateTime? heartbeatUtc, DateTimeOffset now, TimeSpan maxAge) =>
        heartbeatUtc is { } beat && now.UtcDateTime - beat <= maxAge;
}
