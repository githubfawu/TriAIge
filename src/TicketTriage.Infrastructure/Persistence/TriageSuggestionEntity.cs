using TicketTriage.Core.Domain;

namespace TicketTriage.Infrastructure.Persistence;

/// <summary>
/// A stored triage suggestion plus the analyst's review. Used for the HITL workflow
/// and later for acceptance-rate metrics (approved vs. edited vs. rejected).
/// </summary>
public sealed class TriageSuggestionEntity
{
    public Guid Id { get; set; }

    public required string TicketKey { get; set; }

    /// <summary>The <see cref="TriageSuggestion"/> as produced by the pipeline, serialized as JSON.</summary>
    public required string SuggestionJson { get; set; }

    public ReviewDecision Decision { get; set; } = ReviewDecision.Pending;

    /// <summary>The analyst-corrected <see cref="TriageSuggestion"/> as JSON; set when <see cref="Decision"/> is Edited.</summary>
    public string? EditedJson { get; set; }

    public string? ReviewerComment { get; set; }

    public DateTimeOffset CreatedAt { get; set; }

    public DateTimeOffset? ReviewedAt { get; set; }
}
