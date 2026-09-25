using TicketTriage.Core.Domain;

namespace TicketTriage.Web.Triage;

/// <summary>The fixed baseline a review form starts from: the AI suggestion (or its analyst-edited effective
/// value if the ticket already carries saved edits) - see <see cref="TicketReview.EffectiveSuggestion"/>.</summary>
public sealed record ReviewFormSnapshot(
    WorkType WorkType,
    IReadOnlyList<string> AffectedServices,
    string? ServiceTeam,
    string? Assignee,
    Urgency Urgency,
    Impact Impact,
    ResolutionStatus? ResolutionStatus,
    string? DraftComment)
{
    public static ReviewFormSnapshot From(TriageSuggestion suggestion) => new(
        suggestion.WorkType,
        suggestion.AffectedServices,
        suggestion.ServiceTeams.FirstOrDefault(),
        suggestion.Assignee,
        suggestion.Urgency,
        suggestion.Impact,
        suggestion.ResolutionStatus,
        suggestion.DraftComment);
}

/// <summary>
/// The Review page's editable form state (FR16-FR18). Wraps a fixed <see cref="Baseline"/> (the suggestion) so
/// <see cref="HasChanges"/>/<see cref="EditedFields"/> always compare against what the AI actually proposed -
/// "Accept" means "no edits from the suggestion" (0 edits), "Save" means "at least one edit from the suggestion".
/// </summary>
public sealed class ReviewFormModel
{
    public ReviewFormModel(ReviewFormSnapshot baseline)
    {
        Baseline = baseline;
        AffectedServices = [];
        Reset();
    }

    public ReviewFormSnapshot Baseline { get; }

    public WorkType WorkType { get; set; }

    public List<string> AffectedServices { get; set; }

    public string? ServiceTeam { get; set; }

    public string? Assignee { get; set; }

    public Urgency Urgency { get; set; }

    public Impact Impact { get; set; }

    public ResolutionStatus? Resolution { get; set; }

    public string? Comment { get; set; }

    /// <summary>Never edited directly (FR16): always the deterministic result of the current Urgency/Impact,
    /// recomputed on every read (AC6).</summary>
    public Priority Priority => PriorityMatrix.Resolve(Urgency, Impact);

    public bool HasChanges => EditedFields().Count > 0;

    public IReadOnlyList<SuggestionField> EditedFields()
    {
        var edited = new List<SuggestionField>();

        if (WorkType != Baseline.WorkType)
        {
            edited.Add(SuggestionField.WorkType);
        }

        if (!AffectedServices.ToHashSet(StringComparer.Ordinal).SetEquals(Baseline.AffectedServices))
        {
            edited.Add(SuggestionField.AffectedServices);
        }

        if (!string.Equals(ServiceTeam, Baseline.ServiceTeam, StringComparison.Ordinal))
        {
            edited.Add(SuggestionField.ServiceTeams);
        }

        if (!string.Equals(Assignee, Baseline.Assignee, StringComparison.Ordinal))
        {
            edited.Add(SuggestionField.Assignee);
        }

        if (Urgency != Baseline.Urgency)
        {
            edited.Add(SuggestionField.Urgency);
        }

        if (Impact != Baseline.Impact)
        {
            edited.Add(SuggestionField.Impact);
        }

        if (Resolution != Baseline.ResolutionStatus)
        {
            edited.Add(SuggestionField.ResolutionStatus);
        }

        if (!string.Equals(Comment, Baseline.DraftComment, StringComparison.Ordinal))
        {
            edited.Add(SuggestionField.DraftComment);
        }

        return edited;
    }

    /// <summary>Restores every field to the suggestion it started from.</summary>
    public void Reset()
    {
        WorkType = Baseline.WorkType;
        AffectedServices = [.. Baseline.AffectedServices];
        ServiceTeam = Baseline.ServiceTeam;
        Assignee = Baseline.Assignee;
        Urgency = Baseline.Urgency;
        Impact = Baseline.Impact;
        Resolution = Baseline.ResolutionStatus;
        Comment = Baseline.DraftComment;
    }

    /// <summary>Builds the edits to send to <c>IReviewService</c>, or null when nothing changed.</summary>
    public ReviewEdits? ToEdits()
    {
        var fields = EditedFields();
        if (fields.Count == 0)
        {
            return null;
        }

        return new ReviewEdits
        {
            WorkType = fields.Contains(SuggestionField.WorkType) ? WorkType : null,
            AffectedServices = fields.Contains(SuggestionField.AffectedServices) ? AffectedServices : null,
            ServiceTeams = fields.Contains(SuggestionField.ServiceTeams) ? ServiceTeam : null,
            Assignee = fields.Contains(SuggestionField.Assignee) ? Assignee : null,
            Urgency = fields.Contains(SuggestionField.Urgency) ? Urgency : null,
            Impact = fields.Contains(SuggestionField.Impact) ? Impact : null,
            ResolutionStatus = fields.Contains(SuggestionField.ResolutionStatus) ? Resolution : null,
            DraftComment = fields.Contains(SuggestionField.DraftComment) ? Comment : null,
        };
    }
}
