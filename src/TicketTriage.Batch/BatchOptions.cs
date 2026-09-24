namespace TicketTriage.Batch;

public sealed class BatchOptions
{
    public const string SectionName = "Batch";

    /// <summary>Path to the challenge tickets JSON.</summary>
    public string Input { get; set; } = string.Empty;

    /// <summary>Path of the result JSON to write.</summary>
    public string Output { get; set; } = string.Empty;
}
