using TicketTriage.Core.Domain;

namespace TicketTriage.Infrastructure.Persistence;

/// <summary>Stored AI suggestion (1:1 with a ticket) plus the review state of the analyst.</summary>
public sealed class TriageSuggestionEntity
{
    public int TicketId { get; set; }

    public WorkType WorkType { get; set; }

    public List<string> AffectedServices { get; set; } = [];

    public List<string> ServiceTeams { get; set; } = [];

    public string? Assignee { get; set; }

    public Urgency Urgency { get; set; }

    public Impact Impact { get; set; }

    public Priority Priority { get; set; }

    public ResolutionStatus? ResolutionStatus { get; set; }

    public string? DraftComment { get; set; }

    public List<string> SimilarTicketKeys { get; set; } = [];

    public bool IsFallback { get; set; }

    public DateTime CreatedAtUtc { get; set; }

    public ReviewDecision Decision { get; set; }

    public string? RejectReason { get; set; }

    public DateTime? FirstOpenedAtUtc { get; set; }

    public DateTime? DecidedAtUtc { get; set; }
}
