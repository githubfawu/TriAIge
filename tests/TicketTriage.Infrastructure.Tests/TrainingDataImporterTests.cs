using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using TicketTriage.Infrastructure.Import;
using TicketTriage.Infrastructure.Persistence;

namespace TicketTriage.Infrastructure.Tests;

public class TrainingDataImporterTests
{
    // Shape of the real export: no "Issue key", all 16 fields, lowercase vocabulary, null Resolution, non-ISO date.
    private const string RealShapeArray = """
        [
          {"Work type":"Incident","Summary":"s1","Description":"printer jam",
           "Affected Business or IT Services":["Outlook & Email"],"Business Entity":["Swiss Life AG"],
           "Service Team(s)":["Enterprise Applications"],"Reporter":"a@x.ch","Assignee":"Jane Doe",
           "Priority":"high","Urgency":"highest","Impact":"medium","Created date":"2026-03-03 19:36",
           "Status":"Done","Resolution":"done","Resolution date":"2026-03-04 10:00","All Comments":["a@x.ch: Resolution: restart"]},
          {"Work type":"Service Request","Summary":"s2","Description":"vpn down",
           "Affected Business or IT Services":[],"Business Entity":[],"Service Team(s)":[],"Reporter":"b@x.ch","Assignee":null,
           "Priority":"low","Urgency":"low","Impact":"low","Created date":"2026-03-05 08:00",
           "Status":"Open","Resolution":null,"Resolution date":null,"All Comments":[]}
        ]
        """;

    [Fact]
    public async Task Import_RealShapeWithoutIssueKey_ImportsAllAndIsIdempotent_PerAC6()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var database = await SqliteTestDatabase.CreateAsync(ct);
        var path = Path.Combine(Path.GetTempPath(), $"training-{Guid.NewGuid():N}.json");
        try
        {
            await File.WriteAllTextAsync(path, RealShapeArray, ct);
            await using var db = await database.Factory.CreateDbContextAsync(ct);
            var importer = new TrainingDataImporter(
                db,
                Options.Create(new TrainingDataOptions { Path = path }),
                TimeProvider.System,
                NullLogger<TrainingDataImporter>.Instance);

            var first = await importer.ImportAsync(ct);
            var second = await importer.ImportAsync(ct);

            first.Should().Be(2);
            second.Should().Be(0);
            (await db.Tickets.CountAsync(ct)).Should().Be(2);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    [Trait("Category", "Integration")]
    public async Task Import_RealTrainingFile_Imports20000AndIsIdempotent_PerAC6()
    {
        var real = FindRealTrainingFile();
        Assert.SkipUnless(real is not null, "Real training file not present in data/.");
        var ct = TestContext.Current.CancellationToken;
        await using var database = await SqliteTestDatabase.CreateAsync(ct);
        await using var db = await database.Factory.CreateDbContextAsync(ct);
        var importer = new TrainingDataImporter(
            db,
            Options.Create(new TrainingDataOptions { Path = real! }),
            TimeProvider.System,
            NullLogger<TrainingDataImporter>.Instance);

        var first = await importer.ImportAsync(ct);
        var second = await importer.ImportAsync(ct);

        first.Should().Be(20000);
        second.Should().Be(0);
        (await db.Tickets.CountAsync(ct)).Should().Be(20000);
    }

    private static string? FindRealTrainingFile()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            if (File.Exists(Path.Combine(dir.FullName, "TicketTriage.slnx")))
            {
                var file = Path.Combine(dir.FullName, "data", "jira_first_20000_requested_fields_synthetic.json");
                return File.Exists(file) ? file : null;
            }
        }

        return null;
    }

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
