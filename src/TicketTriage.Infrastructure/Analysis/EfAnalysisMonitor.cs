using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using TicketTriage.Core.Abstractions;
using TicketTriage.Core.Domain;
using TicketTriage.Infrastructure.Persistence;

namespace TicketTriage.Infrastructure.Analysis;

/// <summary>Reads ticket status and stored suggestions; every call is one short read-only query set.</summary>
internal sealed class EfAnalysisMonitor(IDbContextFactory<TriageDbContext> dbFactory) : IAnalysisMonitor
{
    private static readonly JsonSerializerOptions PayloadOptions = new(JsonSerializerDefaults.Web);

    public async Task<IReadOnlyList<AnalysisState>> GetStatesAsync(
        IReadOnlyList<int> ticketIds,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(ticketIds);
        if (ticketIds.Count == 0)
        {
            return [];
        }

        var ids = ticketIds.Distinct().ToList();
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var tickets = await db.Tickets
            .AsNoTracking()
            .Where(t => ids.Contains(t.Id))
            .Select(t => new { t.Id, t.StatusId, t.SourcePayload, t.Summary, t.Description })
            .ToDictionaryAsync(t => t.Id, cancellationToken);
        var suggestions = (await db.Suggestions
                .AsNoTracking()
                .Where(s => ids.Contains(s.TicketId))
                .ToListAsync(cancellationToken))
            .ToDictionary(s => s.TicketId, SuggestionMapper.ToDomain);

        return [.. ticketIds.Select(id =>
        {
            if (!tickets.TryGetValue(id, out var row))
            {
                throw new InvalidOperationException($"Ticket {id} does not exist.");
            }

            var pending = row.StatusId == TicketStatusIds.New;
            return new AnalysisState(id, ToTicket(id, row.SourcePayload, row.Summary, row.Description), pending, pending ? null : suggestions.GetValueOrDefault(id));
        })];
    }

    public async Task<DateTime?> GetWorkerHeartbeatAsync(CancellationToken cancellationToken)
    {
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var marker = await db.SystemMarkers
            .AsNoTracking()
            .Where(m => m.Name == SystemMarkerNames.AnalysisWorkerHeartbeat)
            .Select(m => (DateTime?)m.SetAtUtc)
            .FirstOrDefaultAsync(cancellationToken);
        return marker is { } value ? DateTime.SpecifyKind(value, DateTimeKind.Utc) : null;
    }

    private static Ticket ToTicket(int id, string? payload, string summary, string? description)
    {
        Ticket? ticket = null;
        try
        {
            ticket = payload is null ? null : JsonSerializer.Deserialize<Ticket>(payload, PayloadOptions);
        }
        catch (JsonException)
        {
            // A corrupt payload must not break the batch export; the entity's own columns are the fallback input.
        }

        return (ticket ?? new Ticket { Summary = summary, Description = description }) with
        {
            Id = id,
            Key = SuggestionMapper.TicketKey(id),
        };
    }
}
