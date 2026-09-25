using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using TicketTriage.Core.Abstractions;
using TicketTriage.Core.Domain;
using TicketTriage.Infrastructure.Persistence;
using TicketTriage.Infrastructure.Sources;

namespace TicketTriage.Infrastructure.Ingestion;

/// <summary>Upserts received tickets by (origin, key); a changed payload resets analysis unless an analyst already decided.</summary>
public sealed class EfTicketIngestor(TriageDbContext db, TimeProvider timeProvider) : ITicketIngestor
{
    internal const int MaxTicketsPerCall = 10_000;
    internal const int MaxPayloadLength = 64 * 1024;

    private static readonly JsonSerializerOptions PayloadOptions = new(JsonSerializerDefaults.Web);

    public async Task<IReadOnlyList<IngestResult>> IngestAsync(
        IReadOnlyList<Ticket> tickets,
        TicketOrigin origin,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(tickets);
        if (origin == TicketOrigin.Training)
        {
            throw new ArgumentException("Training tickets are imported, not ingested.", nameof(origin));
        }

        if (tickets.Any(t => string.IsNullOrWhiteSpace(t.Key)))
        {
            throw new ArgumentException("Every ticket needs a key to be ingested.", nameof(tickets));
        }

        if (tickets.Count == 0)
        {
            return [];
        }

        if (tickets.Count > MaxTicketsPerCall)
        {
            throw new ArgumentException($"Too many tickets: {tickets.Count} (limit {MaxTicketsPerCall}).", nameof(tickets));
        }

        try
        {
            return await IngestCoreAsync(tickets, origin, cancellationToken);
        }
        catch (DbUpdateException)
        {
            // A concurrent ingest of the same key (version bump or unique index): reload and decide again once.
            db.ChangeTracker.Clear();
        }

        try
        {
            return await IngestCoreAsync(tickets, origin, cancellationToken);
        }
        catch (DbUpdateException ex)
        {
            db.ChangeTracker.Clear();
            throw new InvalidOperationException("Ticket ingest conflict; another writer changed the same tickets. Retry the ingest.", ex);
        }
    }

    private async Task<IReadOnlyList<IngestResult>> IngestCoreAsync(
        IReadOnlyList<Ticket> tickets,
        TicketOrigin origin,
        CancellationToken cancellationToken)
    {
        // Duplicate keys collapse onto one row; the last occurrence is the newest payload.
        var byKey = new Dictionary<string, Ticket>(StringComparer.Ordinal);
        foreach (var ticket in tickets)
        {
            byKey[ticket.Key] = ticket;
        }

        var keys = byKey.Keys.ToList();
        var existing = (await db.Tickets
                .Include(t => t.Comments)
                .Where(t => t.Origin == origin && t.SourceKey != null && keys.Contains(t.SourceKey))
                .ToListAsync(cancellationToken))
            .ToDictionary(t => t.SourceKey!, StringComparer.Ordinal);

        var factory = await TicketEntityFactory.LoadAsync(db, cancellationToken);
        var now = timeProvider.GetUtcNow().UtcDateTime;
        var outcomes = new Dictionary<string, (TicketEntity Entity, IngestOutcome Outcome)>(StringComparer.Ordinal);
        var resetIds = new List<int>();

        foreach (var (key, ticket) in byKey)
        {
            var payload = JsonSerializer.Serialize(ticket, PayloadOptions);
            if (payload.Length > MaxPayloadLength)
            {
                throw new ArgumentException($"A ticket payload is too large: {payload.Length} characters (limit {MaxPayloadLength}).", nameof(tickets));
            }

            var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(payload)));

            if (!existing.TryGetValue(key, out var entity))
            {
                entity = factory.Create(ticket, now);
                entity.Origin = origin;
                entity.SourceKey = key;
                entity.SourceHash = hash;
                entity.SourcePayload = payload;
                entity.StatusId = TicketStatusIds.New;
                entity.IngestedAt = now;
                entity.Comments = TicketEntityFactory.CreateComments(ticket);
                db.Tickets.Add(entity);
                outcomes[key] = (entity, IngestOutcome.Created);
            }
            else if (entity.SourceHash == hash)
            {
                outcomes[key] = (entity, IngestOutcome.Unchanged);
            }
            else if (entity.StatusId is TicketStatusIds.HumanApproved or TicketStatusIds.HumanRejected)
            {
                outcomes[key] = (entity, IngestOutcome.Locked);
            }
            else
            {
                factory.Apply(entity, ticket, now);
                db.Comments.RemoveRange(entity.Comments);
                entity.Comments = TicketEntityFactory.CreateComments(ticket);
                entity.SourceHash = hash;
                entity.SourcePayload = payload;
                entity.StatusId = TicketStatusIds.New;
                entity.ClaimedAt = null;
                entity.Retries = 0;
                entity.Version++;
                resetIds.Add(entity.Id);
                outcomes[key] = (entity, IngestOutcome.Updated);
            }
        }

        if (resetIds.Count > 0)
        {
            db.Suggestions.RemoveRange(await db.Suggestions.Where(s => resetIds.Contains(s.TicketId)).ToListAsync(cancellationToken));
            db.SuggestionEdits.RemoveRange(await db.SuggestionEdits.Where(e => resetIds.Contains(e.TicketId)).ToListAsync(cancellationToken));
        }

        await db.SaveChangesAsync(cancellationToken);

        return [.. tickets.Select(t =>
        {
            var (entity, outcome) = outcomes[t.Key];
            return new IngestResult(entity.Id, outcome);
        })];
    }
}
