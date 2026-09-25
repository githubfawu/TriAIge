using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using TicketTriage.Core.Domain;
using TicketTriage.Infrastructure.Persistence;
using TicketTriage.Infrastructure.Sources;

namespace TicketTriage.Infrastructure.Import;

public sealed class TrainingDataOptions
{
    public const string SectionName = "TrainingData";

    /// <summary>Path to the training JSON file; relative paths are resolved against the current directory.</summary>
    public string Path { get; set; } = "../../data/jira_first_20000_requested_fields_synthetic.json";
}

/// <summary>Imports the training tickets from <c>data/</c> into SQLite. Idempotent: skips once the TrainingDataReady marker exists.</summary>
public sealed class TrainingDataImporter(
    TriageDbContext db,
    IOptions<TrainingDataOptions> options,
    TimeProvider timeProvider,
    ILogger<TrainingDataImporter> logger)
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public async Task<int> ImportAsync(CancellationToken cancellationToken)
    {
        if (await db.SystemMarkers.AnyAsync(m => m.Name == SystemMarkerNames.TrainingDataReady, cancellationToken))
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

        // The real export is a plain JSON array of records without "Issue key"; DB tickets are keyed DB-{Id}.
        await using var stream = File.OpenRead(path);
        var tickets = await JsonSerializer.DeserializeAsync<List<Ticket>>(stream, JsonOptions, cancellationToken) ?? [];

        var factory = await TicketEntityFactory.LoadAsync(db, cancellationToken);
        var now = timeProvider.GetUtcNow().UtcDateTime;

        var entities = tickets.Select(ticket =>
        {
            var entity = factory.Create(ticket, now);
            entity.Origin = TicketOrigin.Training;
            entity.StatusId = ticket.Resolution is null ? TicketStatusIds.New : TicketStatusIds.HumanApproved;
            entity.Comments = TicketEntityFactory.CreateComments(ticket);
            return entity;
        });

        // 20k tickets plus comments: skip per-entity change detection during the bulk insert.
        db.ChangeTracker.AutoDetectChangesEnabled = false;
        db.Tickets.AddRange(entities);
        // Same SaveChanges as the tickets: the marker is the worker's data-ready gate, so it must never exist without them.
        db.SystemMarkers.Add(new SystemMarkerEntity
        {
            Name = SystemMarkerNames.TrainingDataReady,
            SetAtUtc = timeProvider.GetUtcNow().UtcDateTime,
        });
        await db.SaveChangesAsync(cancellationToken);

        logger.LogInformation("Imported {Count} training tickets from {Path}.", tickets.Count, path);
        return tickets.Count;
    }
}
