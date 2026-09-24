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

    public async Task<int> ImportAsync(CancellationToken cancellationToken)
    {
        if (await db.TrainingTickets.AnyAsync(cancellationToken))
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

        var importedAt = timeProvider.GetUtcNow();
        db.TrainingTickets.AddRange(tickets.Select(t => TrainingTicketEntity.FromTicket(t, importedAt)));
        await db.SaveChangesAsync(cancellationToken);

        logger.LogInformation("Imported {Count} training tickets from {Path}.", tickets.Count, path);
        return tickets.Count;
    }
}
