using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using TicketTriage.Core.Domain;
using TicketTriage.Infrastructure.Ingestion;
using TicketTriage.Infrastructure.Persistence;

namespace TicketTriage.Infrastructure.Tests;

public sealed class EfTicketIngestorTests
{
    private static Ticket Make(string key, string description = "printer jam") => new()
    {
        Key = key,
        Summary = $"Summary {key}",
        Description = description,
        WorkType = "Service Request",
        AffectedServices = ["Printing"],
        Comments = ["first", "second"],
    };

    private static async Task SetStatusAsync(
        SqliteTestDatabase database, int id, int statusId, CancellationToken ct)
    {
        await using var db = await database.Factory.CreateDbContextAsync(ct);
        var row = await db.Tickets.SingleAsync(t => t.Id == id, ct);
        row.StatusId = statusId;
        db.Suggestions.Add(new TriageSuggestionEntity { TicketId = id, CreatedAtUtc = DateTime.UtcNow });
        db.SuggestionEdits.Add(new SuggestionEditEntity { TicketId = id, Field = "Assignee", EditedAtUtc = DateTime.UtcNow });
        await db.SaveChangesAsync(ct);
    }

    [Fact]
    public async Task Ingest_Twice_ReturnsSameIdsWithoutDuplicates()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var database = await SqliteTestDatabase.CreateAsync(ct);
        Ticket[] input = [Make("#1"), Make("#2")];

        await using var db1 = await database.Factory.CreateDbContextAsync(ct);
        var first = await new EfTicketIngestor(db1, TimeProvider.System).IngestAsync(input, TicketOrigin.Challenge, ct);
        await using var db2 = await database.Factory.CreateDbContextAsync(ct);
        var second = await new EfTicketIngestor(db2, TimeProvider.System).IngestAsync(input, TicketOrigin.Challenge, ct);

        first.Select(r => r.Outcome).Should().OnlyContain(o => o == IngestOutcome.Created);
        second.Select(r => r.Outcome).Should().OnlyContain(o => o == IngestOutcome.Unchanged);
        second.Select(r => r.TicketId).Should().Equal(first.Select(r => r.TicketId));

