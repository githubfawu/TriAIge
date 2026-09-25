namespace TicketTriage.Web.Triage;

/// <summary>
/// Web-only display state (FR11), derived from the ticket's status/claim/retry facts (see
/// <see cref="TicketBoardQuery"/>) - never persisted, never confused with <see cref="Core.Domain.ReviewDecision"/>
/// (Pending/Approved/Rejected map 1:1 onto that Core enum).
/// </summary>
public enum TicketDisplayState
{
    Queued,
    Analysing,
    Failed,
    Pending,
    Approved,
    Rejected,
}
