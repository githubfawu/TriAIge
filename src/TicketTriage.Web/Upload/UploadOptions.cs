using System.ComponentModel.DataAnnotations;

namespace TicketTriage.Web.Upload;

public sealed class UploadOptions
{
    public const string SectionName = "Upload";

    [Range(1, 3600)]
    public int PollIntervalSeconds { get; set; } = 2;

    /// <summary>Longest wait for a live worker; afterwards the page offers the export with fallbacks for pending tickets.</summary>
    [Range(1, 86_400)]
    public int WaitTimeoutSeconds { get; set; } = 1200;

    /// <summary>A worker heartbeat older than this counts as "not running".</summary>
    [Range(1, 86_400)]
    public int WorkerHeartbeatMaxAgeSeconds { get; set; } = 90;

    /// <summary>How long a missing or stale heartbeat is reported as "starting" before it becomes "not running".</summary>
    [Range(1, 86_400)]
    public int WorkerStartGraceSeconds { get; set; } = 120;
}
