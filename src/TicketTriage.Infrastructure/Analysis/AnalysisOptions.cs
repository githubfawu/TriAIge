using System.ComponentModel.DataAnnotations;
using Microsoft.Extensions.Options;
using TicketTriage.Infrastructure.Pipeline;

namespace TicketTriage.Infrastructure.Analysis;

/// <summary>Settings of the background analysis worker (<c>Analysis</c> section).</summary>
public sealed class AnalysisOptions
{
    public const string SectionName = "Analysis";

    public bool Enabled { get; set; } = true;

    [Range(1, 3600)]
    public int IntervalSeconds { get; set; } = 2;

    [Range(1, 100)]
    public int BatchSize { get; set; } = 5;

    [Range(1, 240)]
    public int LeaseMinutes { get; set; } = 20;
}

/// <summary>Range checks plus the cross-check that a lease outlives the worst case of one batch, else claims are stolen mid-run.</summary>
internal sealed class AnalysisOptionsValidator(IOptions<TriageOptions> triageOptions) : IValidateOptions<AnalysisOptions>
{
    public ValidateOptionsResult Validate(string? name, AnalysisOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        List<ValidationResult> results = [];
        if (!Validator.TryValidateObject(options, new ValidationContext(options), results, validateAllProperties: true))
        {
            return ValidateOptionsResult.Fail(results.Select(r => r.ErrorMessage ?? "Analysis settings are out of range."));
        }

        var triage = triageOptions.Value;
        var worstCaseSeconds = options.BatchSize * triage.RetryCount
            * (triage.TicketTimeoutSeconds + (triage.RetryDelayMilliseconds / 1000.0));
        if (options.LeaseMinutes * 60.0 < worstCaseSeconds)
        {
            return ValidateOptionsResult.Fail(
                $"Analysis:LeaseMinutes ({options.LeaseMinutes}) must cover the worst case of one batch " +
                $"({worstCaseSeconds:F0} s = BatchSize x RetryCount x (TicketTimeoutSeconds + RetryDelayMilliseconds/1000)).");
        }

        return ValidateOptionsResult.Success;
    }
}
