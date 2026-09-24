using System.Diagnostics.CodeAnalysis;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using TicketTriage.Core.Domain;
using TicketTriage.Infrastructure.Persistence;

namespace TicketTriage.Infrastructure.Import;

public sealed class TrainingDataOptions
{
    public const string SectionName = "TrainingData";

    /// <summary>Path to the training JSON file; relative paths are resolved against the current directory.</summary>
    public string Path { get; set; } = "../../data/training.json";
}

/// <summary>Imports the training tickets from <c>data/</c> into SQLite. Idempotent: skips if already imported.</summary>
public sealed class TrainingDataImporter(
    TriageDbContext db,
    IOptions<TrainingDataOptions> options,
    TimeProvider timeProvider,
    ILogger<TrainingDataImporter> logger)
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    // Raw Jira "Impact" severity names don't match the new Impact lookup table; translate by rank.
    private static readonly Dictionary<string, string> ImpactNameTranslation = new(StringComparer.OrdinalIgnoreCase)
    {
        ["Major"] = "Highest",
        ["Significant"] = "High",
        ["Moderate"] = "Medium",
        ["Minor"] = "Low",
        ["No Impact"] = "Lowest",
    };

    public async Task<int> ImportAsync(CancellationToken cancellationToken)
    {
        if (await db.Tickets.AnyAsync(cancellationToken))
        {
            logger.LogInformation("Training data already imported, skipping.");
            return 0;
        }

        var path = Path.GetFullPath(options.Value.Path);
        if (!File.Exists(path))
        {
            logger.LogWarning("Training data file {Path} not found, skipping import.", path);
            return 0;
        }

        // TODO: implement - verify the file shape (array vs. wrapper object), stream large files,
        // normalize list fields and insert in batches.
        await using var stream = File.OpenRead(path);
        var tickets = await JsonSerializer.DeserializeAsync<List<Ticket>>(stream, JsonOptions, cancellationToken) ?? [];

        var workTypeIds = await LoadLookupAsync(db.WorkTypes, cancellationToken);
        var urgencyIds = await LoadLookupAsync(db.Urgencies, cancellationToken);
        var impactIds = await LoadLookupAsync(db.Impacts, cancellationToken);
        var priorityIds = await LoadLookupAsync(db.Priorities, cancellationToken);
        var serviceTeamIds = await LoadLookupAsync(db.ServiceTeams, cancellationToken);
        var affectedServiceIds = await LoadLookupAsync(db.AffectedBusinessOrITServices, cancellationToken);
        var newStatusId = await db.Statuses.Where(s => s.Name == "New").Select(s => s.Id).SingleAsync(cancellationToken);
        var finishedStatusId = await db.Statuses.Where(s => s.Name == "Finished").Select(s => s.Id).SingleAsync(cancellationToken);

        var entities = tickets.Select(ticket => new TicketEntity
        {
            WorkTypeId = ResolveOrDefault(workTypeIds, ticket.WorkType, workTypeIds["Incident"]),
            Summary = Truncate(ticket.Summary, 250),
            Description = Truncate(ticket.Description, 1000),
            AffectedBusinessOrITServiceId = Resolve(affectedServiceIds, ticket.AffectedServices.FirstOrDefault()),
            ServiceTeamId = Resolve(serviceTeamIds, ticket.ServiceTeams.FirstOrDefault()),
            Assignee = Truncate(ticket.Assignee, 50),
            UrgencyId = Resolve(urgencyIds, ticket.Urgency),
            ImpactId = Resolve(impactIds, TranslateImpactName(ticket.Impact)),
            PriorityId = Resolve(priorityIds, ticket.Priority),
            CreatedDate = ticket.Created?.UtcDateTime ?? timeProvider.GetUtcNow().UtcDateTime,
            StatusId = ticket.Resolution is null ? newStatusId : finishedStatusId,
            Resolution = Truncate(ticket.Resolution, 500),
            Comments = [.. ticket.Comments.Select(text => new CommentEntity { CommentText = Truncate(text, 500)! })],
        });

        db.Tickets.AddRange(entities);
        await db.SaveChangesAsync(cancellationToken);

        logger.LogInformation("Imported {Count} training tickets from {Path}.", tickets.Count, path);
        return tickets.Count;
    }

    private static async Task<Dictionary<string, int>> LoadLookupAsync<TEntity>(IQueryable<TEntity> lookup, CancellationToken cancellationToken)
        where TEntity : class, ILookupEntity =>
        (await lookup.AsNoTracking().ToListAsync(cancellationToken))
            .ToDictionary(e => e.Name, e => e.Id, StringComparer.OrdinalIgnoreCase);

    private static int? Resolve(Dictionary<string, int> lookup, string? name) =>
        name is not null && lookup.TryGetValue(name, out var id) ? id : null;

    private static int ResolveOrDefault(Dictionary<string, int> lookup, string? name, int fallback) =>
        Resolve(lookup, name) ?? fallback;

    private static string? TranslateImpactName(string? rawImpact) =>
        rawImpact is not null && ImpactNameTranslation.TryGetValue(rawImpact, out var translated) ? translated : rawImpact;

    [return: NotNullIfNotNull(nameof(value))]
    private static string? Truncate(string? value, int maxLength) =>
        value is null ? null : value.Length <= maxLength ? value : value[..maxLength];
}

