namespace TicketTriage.Web.Triage;

/// <summary>Session-scoped (RAM-only) metrics for the dashboard (FR22/AC8/AC13) - reset on restart along with the
/// rest of <see cref="TriageSessionStore"/> (Out of Scope: persisting them). Deliberately a pure function over a
/// <see cref="TriageSessionStore.Snapshot"/> so it's testable without a DB or a running worker.</summary>
public sealed record SessionMetricsSnapshot(
    int ApprovedCount,
    int RejectedCount,
    double? AcceptanceRate,
    IReadOnlyDictionary<ReviewField, int> EditsPerField,
    TimeSpan? AverageUploadToFirstOpen,
    TimeSpan? AverageFirstOpenToDecision)
{
    public static readonly SessionMetricsSnapshot Empty = new(0, 0, null, EmptyEditsPerField(), null, null);

    private static IReadOnlyDictionary<ReviewField, int> EmptyEditsPerField() =>
        Enum.GetValues<ReviewField>().ToDictionary(field => field, _ => 0);
}

public static class SessionMetrics
{
    /// <summary>A <see cref="TriageSessionEntry"/> was decided this session once <see cref="TriageSessionEntry.DecidedAt"/>
    /// is set. <see cref="ReviewDecisionService"/> always passes a null reject reason for Approve and always
    /// requires a non-empty one for Reject, so the two decisions can be told apart without a dedicated RAM field.</summary>
    public static SessionMetricsSnapshot Compute(IReadOnlyCollection<TriageSessionEntry> entries)
    {
        var decided = entries.Where(e => e.DecidedAt is not null).ToList();
        var approved = decided.Where(e => e.RejectReason is null).ToList();
        var rejectedCount = decided.Count - approved.Count;

        var acceptanceRate = approved.Count == 0
            ? (double?)null
            : (double)approved.Count(e => e.EditedFields.Count == 0) / approved.Count;

        var editsPerField = Enum.GetValues<ReviewField>().ToDictionary(field => field, _ => 0);
        foreach (var fieldName in approved.SelectMany(e => e.EditedFields))
        {
            if (Enum.TryParse<ReviewField>(fieldName, out var field))
            {
                editsPerField[field]++;
            }
        }

        var uploadToOpen = Average(entries
            .Where(e => e.FirstOpenedAt is not null)
            .Select(e => e.FirstOpenedAt!.Value - e.RegisteredAt));

        var openToDecision = Average(decided
            .Where(e => e.FirstOpenedAt is not null)
            .Select(e => e.DecidedAt!.Value - e.FirstOpenedAt!.Value));

        return new SessionMetricsSnapshot(approved.Count, rejectedCount, acceptanceRate, editsPerField, uploadToOpen, openToDecision);
    }

    private static TimeSpan? Average(IEnumerable<TimeSpan> spans)
    {
        var list = spans.ToList();
        return list.Count == 0 ? null : TimeSpan.FromTicks((long)list.Average(span => span.Ticks));
    }
}
