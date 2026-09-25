using TicketTriage.Core.Domain;

namespace TicketTriage.Web.Triage;

public enum FieldOwner
{
    Code,
    Llm,
    LlmAndCode,
}

/// <summary>Who determines each editable field of a <see cref="TriageSuggestion"/> (FR17, architecture.md badge
/// colours). <see cref="SuggestionField"/> has no <c>Priority</c> entry because priority is never edited directly -
/// it is always <see cref="PriorityMatrix.Resolve(Urgency, Impact)"/>.</summary>
public static class SuggestionFieldOwners
{
    public static FieldOwner Owner(this SuggestionField field) => field switch
    {
        SuggestionField.ServiceTeams or SuggestionField.Assignee => FieldOwner.Code,
        SuggestionField.WorkType or SuggestionField.AffectedServices => FieldOwner.Llm,
        SuggestionField.Urgency or SuggestionField.Impact or SuggestionField.ResolutionStatus or SuggestionField.DraftComment => FieldOwner.LlmAndCode,
        _ => throw new ArgumentOutOfRangeException(nameof(field), field, "Unknown suggestion field."),
    };
}
