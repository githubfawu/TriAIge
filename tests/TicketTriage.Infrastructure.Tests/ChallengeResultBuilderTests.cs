using TicketTriage.Core.Abstractions;
using TicketTriage.Core.Domain;
using TicketTriage.Infrastructure.Challenge;

namespace TicketTriage.Infrastructure.Tests;

public sealed class ChallengeResultBuilderTests
{
    private static Ticket Ticket(string key) => new() { Key = key, Summary = "s-" + key };

    private static TriageSuggestion Suggestion(string key, string? comment) => new()
    {
        TicketKey = key,
        WorkType = WorkType.Incident,
        AffectedServices = ["Service A"],
        ServiceTeams = ["Team A"],
        Assignee = "stored",
        Urgency = Urgency.High,
        Impact = Impact.Minor,
        DraftComment = comment,
    };

    private sealed class CountingFallback : IFallbackSuggestionProvider
    {
        public int Calls { get; private set; }

        public Task<TriageSuggestion> CreateAsync(Ticket ticket, CancellationToken cancellationToken)
        {
            Calls++;
            return Task.FromResult(Suggestion(ticket.Key, null) with { Assignee = "fallback" });
        }
    }

    [Fact]
    public async Task BuildAsync_IdMissingFromStates_ThrowsInvalidOperationWithoutTicketData()
    {
        var states = new[] { new AnalysisState(1, Ticket("#1"), false, null) };

        var act = () => ChallengeResults.BuildAsync([1, 2], states, new CountingFallback(), TestContext.Current.CancellationToken);

        var ex = (await act.Should().ThrowAsync<InvalidOperationException>()).Which;
        ex.Message.Should().NotContain("s-#1");
    }

    [Fact]
    public async Task BuildAsync_AnalysedTickets_UseStoredSuggestion_PerAC2()
    {
        var states = new[]
        {
            new AnalysisState(1, Ticket("#1"), false, Suggestion("#1", "Draft.")),
            new AnalysisState(2, Ticket("#2"), false, Suggestion("#2", "Draft.")),
        };
        var fallback = new CountingFallback();

        var set = await ChallengeResults.BuildAsync([1, 2], states, fallback, TestContext.Current.CancellationToken);

        set.Rows.Should().HaveCount(2);
        set.Rows.Should().OnlyContain(r => r.WasAnalysed && !r.IsFallback && r.Result.Assignee == "stored");
        set.Fallbacks.Should().Be(0);
        set.NotAnalysed.Should().Be(0);
        fallback.Calls.Should().Be(0);
    }

    [Fact]
    public async Task BuildAsync_BlankDraftComment_CountsAsFallbackButAnalysed()
    {
        var states = new[] { new AnalysisState(1, Ticket("#1"), false, Suggestion("#1", "  ")) };

        var set = await ChallengeResults.BuildAsync([1], states, new CountingFallback(), TestContext.Current.CancellationToken);

        set.Fallbacks.Should().Be(1);
        set.NotAnalysed.Should().Be(0);
        set.Rows[0].IsFallback.Should().BeTrue();
        set.Rows[0].WasAnalysed.Should().BeTrue();
    }

    [Fact]
    public async Task BuildAsync_PendingTicket_UsesFallbackProvider_PerAC3()
    {
        var states = new[]
        {
            new AnalysisState(1, Ticket("#1"), false, Suggestion("#1", "Draft.")),
            new AnalysisState(2, Ticket("#2"), true, null),
        };
        var fallback = new CountingFallback();

        var set = await ChallengeResults.BuildAsync([1, 2], states, fallback, TestContext.Current.CancellationToken);

        set.Rows[1].WasAnalysed.Should().BeFalse();
        set.Rows[1].IsFallback.Should().BeTrue();
        set.Rows[1].Result.Assignee.Should().Be("fallback");
        set.Fallbacks.Should().Be(1);
        set.NotAnalysed.Should().Be(1);
    }

    [Fact]
    public async Task BuildAsync_DuplicateIds_BuildFallbackOnceButCountEveryRow()
    {
        var states = new[] { new AnalysisState(5, Ticket("#1"), true, null) };
        var fallback = new CountingFallback();

        var set = await ChallengeResults.BuildAsync([5, 5, 5], states, fallback, TestContext.Current.CancellationToken);

        fallback.Calls.Should().Be(1);
        set.Rows.Select(r => r.Position).Should().Equal(0, 1, 2);
        set.NotAnalysed.Should().Be(3);
        set.Fallbacks.Should().Be(3);
    }

    [Fact]
    public async Task BuildAsync_IdWithoutState_Throws()
    {
        var act = () => ChallengeResults.BuildAsync([9], [], new CountingFallback(), TestContext.Current.CancellationToken);

        await act.Should().ThrowAsync<InvalidOperationException>();
    }

    [Fact]
    [Trait("Category", "Smoke")]
    public async Task BuildAsync_MixedTwentyTickets_Smoke()
    {
        var ids = Enumerable.Range(1, 20).ToList();
        var states = ids
            .Select(i => i <= 15
                ? new AnalysisState(i, Ticket($"#{i}"), false, Suggestion($"#{i}", "Draft."))
                : new AnalysisState(i, Ticket($"#{i}"), true, null))
            .ToList();

        var set = await ChallengeResults.BuildAsync(ids, states, new CountingFallback(), TestContext.Current.CancellationToken);

        set.Rows.Should().HaveCount(20);
        set.NotAnalysed.Should().Be(5);
        set.Fallbacks.Should().Be(5);
    }
}
