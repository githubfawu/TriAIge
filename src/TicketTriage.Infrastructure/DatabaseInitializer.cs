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
        await using var scope = services.CreateAsyncScope();

        var db = scope.ServiceProvider.GetRequiredService<TriageDbContext>();
        await db.Database.EnsureCreatedAsync(cancellationToken);

        var importer = scope.ServiceProvider.GetRequiredService<TrainingDataImporter>();
        await importer.ImportAsync(cancellationToken);
    }
}
