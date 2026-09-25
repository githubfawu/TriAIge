using Microsoft.Extensions.Logging.Abstractions;
using TicketTriage.Core.Domain;
using TicketTriage.Infrastructure.Persistence;
using TicketTriage.Infrastructure.Sources;

namespace TicketTriage.Infrastructure.Tests;

public class DbTicketSourceTests
{
    private const int New = TicketStatusIds.New;
    private const int Approved = TicketStatusIds.HumanApproved;

    private static readonly DateTime Day1 = new(2025, 1, 1, 0, 0, 0, DateTimeKind.Utc);

    private static DbTicketSource Create(SqliteTestDatabase db, int batchSize = 100) =>
        new(db.Factory, new LookupNamesProvider(db.Factory), NullLogger<DbTicketSource>.Instance, batchSize);

    private static async Task<List<Ticket>> ReadAllAsync(DbTicketSource source, CancellationToken ct)
    {
        List<Ticket> result = [];
        await foreach (var ticket in source.GetTicketsAsync(ct))
        {
            result.Add(ticket);
        }

        return result;
    }

    [Fact]
    public async Task GetTickets_YieldsOnlyNewTickets_InCreatedDateThenIdOrder_PerAC7()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var db = await SqliteTestDatabase.CreateAsync(TicketOrigin.Intake, ct);
        await db.AddTicketAsync(1, "a", statusId: New, created: Day1.AddDays(2), cancellationToken: ct);
        await db.AddTicketAsync(2, "b", statusId: Approved, created: Day1, cancellationToken: ct);
        await db.AddTicketAsync(4, "c", statusId: New, created: Day1, cancellationToken: ct);
        await db.AddTicketAsync(3, "d", statusId: New, created: Day1, cancellationToken: ct);

        var tickets = await ReadAllAsync(Create(db), ct);

        tickets.Select(t => t.Id).Should().Equal(3, 4, 1);
    }

    [Fact]
    public async Task GetTickets_SetsIdAndDbKey_AndMapsLikeSimilarSource_PerAC7()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var db = await SqliteTestDatabase.CreateAsync(TicketOrigin.Intake, ct);
        await db.AddTicketAsync(
            7, "Printer broken", comments: ["second", "third"], workTypeId: 1, statusId: New,
            assignee: "jdoe", resolution: "Done", cancellationToken: ct);

        var ticket = (await ReadAllAsync(Create(db), ct)).Should().ContainSingle().Subject;

        ticket.Id.Should().Be(7);
        ticket.Key.Should().Be("DB-7");
        ticket.Description.Should().Be("Printer broken");
        ticket.WorkType.Should().NotBeNull();
        ticket.Assignee.Should().Be("jdoe");
        ticket.Comments.Should().Equal("second", "third");
        ticket.Urgency.Should().BeNull();
        ticket.Impact.Should().BeNull();
        ticket.Priority.Should().BeNull();
    }

    [Fact]
    public async Task GetTickets_AcrossBatchBoundaries_NoDuplicatesOrGaps()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var db = await SqliteTestDatabase.CreateAsync(TicketOrigin.Intake, ct);
        // Tie on CreatedDate straddles the boundary between batch 1 (ids 1,2) and batch 2 (ids 3,4).
        await db.AddTicketAsync(1, "a", statusId: New, created: Day1, cancellationToken: ct);
        await db.AddTicketAsync(2, "b", statusId: New, created: Day1.AddDays(1), cancellationToken: ct);
        await db.AddTicketAsync(3, "c", statusId: New, created: Day1.AddDays(1), cancellationToken: ct);
        await db.AddTicketAsync(4, "d", statusId: New, created: Day1.AddDays(1), cancellationToken: ct);
        await db.AddTicketAsync(5, "e", statusId: New, created: Day1.AddDays(2), cancellationToken: ct);

        var tickets = await ReadAllAsync(Create(db, batchSize: 2), ct);

        tickets.Select(t => t.Id).Should().Equal(1, 2, 3, 4, 5);
    }

    [Fact]
    public async Task GetTickets_CancelledMidStream_ThrowsOperationCanceled_PerAC7()
    {
        await using var db = await SqliteTestDatabase.CreateAsync(TicketOrigin.Intake, TestContext.Current.CancellationToken);
        for (var i = 1; i <= 3; i++)
        {
            await db.AddTicketAsync(i, "x", statusId: New, created: Day1.AddDays(i), cancellationToken: TestContext.Current.CancellationToken);
        }

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        var seen = 0;
        var act = async () =>
        {
            await foreach (var _ in Create(db, batchSize: 2).GetTicketsAsync(cts.Token))
            {
                seen++;
                await cts.CancelAsync();
            }
        };

        await act.Should().ThrowAsync<OperationCanceledException>();
        seen.Should().Be(1);
    }

    [Fact]
    public async Task GetTickets_EmptyOrNoNewTickets_YieldsNothing()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var db = await SqliteTestDatabase.CreateAsync(TicketOrigin.Intake, ct);
        var source = Create(db);

        (await ReadAllAsync(source, ct)).Should().BeEmpty();

        await db.AddTicketAsync(1, "a", statusId: Approved, cancellationToken: ct);
        (await ReadAllAsync(source, ct)).Should().BeEmpty();
    }

    [Fact]
    [Trait("Category", "Smoke")]
    public async Task GetTickets_Smoke_StreamsNewTicketsWithDbKeys()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var db = await SqliteTestDatabase.CreateAsync(TicketOrigin.Intake, ct);
        await db.AddTicketAsync(1, "VPN does not connect", comments: ["please help"], statusId: New, cancellationToken: ct);
        await db.AddTicketAsync(2, "Old ticket", statusId: Approved, cancellationToken: ct);

        var tickets = await ReadAllAsync(Create(db), ct);

        tickets.Should().ContainSingle().Which.Key.Should().Be("DB-1");
    }
}
