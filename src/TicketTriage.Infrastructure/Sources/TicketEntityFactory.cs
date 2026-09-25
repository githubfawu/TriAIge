using System.Diagnostics.CodeAnalysis;
using Microsoft.EntityFrameworkCore;
using TicketTriage.Core.Domain;
using TicketTriage.Infrastructure.Persistence;

namespace TicketTriage.Infrastructure.Sources;

/// <summary>Maps a Core <see cref="Ticket"/> (raw strings) onto <see cref="TicketEntity"/> lookup ids, truncating to column sizes.</summary>
internal sealed class TicketEntityFactory
{
    // Raw Jira "Impact" severity names don't match the Impact lookup table; translate by rank.
    private static readonly Dictionary<string, string> ImpactNameTranslation = new(StringComparer.OrdinalIgnoreCase)
    {
        ["Major"] = "Highest",
        ["Significant"] = "High",
        ["Moderate"] = "Medium",
        ["Minor"] = "Low",
        ["No Impact"] = "Lowest",
    };

    private readonly Dictionary<string, int> _workTypeIds;
    private readonly Dictionary<string, int> _urgencyIds;
    private readonly Dictionary<string, int> _impactIds;
    private readonly Dictionary<string, int> _priorityIds;
    private readonly Dictionary<string, int> _serviceTeamIds;
    private readonly Dictionary<string, int> _affectedServiceIds;

    private TicketEntityFactory(
        Dictionary<string, int> workTypeIds,
        Dictionary<string, int> urgencyIds,
        Dictionary<string, int> impactIds,
        Dictionary<string, int> priorityIds,
        Dictionary<string, int> serviceTeamIds,
        Dictionary<string, int> affectedServiceIds)
    {
        _workTypeIds = workTypeIds;
        _urgencyIds = urgencyIds;
        _impactIds = impactIds;
        _priorityIds = priorityIds;
        _serviceTeamIds = serviceTeamIds;
        _affectedServiceIds = affectedServiceIds;
    }

    public static async Task<TicketEntityFactory> LoadAsync(TriageDbContext db, CancellationToken cancellationToken) =>
        new(
            await LoadLookupAsync(db.WorkTypes, cancellationToken),
            await LoadLookupAsync(db.Urgencies, cancellationToken),
            await LoadLookupAsync(db.Impacts, cancellationToken),
            await LoadLookupAsync(db.Priorities, cancellationToken),
            await LoadLookupAsync(db.ServiceTeams, cancellationToken),
            await LoadLookupAsync(db.AffectedBusinessOrITServices, cancellationToken));

    /// <summary>Creates a row with the mapped ticket columns; origin, status and ingest columns are the caller's.</summary>
    public TicketEntity Create(Ticket ticket, DateTime createdFallbackUtc)
    {
        var entity = new TicketEntity { Summary = Truncate(ticket.Summary, 250) };
        Apply(entity, ticket, createdFallbackUtc);
        return entity;
    }

    /// <summary>Overwrites the mapped ticket columns (not comments) on an existing row.</summary>
    public void Apply(TicketEntity entity, Ticket ticket, DateTime createdFallbackUtc)
    {
        entity.WorkTypeId = Resolve(_workTypeIds, ticket.WorkType) ?? _workTypeIds["Incident"];
        entity.Summary = Truncate(ticket.Summary, 250);
        entity.Description = Truncate(ticket.Description, 1000);
        entity.AffectedBusinessOrITServiceId = Resolve(_affectedServiceIds, ticket.AffectedServices.FirstOrDefault());
        entity.ServiceTeamId = Resolve(_serviceTeamIds, ticket.ServiceTeams.FirstOrDefault());
        entity.Assignee = Truncate(ticket.Assignee, 50);
        entity.UrgencyId = Resolve(_urgencyIds, ticket.Urgency);
        entity.ImpactId = Resolve(_impactIds, TranslateImpactName(ticket.Impact));
        entity.PriorityId = Resolve(_priorityIds, ticket.Priority);
        entity.CreatedDate = ticket.Created?.UtcDateTime ?? createdFallbackUtc;
        entity.Resolution = Truncate(ticket.Resolution, 500);
    }

    public static List<CommentEntity> CreateComments(Ticket ticket) =>
        [.. ticket.Comments.Select(text => new CommentEntity { CommentText = Truncate(text, 500) })];

    private static async Task<Dictionary<string, int>> LoadLookupAsync<TEntity>(IQueryable<TEntity> lookup, CancellationToken cancellationToken)
        where TEntity : class, ILookupEntity =>
        (await lookup.AsNoTracking().ToListAsync(cancellationToken))
            .ToDictionary(e => e.Name, e => e.Id, StringComparer.OrdinalIgnoreCase);

    private static int? Resolve(Dictionary<string, int> lookup, string? name) =>
        name is not null && lookup.TryGetValue(name, out var id) ? id : null;

    private static string? TranslateImpactName(string? rawImpact) =>
        rawImpact is not null && ImpactNameTranslation.TryGetValue(rawImpact, out var translated) ? translated : rawImpact;

    [return: NotNullIfNotNull(nameof(value))]
    private static string? Truncate(string? value, int maxLength) =>
        value is null ? null : value.Length <= maxLength ? value : value[..maxLength];
}