        await using var check = await database.Factory.CreateDbContextAsync(ct);
        var rows = await check.Tickets.Include(t => t.Comments).ToListAsync(ct);
        rows.Should().HaveCount(2);
        rows.Should().OnlyContain(t => t.Origin == TicketOrigin.Challenge && t.StatusId == TicketStatusIds.New
            && t.IngestedAt != null && t.Version == 0 && t.Retries == 0 && t.Comments.Count == 2);
        rows.Select(t => t.SourceKey).Should().BeEquivalentTo("#1", "#2");
    }

    [Fact]
    public async Task Ingest_ChangedPayloadOnReviewing_ResetsToNewAndDropsSuggestion()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var database = await SqliteTestDatabase.CreateAsync(ct);
        await using var db1 = await database.Factory.CreateDbContextAsync(ct);
        var created = await new EfTicketIngestor(db1, TimeProvider.System).IngestAsync([Make("#1")], TicketOrigin.Challenge, ct);
        var id = created[0].TicketId;
        await SetStatusAsync(database, id, TicketStatusIds.Reviewing, ct);
        await using (var claim = await database.Factory.CreateDbContextAsync(ct))
        {
            var row = await claim.Tickets.SingleAsync(t => t.Id == id, ct);
            row.ClaimedAt = DateTime.UtcNow;
            row.Retries = 2;
            await claim.SaveChangesAsync(ct);
        }

        DateTime? ingestedAt;
        await using (var before = await database.Factory.CreateDbContextAsync(ct))
        {
            ingestedAt = (await before.Tickets.SingleAsync(ct)).IngestedAt;
        }

        await using var db2 = await database.Factory.CreateDbContextAsync(ct);
        var result = await new EfTicketIngestor(db2, TimeProvider.System).IngestAsync([Make("#1", "new text")], TicketOrigin.Challenge, ct);

        result.Should().ContainSingle().Which.Should().Be(new IngestResult(id, IngestOutcome.Updated));
        await using var check = await database.Factory.CreateDbContextAsync(ct);
        var updated = await check.Tickets.Include(t => t.Comments).SingleAsync(ct);
        updated.StatusId.Should().Be(TicketStatusIds.New);
        updated.Description.Should().Be("new text");
        updated.ClaimedAt.Should().BeNull();
        updated.Retries.Should().Be(0);
        updated.Version.Should().Be(1);
        updated.IngestedAt.Should().Be(ingestedAt);
        updated.Comments.Should().HaveCount(2);
        (await check.Suggestions.AnyAsync(ct)).Should().BeFalse();
        (await check.SuggestionEdits.AnyAsync(ct)).Should().BeFalse();
    }

    [Theory]
    [InlineData(TicketStatusIds.HumanApproved)]
    [InlineData(TicketStatusIds.HumanRejected)]
    public async Task Ingest_ChangedPayloadOnDecidedTicket_IsLockedAndUntouched(int decidedStatus)
    {
        var ct = TestContext.Current.CancellationToken;
        await using var database = await SqliteTestDatabase.CreateAsync(ct);
        await using var db1 = await database.Factory.CreateDbContextAsync(ct);
        var id = (await new EfTicketIngestor(db1, TimeProvider.System).IngestAsync([Make("#1")], TicketOrigin.Challenge, ct))[0].TicketId;
        await SetStatusAsync(database, id, decidedStatus, ct);

        await using var db2 = await database.Factory.CreateDbContextAsync(ct);
        var result = await new EfTicketIngestor(db2, TimeProvider.System).IngestAsync([Make("#1", "changed")], TicketOrigin.Challenge, ct);

        result.Should().ContainSingle().Which.Should().Be(new IngestResult(id, IngestOutcome.Locked));
        await using var check = await database.Factory.CreateDbContextAsync(ct);
        var row = await check.Tickets.SingleAsync(ct);
        row.StatusId.Should().Be(decidedStatus);
        row.Description.Should().Be("printer jam");
        row.Version.Should().Be(0);
        (await check.Suggestions.CountAsync(ct)).Should().Be(1);
        (await check.SuggestionEdits.CountAsync(ct)).Should().Be(1);
    }

    [Fact]
    public async Task Ingest_TrainingOrigin_IsRejected()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var database = await SqliteTestDatabase.CreateAsync(ct);
        await using var db = await database.Factory.CreateDbContextAsync(ct);

        var act = () => new EfTicketIngestor(db, TimeProvider.System).IngestAsync([Make("#1")], TicketOrigin.Training, ct);

        await act.Should().ThrowAsync<ArgumentException>();
    }

    [Fact]
    public async Task Ingest_TicketWithoutKey_IsRejected()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var database = await SqliteTestDatabase.CreateAsync(ct);
        await using var db = await database.Factory.CreateDbContextAsync(ct);

        var act = () => new EfTicketIngestor(db, TimeProvider.System).IngestAsync([Make("")], TicketOrigin.Challenge, ct);

        await act.Should().ThrowAsync<ArgumentException>();
    }

    [Fact]
    public async Task Ingest_StoresPayloadThatRoundTripsToEqualTicket()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var database = await SqliteTestDatabase.CreateAsync(ct);
        var ticket = Make("#7") with { Urgency = "high", Impact = "low", Assignee = "Anna", Created = new DateTimeOffset(2025, 3, 4, 5, 6, 7, TimeSpan.Zero) };
        await using var db = await database.Factory.CreateDbContextAsync(ct);

        await new EfTicketIngestor(db, TimeProvider.System).IngestAsync([ticket], TicketOrigin.Intake, ct);

        await using var check = await database.Factory.CreateDbContextAsync(ct);
        var row = await check.Tickets.SingleAsync(ct);
        row.SourceHash.Should().MatchRegex("^[0-9A-F]{64}$");
        var restored = JsonSerializer.Deserialize<Ticket>(row.SourcePayload!, new JsonSerializerOptions(JsonSerializerDefaults.Web));
        restored.Should().BeEquivalentTo(ticket);
    }

    [Fact]
    public async Task Ingest_DuplicateKeysInOneCall_CollapseOntoOneRow()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var database = await SqliteTestDatabase.CreateAsync(ct);
        await using var db = await database.Factory.CreateDbContextAsync(ct);

        var result = await new EfTicketIngestor(db, TimeProvider.System)
            .IngestAsync([Make("#1"), Make("#2"), Make("#1")], TicketOrigin.Challenge, ct);

        result.Should().HaveCount(3);
        result[0].TicketId.Should().Be(result[2].TicketId);
        result[0].TicketId.Should().NotBe(result[1].TicketId);
        await using var check = await database.Factory.CreateDbContextAsync(ct);
        (await check.Tickets.CountAsync(ct)).Should().Be(2);
    }

    [Fact]
    [Trait("Category", "Smoke")]
    public async Task Ingest_ChallengeBatch_Smoke()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var database = await SqliteTestDatabase.CreateAsync(ct);
        await using var db = await database.Factory.CreateDbContextAsync(ct);
        var tickets = Enumerable.Range(1, 20).Select(n => Make($"#{n}", $"issue {n}")).ToList();

        var result = await new EfTicketIngestor(db, TimeProvider.System).IngestAsync(tickets, TicketOrigin.Challenge, ct);

        result.Should().HaveCount(20);
        result.Select(r => r.TicketId).Should().OnlyHaveUniqueItems();
    }
}
