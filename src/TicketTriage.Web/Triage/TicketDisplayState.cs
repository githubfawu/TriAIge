namespace TicketTriage.Web.Triage;

/// <summary>
/// Web-only display state (FR11), derived from DB facts and the RAM queue - never persisted, never confused
/// with <see cref="Core.Domain.ReviewDecision"/> (Pending/Approved/Rejected map 1:1 onto that Core enum).
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

/// <summary>The DB facts <see cref="TicketDisplayStateMapper"/> needs; deliberately narrow so callers can't
/// accidentally derive state from anything but names/ids resolved through <see cref="LookupCatalog"/>.</summary>
public readonly record struct TicketDbFacts(int TicketId, int StatusId, bool HasSuggestion);

/// <summary>Derives the Web-only display state (Leitplanke 1). Order matters: a human decision always wins,
/// then an analysed-but-undecided ticket is Pending, and only then does the RAM queue phase apply.</summary>
public static class TicketDisplayStateMapper
{
    public static TicketDisplayState? Derive(TicketDbFacts facts, TriageSessionEntry? ramEntry, LookupCatalog catalog)
    {
        if (facts.StatusId == catalog.HumanApprovedStatusId)
        {
            return TicketDisplayState.Approved;
        }

        if (facts.StatusId == catalog.HumanRejectedStatusId)
        {
            return TicketDisplayState.Rejected;
        }

        if (facts.StatusId == catalog.NewStatusId && facts.HasSuggestion)
        {
            return TicketDisplayState.Pending;
        }

        return ramEntry?.Phase switch
        {
            QueuePhase.Queued => TicketDisplayState.Queued,
            QueuePhase.Analysing => TicketDisplayState.Analysing,
            QueuePhase.Failed => TicketDisplayState.Failed,
            _ => null,
        };
    }
}
