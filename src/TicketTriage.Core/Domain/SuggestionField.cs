namespace TicketTriage.Core.Domain;

/// <summary>The editable fields of a <see cref="TriageSuggestion"/>; priority is absent because it is derived from urgency and impact.</summary>
public enum SuggestionField
{
    WorkType,
    AffectedServices,
    ServiceTeams,
    Assignee,
    Urgency,
    Impact,
    ResolutionStatus,
    DraftComment,
}
