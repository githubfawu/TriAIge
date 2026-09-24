using System.Runtime.CompilerServices;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using TicketTriage.Core.Abstractions;
using TicketTriage.Core.Domain;
using TicketTriage.Infrastructure.Persistence;

namespace TicketTriage.Infrastructure.Sources;

/// <summary>
/// Streams tickets with status "New" in (CreatedDate, Id) order. Keyset paging with a fresh context per batch, so no
/// context or reader stays open across a <c>yield</c> while the consumer (the pipeline) works for seconds per ticket.
/// </summary>
internal sealed class DbTicketSource : ITicketSource
{
    private const string NewStatusName = "New";
    private const int DefaultBatchSize = 100;

    private readonly IDbContextFactory<TriageDbContext> _dbFactory;
    private readonly LookupNamesProvider _lookupNames;
    private readonly ILogger<DbTicketSource> _logger;
    private readonly int _batchSize;

    public DbTicketSource(IDbContextFactory<TriageDbContext> dbFactory, LookupNamesProvider lookupNames, ILogger<DbTicketSource> logger)
        : this(dbFactory, lookupNames, logger, DefaultBatchSize)
    {
    }

    internal DbTicketSource(IDbContextFactory<TriageDbContext> dbFactory, LookupNamesProvider lookupNames, ILogger<DbTicketSource> logger, int batchSize)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(batchSize, 1);
        _dbFactory = dbFactory;
        _lookupNames = lookupNames;
        _logger = logger;
        _batchSize = batchSize;
    }

    public async IAsyncEnumerable<Ticket> GetTicketsAsync([EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var names = await _lookupNames.GetAsync(cancellationToken);
        int newStatusId;
        await using (var db = await _dbFactory.CreateDbContextAsync(cancellationToken))
        {
            var statusId = await db.Statuses.AsNoTracking()
                .Where(s => s.Name == NewStatusName)
                .Select(s => (int?)s.Id)
                .FirstOrDefaultAsync(cancellationToken);
            if (statusId is null)
            {
                yield break;
            }

            newStatusId = statusId.Value;
        }

        DateTime? lastCreated = null;
        var lastId = 0;
        var total = 0;

        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();

            List<Ticket> batch;
            await using (var db = await _dbFactory.CreateDbContextAsync(cancellationToken))
            {
                var query = db.Tickets.AsNoTracking().Include(t => t.Comments).AsSplitQuery().Where(t => t.StatusId == newStatusId);
                if (lastCreated is { } d)
                {
                    var id = lastId;
                    query = query.Where(t => t.CreatedDate > d || (t.CreatedDate == d && t.Id > id));
                }

                var entities = await query
                    .OrderBy(t => t.CreatedDate).ThenBy(t => t.Id)
                    .Take(_batchSize)
                    .ToListAsync(cancellationToken);

                batch = [.. entities.Select(e => TicketEntityMapper.ToTicket(e, names))];
                if (entities.Count > 0)
                {
                    lastCreated = entities[^1].CreatedDate;
                    lastId = entities[^1].Id;
                }
            }

            if (batch.Count == 0)
            {
                break;
            }

            total += batch.Count;
            _logger.LogDebug("Loaded batch of {BatchCount} new tickets ({Total} so far)", batch.Count, total);

            foreach (var ticket in batch)
            {
                cancellationToken.ThrowIfCancellationRequested();
                yield return ticket;
            }

            if (batch.Count < _batchSize)
            {
                break;
            }
        }
    }
}
