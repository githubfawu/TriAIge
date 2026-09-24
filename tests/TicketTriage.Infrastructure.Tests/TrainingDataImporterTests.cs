using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using TicketTriage.Infrastructure.Import;
using TicketTriage.Infrastructure.Persistence;

namespace TicketTriage.Infrastructure.Tests;

public class TrainingDataImporterTests
{
    [Fact]
    public async Task Import_SeededLookups_DoesNotThrow_AndSetsHumanApprovedForResolved_PerAC9()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var database = await SqliteTestDatabase.CreateAsync(ct);
        var path = Path.Combine(Path.GetTempPath(), $"training-{Guid.NewGuid():N}.json");
        try
        {
            await File.WriteAllTextAsync(path, """
                [
                  {"Issue key":"A-1","Summary":"s1","Description":"printer jam","Work type":"Incident","Resolution":"fixed"},
                  {"Issue key":"A-2","Summary":"s2","Description":"vpn down","Work type":"Incident"}
                ]
                """, ct);
            await using var db = await database.Factory.CreateDbContextAsync(ct);
            var importer = new TrainingDataImporter(
                db,
                Options.Create(new TrainingDataOptions { Path = path }),
                TimeProvider.System,
                NullLogger<TrainingDataImporter>.Instance);

            var count = await importer.ImportAsync(ct);

            count.Should().Be(2);
            var statuses = await db.Tickets.AsNoTracking().OrderBy(t => t.Summary).Select(t => t.StatusId).ToListAsync(ct);
            var approved = await db.Statuses.Where(s => s.Name == "HumanApproved").Select(s => s.Id).SingleAsync(ct);
            var newId = await db.Statuses.Where(s => s.Name == "New").Select(s => s.Id).SingleAsync(ct);
            statuses.Should().Equal(approved, newId);
        }
        finally
        {
            File.Delete(path);
        }
    }
}
