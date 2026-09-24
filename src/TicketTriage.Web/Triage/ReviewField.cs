namespace TicketTriage.Web.Triage;

/// <summary>Editable review fields (FR16). Wired into the Review page and badges in Slice 2; declared now so the
/// RAM store's <c>EditedFields</c> shape is stable across slices.</summary>
public enum ReviewField
{
    WorkType,
    AffectedService,
    ServiceTeam,
    Assignee,
    Urgency,
    Impact,
    Resolution,
    Comment,
}

public enum FieldOwner
{
    Code,
    Llm,
    LlmAndCode,
}

/// <summary>Who determines each editable field (FR17, architecture.md badge colours) - used by the Review page in Slice 2.</summary>
public static class ReviewFieldOwners
{
    public static FieldOwner Owner(this ReviewField field) => field switch
    {
        ReviewField.ServiceTeam or ReviewField.Assignee => FieldOwner.Code,
        ReviewField.WorkType or ReviewField.AffectedService => FieldOwner.Llm,
        ReviewField.Urgency or ReviewField.Impact or ReviewField.Resolution or ReviewField.Comment => FieldOwner.LlmAndCode,
        _ => throw new ArgumentOutOfRangeException(nameof(field), field, "Unknown review field."),
    };
}
