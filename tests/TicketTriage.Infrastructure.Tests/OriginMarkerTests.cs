using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using TicketTriage.Core.Domain;
using TicketTriage.Infrastructure.Import;
using TicketTriage.Infrastructure.Persistence;
using TicketTriage.Infrastructure.Retrieval;
using TicketTriage.Infrastructure.Routing;
using TicketTriage.Infrastructure.Sources;

namespace TicketTriage.Infrastructure.Tests;

public class OriginMarkerTests
{
    private const int FundPricingService = 4;
    private const int ClientServicesTeam = 7;

    [Fact]
    public async Task Schema_HasOriginLeaseAndMarkerColumns_PerAC3()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var database = await SqliteTestDatabase.CreateAsync(ct);
        await using var db = await database.Factory.CreateDbContextAsync(ct);

        var ticketColumns = await ColumnsAsync(db, "Ticket", ct);
        ticketColumns.Should().Contain(["Origin", "SourceKey", "SourceHash", "SourcePayload", "IngestedAt", "ClaimedAt", "Version"]);
        (await ColumnsAsync(db, "SystemMarker", ct)).Should().Contain(["Name", "SetAtUtc"]);
    }

    [Fact]
    public async Task UniqueOriginSourceKey_RejectsDuplicateButAllowsManyNullKeys_PerAC3()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var database = await SqliteTestDatabase.CreateAsync(ct);
        await database.AddTicketAsync(1, "a", origin: TicketOrigin.Challenge, sourceKey: "#1", cancellationToken: ct);
        await database.AddTicketAsync(2, "b", origin: TicketOrigin.Intake, sourceKey: "#1", cancellationToken: ct);
        await database.AddTicketAsync(3, "c", cancellationToken: ct);
        await database.AddTicketAsync(4, "d", cancellationToken: ct);

        var act = () => database.AddTicketAsync(5, "e", origin: TicketOrigin.Challenge, sourceKey: "#1", cancellationToken: ct);

        await act.Should().ThrowAsync<DbUpdateException>();
    }

    [Fact]
    public async Task StatusIdConstants_EqualSeededStatusRows_PerAC3()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var database = await SqliteTestDatabase.CreateAsync(ct);
        await using var db = await database.Factory.CreateDbContextAsync(ct);

        var seed = await db.Statuses.AsNoTracking().ToDictionaryAsync(s => s.Name, s => s.Id, ct);

        seed["New"].Should().Be(TicketStatusIds.New);
        seed["Reviewing"].Should().Be(TicketStatusIds.Reviewing);
        seed["Reviewed"].Should().Be(TicketStatusIds.Reviewed);
        seed["HumanRejected"].Should().Be(TicketStatusIds.HumanRejected);
        seed["HumanApproved"].Should().Be(TicketStatusIds.HumanApproved);
        seed.Should().HaveCount(5);
    }

    [Fact]
    public async Task Import_SetsOriginAndMarker_AndSkipsEvenWhenNonTrainingTicketsExist_PerAC8()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var database = await SqliteTestDatabase.CreateAsync(ct);
        var path = Path.Combine(Path.GetTempPath(), $"training-{Guid.NewGuid():N}.json");
        try
        {
            await File.WriteAllTextAsync(path, """[{"Summary":"s1","Description":"printer jam","Work type":"Incident"}]""", ct);
            await database.AddTicketAsync(100, "challenge", origin: TicketOrigin.Challenge, sourceKey: "#1", statusId: TicketStatusIds.New, cancellationToken: ct);
            await using var db = await database.Factory.CreateDbContextAsync(ct);
            var importer = new TrainingDataImporter(
                db, Options.Create(new TrainingDataOptions { Path = path }), TimeProvider.System, NullLogger<TrainingDataImporter>.Instance);

            var first = await importer.ImportAsync(ct);
            var second = await importer.ImportAsync(ct);

            first.Should().Be(1);
            second.Should().Be(0);
            (await db.Tickets.Where(t => t.Origin == TicketOrigin.Training).CountAsync(ct)).Should().Be(1);
            (await db.SystemMarkers.SingleAsync(ct)).Name.Should().Be(SystemMarkerNames.TrainingDataReady);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public async Task Import_MissingFile_WritesNoMarker_PerAC8()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var database = await SqliteTestDatabase.CreateAsync(ct);
        await using var db = await database.Factory.CreateDbContextAsync(ct);
        var importer = new TrainingDataImporter(
            db,
            Options.Create(new TrainingDataOptions { Path = Path.Combine(Path.GetTempPath(), $"missing-{Guid.NewGuid():N}.json") }),
            TimeProvider.System,
            NullLogger<TrainingDataImporter>.Instance);

        (await importer.ImportAsync(ct)).Should().Be(0);

        (await db.SystemMarkers.AnyAsync(ct)).Should().BeFalse();
    }

    [Fact]
    public async Task Corpus_IgnoresChallengeRows_PerAC8()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var database = await SqliteTestDatabase.CreateAsync(ct);
        await database.AddTicketAsync(1, "printer jam", cancellationToken: ct);
        await database.AddTicketAsync(2, "vpn down", origin: TicketOrigin.Challenge, sourceKey: "#1", cancellationToken: ct);
        using var provider = new SimilarTicketIndexProvider(database.Factory, NullLogger<SimilarTicketIndexProvider>.Instance);

        var index = await provider.GetAsync(ct);

        index.DocumentCount.Should().Be(1);
    }

    [Fact]
    public async Task RoutingStatistics_IgnoreChallengeRows_PerAC8()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var database = await SqliteTestDatabase.CreateAsync(ct);
        await database.AddTicketAsync(1, "a", serviceId: FundPricingService, teamId: ClientServicesTeam, assignee: "alice", cancellationToken: ct);
        for (var i = 0; i < 3; i++)
        {
            await database.AddTicketAsync(10 + i, "x", serviceId: FundPricingService, teamId: 0, assignee: "bob",
                origin: TicketOrigin.Challenge, sourceKey: $"#{i}", cancellationToken: ct);
        }

        using var provider = new RoutingStatisticsProvider(database.Factory, new LookupNamesProvider(database.Factory), NullLogger<RoutingStatisticsProvider>.Instance);
        var statistics = await provider.GetAsync(ct);

        statistics.ResolveTeams(["Fund Pricing"]).Should().Equal("Client Services");
    }

    [Fact]
    public async Task DbTicketSource_SkipsUnresolvedTrainingTickets_PerAC8()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var database = await SqliteTestDatabase.CreateAsync(ct);
        await database.AddTicketAsync(1, "training without resolution", statusId: TicketStatusIds.New, cancellationToken: ct);
        await database.AddTicketAsync(2, "challenge", statusId: TicketStatusIds.New, origin: TicketOrigin.Challenge, sourceKey: "#1", cancellationToken: ct);
        var source = new DbTicketSource(database.Factory, new LookupNamesProvider(database.Factory), NullLogger<DbTicketSource>.Instance);

        List<Ticket> tickets = [];
        await foreach (var ticket in source.GetTicketsAsync(ct))
        {
            tickets.Add(ticket);
        }

        tickets.Select(t => t.Id).Should().Equal(2);
    }

    [Fact]
    public async Task ClaimedAt_RoundTripsExactlyAsUtc_PerAC3()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var database = await SqliteTestDatabase.CreateAsync(ct);
        await database.AddTicketAsync(1, "a", cancellationToken: ct);
        var claim = new DateTime(2026, 9, 25, 10, 11, 12, DateTimeKind.Utc).AddTicks(1234567);

        await using (var db = await database.Factory.CreateDbContextAsync(ct))
        {
            var ticket = await db.Tickets.SingleAsync(ct);
            ticket.ClaimedAt = claim;
            await db.SaveChangesAsync(ct);
        }

        await using var read = await database.Factory.CreateDbContextAsync(ct);
        var stored = (await read.Tickets.AsNoTracking().SingleAsync(ct)).ClaimedAt;
        stored.Should().Be(claim);
        stored!.Value.Kind.Should().Be(DateTimeKind.Utc);
        (await read.Tickets.CountAsync(t => t.ClaimedAt == claim, ct)).Should().Be(1);
    }

    private static async Task<List<string>> ColumnsAsync(TriageDbContext db, string table, CancellationToken ct)
    {
        var connection = db.Database.GetDbConnection();
        await db.Database.OpenConnectionAsync(ct);
        await using var command = connection.CreateCommand();
        command.CommandText = $"SELECT name FROM pragma_table_info('{table}')";
        List<string> names = [];
        await using var reader = await command.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            names.Add(reader.GetString(0));
        }

        return names;
    }
}
