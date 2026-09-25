using System.Globalization;
using Microsoft.EntityFrameworkCore;
using TicketTriage.Core.Domain;
using TicketTriage.Infrastructure.Persistence;

namespace TicketTriage.Infrastructure.Sources;

/// <summary>Id-to-name maps for the lookup columns; load once per call/stream, never per ticket.</summary>
internal sealed record LookupNames(
    IReadOnlyDictionary<int, string> WorkTypes,
    IReadOnlyDictionary<int, string> AffectedBusinessOrITServices,
    IReadOnlyDictionary<int, string> ServiceTeams)
{
    public static async Task<LookupNames> LoadAsync(TriageDbContext db, CancellationToken cancellationToken) => new(
        await db.WorkTypes.AsNoTracking().ToDictionaryAsync(e => e.Id, e => e.Name, cancellationToken),
        await db.AffectedBusinessOrITServices.AsNoTracking().ToDictionaryAsync(e => e.Id, e => e.Name, cancellationToken),
        await db.ServiceTeams.AsNoTracking().ToDictionaryAsync(e => e.Id, e => e.Name, cancellationToken));
}

internal static class TicketEntityMapper
{
    public static string KeyFor(int id) => string.Create(CultureInfo.InvariantCulture, $"DB-{id}");

    public static Ticket ToTicket(TicketEntity entity, LookupNames names) => new()
    {
        Id = entity.Id,
        Key = KeyFor(entity.Id),
        Summary = entity.Summary,
        Description = entity.Description,
        WorkType = names.WorkTypes.GetValueOrDefault(entity.WorkTypeId),
        AffectedServices = NameOrEmpty(names.AffectedBusinessOrITServices, entity.AffectedBusinessOrITServiceId),
        ServiceTeams = NameOrEmpty(names.ServiceTeams, entity.ServiceTeamId),
        Assignee = entity.Assignee,
        // Urgency/Impact/Priority are random in the training data (ADR-0001): never expose them as labels.
        Urgency = null,
        Impact = null,
        Priority = null,
        Resolution = entity.Resolution,
        Created = new DateTimeOffset(DateTime.SpecifyKind(entity.CreatedDate, DateTimeKind.Utc)),
        Comments = [.. entity.Comments.OrderBy(c => c.CommentId).Select(c => c.CommentText)],
    };

    private static IReadOnlyList<string> NameOrEmpty(IReadOnlyDictionary<int, string> lookup, int? id) =>
        id is { } key && lookup.TryGetValue(key, out var name) ? [name] : [];
}
