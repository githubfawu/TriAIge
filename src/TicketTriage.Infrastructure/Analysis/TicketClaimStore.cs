using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using TicketTriage.Core.Domain;
using TicketTriage.Infrastructure.Persistence;
using TicketTriage.Infrastructure.Pipeline;

namespace TicketTriage.Infrastructure.Analysis;

/// <summary>A ticket leased to this worker; <see cref="Claim"/> and <see cref="Version"/> identify the lease and payload when saving.</summary>
internal sealed record ClaimedTicket(Ticket Ticket, DateTime Claim, long Version);

/// <summary>Lease-based work queue over <c>Ticket</c> rows; every operation is one short statement or transaction, none spans an LLM call.</summary>
internal sealed class TicketClaimStore(
    IDbContextFactory<TriageDbContext> dbFactory,
    IOptions<AnalysisOptions> options,
    IOptions<TriageOptions> triageOptions,
    TimeProvider timeProvider)
{
    private static readonly JsonSerializerOptions PayloadOptions = new(JsonSerializerDefaults.Web);

    // Process-wide so scoped stores of the same process never hand out the same claim value.
    private static long _lastClaimTicks;

    public async Task<bool> IsDataReadyAsync(CancellationToken cancellationToken)
    {
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        return await db.SystemMarkers.AnyAsync(m => m.Name == SystemMarkerNames.TrainingDataReady, cancellationToken);
    }

    public async Task BeatAsync(CancellationToken cancellationToken)
    {
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var now = Now();
        var updated = await db.SystemMarkers
            .Where(m => m.Name == SystemMarkerNames.AnalysisWorkerHeartbeat)
            .ExecuteUpdateAsync(s => s.SetProperty(m => m.SetAtUtc, now), cancellationToken);
        if (updated > 0)
        {
            return;
        }

        db.SystemMarkers.Add(new SystemMarkerEntity { Name = SystemMarkerNames.AnalysisWorkerHeartbeat, SetAtUtc = now });
        try
        {
            await db.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException)
        {
            // A second process inserted the row first; its beat is as fresh as ours.
        }
    }

    public async Task<int> ReleaseStaleAsync(CancellationToken cancellationToken)
    {
        var threshold = Now().AddMinutes(-options.Value.LeaseMinutes);
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        return await db.Tickets
            .Where(t => t.StatusId == TicketStatusIds.New && t.ClaimedAt != null && t.ClaimedAt < threshold)
            .ExecuteUpdateAsync(s => s.SetProperty(t => t.ClaimedAt, (DateTime?)null), cancellationToken);
    }

    public Task<IReadOnlyList<ClaimedTicket>> ClaimAsync(int batchSize, CancellationToken cancellationToken) =>
        ClaimAsync(batchSize, [], cancellationToken);

    /// <param name="excludeIds">Tickets that must not be claimed again (already attempted in this cycle).</param>
    public async Task<IReadOnlyList<ClaimedTicket>> ClaimAsync(
        int batchSize,
        IReadOnlyCollection<int> excludeIds,
        CancellationToken cancellationToken)
    {
        var claim = NextClaim();
        // Tickets that exhausted their retries are failed and stay out of the queue until an analyst requeues them
        // (this is also where an unreadable payload ends up).
        var retryCount = triageOptions.Value.RetryCount;
        var excluded = excludeIds.ToList();

        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var candidateIds = db.Tickets
            .Where(t => t.StatusId == TicketStatusIds.New && t.Origin != TicketOrigin.Training && t.ClaimedAt == null
                && t.Retries < retryCount && !excluded.Contains(t.Id))
            .OrderBy(t => t.IngestedAt)
            .ThenBy(t => t.Id)
            .Take(batchSize)
            .Select(t => t.Id);

        var claimed = await db.Tickets
            .Where(t => candidateIds.Contains(t.Id)
                && t.StatusId == TicketStatusIds.New && t.Origin != TicketOrigin.Training && t.ClaimedAt == null)
            .ExecuteUpdateAsync(s => s.SetProperty(t => t.ClaimedAt, (DateTime?)claim), cancellationToken);
        if (claimed == 0)
        {
            return [];
        }

        var rows = await db.Tickets
            .AsNoTracking()
            .Where(t => t.ClaimedAt == claim)
            .OrderBy(t => t.IngestedAt)
            .ThenBy(t => t.Id)
            .Select(t => new { t.Id, t.SourcePayload, t.Version })
            .ToListAsync(cancellationToken);

        List<ClaimedTicket> result = [];
        List<int> poisoned = [];
        foreach (var row in rows)
        {
            var ticket = TryToTicket(row.Id, row.SourcePayload);
            if (ticket is null)
            {
                poisoned.Add(row.Id);
            }
            else
            {
                result.Add(new ClaimedTicket(ticket, claim, row.Version));
            }
        }

        if (poisoned.Count > 0)
        {
            await db.Tickets
                .Where(t => poisoned.Contains(t.Id) && t.ClaimedAt == claim)
                .ExecuteUpdateAsync(
                    s => s.SetProperty(t => t.ClaimedAt, (DateTime?)null).SetProperty(t => t.Retries, retryCount),
                    cancellationToken);
        }

        return result;
    }

    /// <returns>false when the lease was lost (swept, re-ingested, decided); nothing is stored then.</returns>
    public async Task<bool> SaveAsync(ClaimedTicket claimed, TriageSuggestion suggestion, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(claimed);
        ArgumentNullException.ThrowIfNull(suggestion);

        var ticketId = claimed.Ticket.Id!.Value;
        var claim = claimed.Claim;
        var version = claimed.Version;
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);

        // The version check rejects a suggestion computed from a payload that a re-ingest replaced meanwhile.
        var updated = await db.Tickets
            .Where(t => t.Id == ticketId && t.StatusId == TicketStatusIds.New && t.ClaimedAt == claim && t.Version == version)
            .ExecuteUpdateAsync(
                s => s.SetProperty(t => t.StatusId, TicketStatusIds.Reviewing)
                    .SetProperty(t => t.ClaimedAt, (DateTime?)null)
                    .SetProperty(t => t.Version, t => t.Version + 1),
                cancellationToken);
        if (updated == 0)
        {
            await transaction.RollbackAsync(cancellationToken);
            return false;
        }

        db.Suggestions.Add(SuggestionMapper.ToEntity(ticketId, suggestion, Now()));
        await db.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return true;
    }

    public async Task ReleaseAsync(DateTime claim, CancellationToken cancellationToken)
    {
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        await db.Tickets
            .Where(t => t.StatusId == TicketStatusIds.New && t.ClaimedAt == claim)
            .ExecuteUpdateAsync(s => s.SetProperty(t => t.ClaimedAt, (DateTime?)null), cancellationToken);
    }

    /// <summary>Releases one ticket of a batch; siblings sharing the claim value stay leased.</summary>
    public async Task ReleaseAsync(int ticketId, DateTime claim, CancellationToken cancellationToken)
    {
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        await db.Tickets
            .Where(t => t.Id == ticketId && t.StatusId == TicketStatusIds.New && t.ClaimedAt == claim)
            .ExecuteUpdateAsync(s => s.SetProperty(t => t.ClaimedAt, (DateTime?)null), cancellationToken);
    }

    private DateTime Now() => timeProvider.GetUtcNow().UtcDateTime;

    private DateTime NextClaim()
    {
        var now = Now().Ticks;
        while (true)
        {
            var last = Interlocked.Read(ref _lastClaimTicks);
            var next = Math.Max(now, last + 1);
            if (Interlocked.CompareExchange(ref _lastClaimTicks, next, last) == last)
            {
                return new DateTime(next, DateTimeKind.Utc);
            }
        }
    }

    /// <summary>Releases every lease on the queue; only valid at worker start, when this process is the single worker.</summary>
    public async Task<int> ReleaseAllAsync(CancellationToken cancellationToken)
    {
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        return await db.Tickets
            .Where(t => t.StatusId == TicketStatusIds.New && t.ClaimedAt != null)
            .ExecuteUpdateAsync(s => s.SetProperty(t => t.ClaimedAt, (DateTime?)null), cancellationToken);
    }

    private static Ticket? TryToTicket(int id, string? payload)
    {
        Ticket? ticket;
        try
        {
            ticket = payload is null ? null : JsonSerializer.Deserialize<Ticket>(payload, PayloadOptions);
        }
        catch (JsonException)
        {
            return null;
        }

        return ticket is null
            ? null
            : ticket with
            {
                Id = id,
                Key = SuggestionMapper.TicketKey(id),
            };
    }
}
