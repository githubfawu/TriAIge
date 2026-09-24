using System.Diagnostics;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using TicketTriage.Core.Abstractions;
using TicketTriage.Core.Domain;
using TicketTriage.Infrastructure.Persistence;
using TicketTriage.Infrastructure.Sources;

namespace TicketTriage.Infrastructure.Retrieval;

/// <summary>Finds similar historical tickets via the in-memory TF-IDF index over descriptions (FR1).</summary>
internal sealed class DbSimilarTicketSource(
    IDbContextFactory<TriageDbContext> dbFactory,
    SimilarTicketIndexProvider indexProvider,
    LookupNamesProvider lookupNames,
    ILogger<DbSimilarTicketSource> logger) : ISimilarTicketSource
{
    public async Task<IReadOnlyList<SimilarTicket>> FindSimilarAsync(Ticket ticket, int top, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(ticket);

        if (top <= 0 || string.IsNullOrWhiteSpace(ticket.Description))
        {
            return [];
        }

        var started = Stopwatch.GetTimestamp();
        var index = await indexProvider.GetAsync(cancellationToken);
        var hits = index.Search(ticket.Description, top, ticket.Id);
        if (hits.Count == 0)
        {
            return [];
        }

        var ids = hits.Select(h => h.Id).ToList();
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var entities = await db.Tickets.AsNoTracking()
            .Include(t => t.Comments)
            .Where(t => ids.Contains(t.Id))
            .ToListAsync(cancellationToken);
        var names = await lookupNames.GetAsync(cancellationToken);
        var byId = entities.ToDictionary(e => e.Id);

        // A row deleted since the index was built is skipped.
        List<SimilarTicket> result = [];
        foreach (var hit in hits)
        {
            if (byId.TryGetValue(hit.Id, out var entity))
            {
                result.Add(new SimilarTicket(TicketEntityMapper.ToTicket(entity, names), hit.Score));
            }
        }

        logger.LogDebug(
            "Similar-ticket search for {TicketKey}: {HitCount} hits in {ElapsedMs} ms",
            ticket.Key,
            result.Count,
            (long)Stopwatch.GetElapsedTime(started).TotalMilliseconds);
        return result;
    }
}
