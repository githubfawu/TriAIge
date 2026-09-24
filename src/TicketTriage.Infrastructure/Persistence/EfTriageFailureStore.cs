using Microsoft.EntityFrameworkCore;
using TicketTriage.Core.Abstractions;
using TicketTriage.Core.Domain;

namespace TicketTriage.Infrastructure.Persistence;

internal sealed class EfTriageFailureStore(IDbContextFactory<TriageDbContext> dbFactory, TimeProvider timeProvider)
    : ITriageFailureStore
{
    private const int MaxStackTraceLength = 4000;

    public async Task<int?> RecordFailureAsync(TriageFailure failure, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(failure);

        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);

        var ticketId = failure.TicketId;
        var updated = 0;
        if (ticketId is { } id)
        {
            updated = await db.Tickets
                .Where(t => t.Id == id)
                .ExecuteUpdateAsync(s => s.SetProperty(t => t.Retries, t => t.Retries + 1), cancellationToken);
        }

        db.TriageFailures.Add(new TriageFailureEntity
        {
            TicketId = ticketId,
            TicketKey = failure.TicketKey,
            Attempt = failure.Attempt,
            Reason = failure.Reason,
            ExceptionType = failure.ExceptionType,
            StackTrace = failure.StackTrace is { Length: > MaxStackTraceLength } trace ? trace[..MaxStackTraceLength] : failure.StackTrace,
            OccurredAtUtc = timeProvider.GetUtcNow().UtcDateTime,
        });
        await db.SaveChangesAsync(cancellationToken);

        int? retries = null;
        if (updated > 0 && ticketId is { } readId)
        {
            retries = await db.Tickets
                .AsNoTracking()
                .Where(t => t.Id == readId)
                .Select(t => (int?)t.Retries)
                .SingleOrDefaultAsync(cancellationToken);
        }

        await transaction.CommitAsync(cancellationToken);
        return retries;
    }

    public async Task ResetRetriesAsync(int ticketId, CancellationToken cancellationToken)
    {
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        await db.Tickets
            .Where(t => t.Id == ticketId && t.Retries != 0)
            .ExecuteUpdateAsync(s => s.SetProperty(t => t.Retries, 0), cancellationToken);
    }
}
