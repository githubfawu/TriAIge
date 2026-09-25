namespace TicketTriage.Core.Domain;

/// <summary>One stored analyst change relative to the AI suggestion; values are enum names, JSON lists or plain text.</summary>
public sealed record SuggestionEditInfo(SuggestionField Field, string? AiValue, string? FinalValue, DateTime EditedAtUtc);

/// <summary>Review state of a suggestion: decision, reject reason and timestamps.</summary>
public sealed record ReviewDecisionInfo(
    ReviewDecision Decision,
    string? RejectReason,
    DateTime? FirstOpenedAtUtc,
    DateTime? DecidedAtUtc);

/// <summary>Everything a reviewer needs for one stored ticket.</summary>
/// <param name="Suggestion">The AI suggestion as stored, never altered by edits.</param>
/// <param name="EffectiveSuggestion">The suggestion with the analyst edits applied; its priority is derived via <see cref="PriorityMatrix"/>.</param>
/// <param name="Version">Row version to pass back as the expected version when saving.</param>
/// <param name="IsAnalysing">The ticket has no suggestion yet (status New).</param>
/// <param name="IsFailed">The analysis exhausted its retries without producing a suggestion.</param>
public sealed record TicketReview(
    Ticket Ticket,
    TriageSuggestion? Suggestion,
    TriageSuggestion? EffectiveSuggestion,
    string StatusName,
    long Version,
    bool IsAnalysing,
    bool IsFailed,
    ReviewDecisionInfo? Decision,
    IReadOnlyList<SuggestionEditInfo> Edits);

/// <summary>Field values the analyst changed; null means untouched. Priority is not editable, it follows urgency and impact.</summary>
public sealed record ReviewEdits
{
    public WorkType? WorkType { get; init; }

    public IReadOnlyList<string>? AffectedServices { get; init; }

    public string? ServiceTeams { get; init; }

    public string? Assignee { get; init; }

    public Urgency? Urgency { get; init; }

    public Impact? Impact { get; init; }

    public ResolutionStatus? ResolutionStatus { get; init; }

    public string? DraftComment { get; init; }

    public bool HasAny =>
        WorkType is not null || AffectedServices is not null || ServiceTeams is not null || Assignee is not null
        || Urgency is not null || Impact is not null || ResolutionStatus is not null || DraftComment is not null;
}

public enum ReviewOutcome
{
    Success,
    NotFound,
    Conflict,
    InvalidState,
    Invalid,
}

/// <param name="Version">The new row version on success, otherwise null.</param>
public sealed record ReviewResult(ReviewOutcome Outcome, long? Version, string? Message)
{
    public bool IsSuccess => Outcome == ReviewOutcome.Success;
}
