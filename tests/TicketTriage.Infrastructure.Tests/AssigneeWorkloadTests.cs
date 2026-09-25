using TicketTriage.Core.Domain;
using TicketTriage.Infrastructure.Persistence;
using TicketTriage.Infrastructure.Routing;

namespace TicketTriage.Infrastructure.Tests;

public class AssigneeWorkloadTests
{
    private static AssigneeWorkloadProvider With(params (string Assignee, int Count)[] counts) =>
        new(_ => Task.FromResult<IReadOnlyDictionary<string, int>>(counts.ToDictionary(c => c.Assignee, c => c.Count)));

    [Fact]
    public async Task Peek_ReturnsPersonWithFewestTickets()
    {
        using var sut = With(("alice", 5), ("bob", 2), ("carol", 9));

        (await sut.PeekLeastLoadedAsync(TestContext.Current.CancellationToken)).Should().Be("bob");
    }

    [Fact]
    public async Task Peek_Tie_PicksAlphabeticallyFirstIgnoringCase()
    {
        using var sut = With(("bob", 1), ("Alice", 1), ("carol", 1));

        (await sut.PeekLeastLoadedAsync(TestContext.Current.CancellationToken)).Should().Be("Alice");
    }

    [Fact]
    public async Task Peek_DoesNotChangeTheCounts()
    {
        var ct = TestContext.Current.CancellationToken;
        using var sut = With(("alice", 1), ("bob", 2));

        await sut.PeekLeastLoadedAsync(ct);

        (await sut.PeekLeastLoadedAsync(ct)).Should().Be("alice");
    }

    [Fact]
    public async Task Reserve_MovesTheNextTicketToTheNextPerson()
    {
        var ct = TestContext.Current.CancellationToken;
        using var sut = With(("alice", 1), ("bob", 2), ("carol", 2));
        List<string?> picked = [];

        for (var i = 0; i < 5; i++)
        {
            var next = await sut.PeekLeastLoadedAsync(ct);
            await sut.ReserveAsync(next, ct);
            picked.Add(next);
        }

        // alice 1 -> 2, then all at 2: alice, bob, carol, then all at 3 again.
        picked.Should().Equal("alice", "alice", "bob", "carol", "alice");
    }

    [Fact]
    public async Task Reserve_NullOrUnknownAssignee_IsIgnored()
    {
        var ct = TestContext.Current.CancellationToken;
        using var sut = With(("alice", 1), ("bob", 2));

        await sut.ReserveAsync(null, ct);
        await sut.ReserveAsync("mallory", ct);

        (await sut.PeekLeastLoadedAsync(ct)).Should().Be("alice");
    }

    [Fact]
    public async Task Peek_NoKnownAssignees_ReturnsNullAndReloadsLater()
    {
        var ct = TestContext.Current.CancellationToken;
        var loads = 0;
        using var sut = new AssigneeWorkloadProvider(_ => Task.FromResult<IReadOnlyDictionary<string, int>>(
            ++loads == 1 ? new Dictionary<string, int>() : new Dictionary<string, int> { ["alice"] = 1 }));

        (await sut.PeekLeastLoadedAsync(ct)).Should().BeNull();
        (await sut.PeekLeastLoadedAsync(ct)).Should().Be("alice");
        await sut.PeekLeastLoadedAsync(ct);

        loads.Should().Be(2, "an empty result is retried, a filled one is cached");
    }

    [Fact]
    public async Task Database_CountsTrainingTicketsAndStoredSuggestions_IgnoringOtherOrigins()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var db = await SqliteTestDatabase.CreateAsync(ct);
        await db.AddTicketAsync(1, "a", assignee: "alice", cancellationToken: ct);
        await db.AddTicketAsync(2, "b", assignee: "alice", cancellationToken: ct);
        await db.AddTicketAsync(3, "c", assignee: "bob", cancellationToken: ct);
        await db.AddTicketAsync(4, "d", assignee: "mallory", origin: TicketOrigin.Challenge, sourceKey: "#1", cancellationToken: ct);
        await using (var context = await db.Factory.CreateDbContextAsync(ct))
        {
            // bob 1 + 2 suggested = 3 > alice 2, so alice is the least loaded again after the suggestions are counted.
            context.Suggestions.AddRange(
                new TriageSuggestionEntity { TicketId = 3, Assignee = "bob" },
                new TriageSuggestionEntity { TicketId = 4, Assignee = "bob" });
            await context.SaveChangesAsync(ct);
        }

        using var sut = new AssigneeWorkloadProvider(db.Factory);

        (await sut.PeekLeastLoadedAsync(ct)).Should().Be("alice");
        await sut.ReserveAsync("alice", ct);
        await sut.ReserveAsync("alice", ct);
        (await sut.PeekLeastLoadedAsync(ct)).Should().Be("bob", "mallory is not a training assignee and never becomes a candidate");
    }
}
