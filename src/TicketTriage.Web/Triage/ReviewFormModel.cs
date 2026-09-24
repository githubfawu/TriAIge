using TicketTriage.Core.Domain;

namespace TicketTriage.Web.Triage;

/// <summary>The fixed baseline a review form starts from: the suggestion, falling back to the original ticket
/// field-by-field wherever the pipeline made no (usable) suggestion (Leitplanke 6). WorkType, Urgency and Impact
/// are never a fallback concern here because <see cref="SuggestionWriter"/> always writes them once a ticket has
/// any suggestion at all (FR7) - only the catalog-validated/free-text fields can be empty.</summary>
public sealed record ReviewFormSnapshot(
    WorkType WorkType,
    string? AffectedService,
    string? ServiceTeam,
    string? Assignee,
    Urgency Urgency,
    Impact Impact,
    ResolutionStatus? Resolution,
    string? Comment);

/// <summary>Already-resolved (name/enum) original and suggested values for one ticket, used only to build the
/// review form's baseline. Kept free of DB/catalog types so the fallback rule is unit-testable without a database -
/// <see cref="TriageBoardQuery"/> is the only caller, translating ids via <see cref="LookupCatalog"/> first.</summary>
public sealed record ReviewFieldInputs(
    WorkType WorkType,
    Urgency Urgency,
    Impact Impact,
    string? ChangedAffectedService,
    string? OriginalAffectedService,
    string? ChangedServiceTeam,
    string? OriginalServiceTeam,
    string? ChangedAssignee,
    string? OriginalAssignee,
    ResolutionStatus? ChangedResolution,
    ResolutionStatus? OriginalResolution);

/// <summary>
/// The Review page's editable form state (FR16-FR18). Wraps a fixed <see cref="Baseline"/> (the suggestion, or the
/// original where the suggestion was empty) so <see cref="HasChanges"/>/<see cref="EditedFields"/> always compare
/// against what the AI actually proposed, never against the original ticket - "Accept" means "no edits from the
/// suggestion" (0 edits), "Save" means "at least one edit from the suggestion" (FR18).
/// </summary>
public sealed class ReviewFormModel
{
    public ReviewFormModel(ReviewFormSnapshot baseline)
    {
        Baseline = baseline;
        Reset();
    }

    public ReviewFormSnapshot Baseline { get; }

    public WorkType WorkType { get; set; }

    public string? AffectedService { get; set; }

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

    public IReadOnlyList<ReviewField> EditedFields()
    {
        var edited = new List<ReviewField>();

        if (WorkType != Baseline.WorkType)
        {
            edited.Add(ReviewField.WorkType);
        }

        if (!string.Equals(AffectedService, Baseline.AffectedService, StringComparison.Ordinal))
        {
            edited.Add(ReviewField.AffectedService);
        }

        if (!string.Equals(ServiceTeam, Baseline.ServiceTeam, StringComparison.Ordinal))
        {
            edited.Add(ReviewField.ServiceTeam);
        }

        if (!string.Equals(Assignee, Baseline.Assignee, StringComparison.Ordinal))
        {
            edited.Add(ReviewField.Assignee);
        }

        if (Urgency != Baseline.Urgency)
        {
            edited.Add(ReviewField.Urgency);
        }

        if (Impact != Baseline.Impact)
        {
            edited.Add(ReviewField.Impact);
        }

        if (Resolution != Baseline.Resolution)
        {
            edited.Add(ReviewField.Resolution);
        }

        if (!string.Equals(Comment, Baseline.Comment, StringComparison.Ordinal))
        {
            edited.Add(ReviewField.Comment);
        }

        return edited;
    }

    /// <summary>Restores every field to the suggestion (or original fallback) it started from.</summary>
    public void Reset()
    {
        WorkType = Baseline.WorkType;
        AffectedService = Baseline.AffectedService;
        ServiceTeam = Baseline.ServiceTeam;
        Assignee = Baseline.Assignee;
        Urgency = Baseline.Urgency;
        Impact = Baseline.Impact;
        Resolution = Baseline.Resolution;
        Comment = Baseline.Comment;
    }

    /// <summary>Builds the baseline the form starts from: the suggestion field-by-field, falling back to the
    /// original wherever the pipeline made no usable suggestion (Leitplanke 6). The draft comment has no
    /// "original" counterpart (RAM-only) and is seeded separately by the caller.</summary>
    public static ReviewFormSnapshot BuildBaseline(ReviewFieldInputs inputs, string? draftComment) => new(
        inputs.WorkType,
        inputs.ChangedAffectedService ?? inputs.OriginalAffectedService,
        inputs.ChangedServiceTeam ?? inputs.OriginalServiceTeam,
        inputs.ChangedAssignee ?? inputs.OriginalAssignee,
        inputs.Urgency,
        inputs.Impact,
        inputs.ChangedResolution ?? inputs.OriginalResolution,
        draftComment);
}
