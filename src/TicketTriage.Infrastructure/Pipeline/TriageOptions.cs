using System.ComponentModel.DataAnnotations;

namespace TicketTriage.Infrastructure.Pipeline;

public sealed class TriageOptions
{
    public const string SectionName = "Triage";

    [Range(1, 10)]
    public int RetryCount { get; set; } = 3;

    /// <summary>
    /// Stops the host on the first failure of a ticket. Development / Batch only; never enable in production Web,
    /// where a single bad ticket would take the whole app down.
    /// </summary>
    public bool StopSystemOnFailure { get; set; }

    [Range(1, 300)]
    public int TicketTimeoutSeconds { get; set; } = 60;

    [Range(1, 50)]
    public int SimilarTicketCount { get; set; } = 10;

    [Range(0, 10000)]
    public int RetryDelayMilliseconds { get; set; } = 500;

    // Validator directly: ValidateDataAnnotations lives in a package this project does not reference.
    internal static bool IsValid(TriageOptions options) =>
        Validator.TryValidateObject(options, new ValidationContext(options), validationResults: null, validateAllProperties: true);
}
