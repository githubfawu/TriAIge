using Microsoft.EntityFrameworkCore;
using TicketTriage.Core.Abstractions;
using TicketTriage.Core.Domain;
using TicketTriage.Infrastructure.Persistence;

namespace TicketTriage.Infrastructure.Review;

public sealed class EfTriageMetricsService(IDbContextFactory<TriageDbContext> dbFactory) : ITriageMetricsService
{
    public async Task<TriageMetrics> GetAsync(CancellationToken cancellationToken)
    {
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);

        var rows = await (
            from s in db.Suggestions.AsNoTracking()
            join t in db.Tickets.AsNoTracking() on s.TicketId equals t.Id
            where t.Origin != TicketOrigin.Training
            select new { s.TicketId, s.Decision, s.FirstOpenedAtUtc, s.DecidedAtUtc, t.IngestedAt })
            .ToListAsync(cancellationToken);

        var edits = await (
            from e in db.SuggestionEdits.AsNoTracking()
            join t in db.Tickets.AsNoTracking() on e.TicketId equals t.Id
            where t.Origin != TicketOrigin.Training
            select new { e.TicketId, e.Field })
            .ToListAsync(cancellationToken);

        var editedTickets = edits.Select(e => e.TicketId).ToHashSet();
        var perField = Enum.GetValues<SuggestionField>().ToDictionary(f => f, _ => 0);
        foreach (var edit in edits)
        {
            if (Enum.TryParse<SuggestionField>(edit.Field, out var field))
            {
                perField[field]++;
            }
        }

        var approvedRows = rows.Where(r => r.Decision == ReviewDecision.Approved).ToList();
        var approved = approvedRows.Count;
        var rejected = rows.Count(r => r.Decision == ReviewDecision.Rejected);
        var decided = approved + rejected;
        var unchanged = approvedRows.Count(r => !editedTickets.Contains(r.TicketId));

        var firstOpen = rows
            .Where(r => r.FirstOpenedAtUtc is not null && r.IngestedAt is not null)
            .Select(r => r.FirstOpenedAtUtc!.Value - r.IngestedAt!.Value);
        var toDecision = rows
            .Where(r => r.DecidedAtUtc is not null && r.IngestedAt is not null)
            .Select(r => r.DecidedAtUtc!.Value - r.IngestedAt!.Value);

        return new TriageMetrics(
            rows.Count,
            decided,
            approved,
            unchanged,
            rejected,
            decided == 0 ? null : (double)unchanged / decided,
            perField,
            Median(firstOpen),
            Median(toDecision));
    }

    private static TimeSpan? Median(IEnumerable<TimeSpan> values)
    {
        var sorted = values.Order().ToArray();
        if (sorted.Length == 0)
        {
            return null;
        }

        var mid = sorted.Length / 2;
        return sorted.Length % 2 == 1 ? sorted[mid] : (sorted[mid - 1] + sorted[mid]) / 2;
    }
}
