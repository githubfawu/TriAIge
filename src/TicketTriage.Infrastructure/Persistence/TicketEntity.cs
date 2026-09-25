using TicketTriage.Core.Domain;

namespace TicketTriage.Infrastructure.Persistence;

/// <summary>
/// A ticket in the normalized schema. Each classification field has an original value and a
/// "_changed" counterpart holding the AI's re-classification, pending human review.
/// </summary>
public sealed class TicketEntity
{
    public int Id { get; set; }

    public int WorkTypeId { get; set; }

    public int? WorkTypeChangedId { get; set; }

    public required string Summary { get; set; }

    public string? Description { get; set; }

    public int? AffectedBusinessOrITServiceId { get; set; }

    public int? AffectedBusinessOrITServiceChangedId { get; set; }

    public int? BusinessEntityId { get; set; }

    public int? BusinessEntityChangedId { get; set; }

    public int? ServiceTeamId { get; set; }

    public int? ServiceTeamChangedId { get; set; }

    public string? Reporter { get; set; }

    public string? Assignee { get; set; }

    public string? AssigneeChanged { get; set; }

    public int? PriorityId { get; set; }

    public int? PriorityChangedId { get; set; }

    public int? UrgencyId { get; set; }

    public int? UrgencyChangedId { get; set; }

    public int? ImpactId { get; set; }

    public int? ImpactChangedId { get; set; }

    public DateTime CreatedDate { get; set; }

    // FK to Status (0 = New, 1 = Finished).
    public int StatusId { get; set; }

    public int? StatusChangedId { get; set; }

    public string? Resolution { get; set; }

    public string? ResolutionChanged { get; set; }

    public DateTime? ResolutionDate { get; set; }

    public DateTime? ResolutionDateChanged { get; set; }

    public int Retries { get; set; }

    public TicketOrigin Origin { get; set; }

    /// <summary>Stable key within <see cref="Origin"/> for ingested tickets; null for training tickets.</summary>
    public string? SourceKey { get; set; }

    /// <summary>SHA-256 (hex) of the received payload, to detect changed re-ingests.</summary>
    public string? SourceHash { get; set; }

    public string? SourcePayload { get; set; }

    public DateTime? IngestedAt { get; set; }

    /// <summary>Analysis lease (UTC); non-null while a worker owns the ticket.</summary>
    public DateTime? ClaimedAt { get; set; }

    public long Version { get; set; }

    public List<CommentEntity> Comments { get; set; } = [];
}
