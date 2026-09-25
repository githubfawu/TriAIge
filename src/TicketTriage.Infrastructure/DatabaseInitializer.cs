using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using TicketTriage.Infrastructure.Import;
using TicketTriage.Infrastructure.Persistence;

namespace TicketTriage.Infrastructure;

public static class DatabaseInitializer
{
    /// <summary>Creates the database from the current model (if missing) and imports training data (idempotent). Intended for Development.</summary>
    public static async Task InitializeTriageDatabaseAsync(this IServiceProvider services, CancellationToken cancellationToken)
    {
        await services.EnsureTriageDatabaseCreatedAsync(cancellationToken);

        await using var scope = services.CreateAsyncScope();
        var importer = scope.ServiceProvider.GetRequiredService<TrainingDataImporter>();
        await importer.ImportAsync(cancellationToken);
    }

    /// <summary>Creates the schema if missing (WAL mode included) without importing; for hosts that only ingest and read, e.g. Batch.</summary>
    public static async Task EnsureTriageDatabaseCreatedAsync(this IServiceProvider services, CancellationToken cancellationToken)
    {
        // Web and Batch may start at the same moment: the loser of EnsureCreated sees "database is locked" or "already exists".
        await RetryOnContentionAsync(
            async ct =>
            {
                await using var scope = services.CreateAsyncScope();

                var db = scope.ServiceProvider.GetRequiredService<TriageDbContext>();
                await db.Database.EnsureCreatedAsync(ct);

                // Web worker and Batch write concurrently; WAL lets readers proceed during a write. Not applicable to :memory: databases.
                if (db.Database.GetConnectionString() is { } connectionString
                    && !connectionString.Contains(":memory:", StringComparison.OrdinalIgnoreCase))
                {
                    await db.Database.ExecuteSqlRawAsync("PRAGMA journal_mode=WAL;", ct);
                }
            },
            attempts: 5,
            delay: TimeSpan.FromMilliseconds(200),
            cancellationToken);
    }

    internal static async Task RetryOnContentionAsync(
        Func<CancellationToken, Task> action,
        int attempts,
        TimeSpan delay,
        CancellationToken cancellationToken)
    {
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                await action(cancellationToken);
                return;
            }
            catch (Exception ex) when (attempt < attempts && IsContention(ex))
            {
                await Task.Delay(delay, cancellationToken);
            }
        }
    }

    private static bool IsContention(Exception exception)
    {
        for (var current = exception; current is not null; current = current.InnerException)
        {
            if (current is SqliteException { SqliteErrorCode: 5 or 6 }
                || (current is SqliteException && current.Message.Contains("already exists", StringComparison.OrdinalIgnoreCase)))
            {
                return true;
            }
        }

        return false;
    }
}
