using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using TicketTriage.Core.Domain;
using TicketTriage.Infrastructure.Pipeline;

namespace TicketTriage.Infrastructure.Tests;

public class TriagePipelineTests
{
    private readonly CallLog _log = new();
    private readonly RecordingSimilarSource _similar;
    private readonly FakeDrafter _drafter;
    private readonly TriagePipeline _pipeline;

    public TriagePipelineTests()
    {
        _similar = new RecordingSimilarSource(_log);
        _drafter = new FakeDrafter(_log);
        _pipeline = new TriagePipeline(
            new TicketNormalizer(NullLogger<TicketNormalizer>.Instance),
            _similar,
            new FakeClassifier(_log),
            new FakeRouter(_log),
            new FakeRoutingStatisticsSource(),
            _drafter,
            Options.Create(new TriageOptions { SimilarTicketCount = 7 }),
            new RecordingFailureStore(),
            NullLogger<TriagePipeline>.Instance);
    }

    [Fact]
    [Trait("Category", "Smoke")]
    public async Task Stream_NTickets_ReturnsNSuggestionsInInputOrder_PerAC1()
    {
        var ct = TestContext.Current.CancellationToken;
        var input = new[] { "T-3", "T-1", "T-2" }.Select(k => Tickets.Make(k)).ToArray();

        var result = await _pipeline.TriageAsync(Tickets.Stream(input), ct).ToListAsync(ct);

        result.Select(s => s.TicketKey).Should().Equal("T-3", "T-1", "T-2");
        result.Should().OnlyContain(s => s.DraftComment == "draft text" && s.SimilarTicketKeys.SequenceEqual(new[] { "OLD-1" }));
        result.Should().OnlyContain(s => s.ResolutionStatus == ResolutionStatus.Clarification);
    }

    [Fact]
    public async Task Triage_DrafterReceivesRoutingDecision_PerFR6()
    {
        await _pipeline.TriageAsync(Tickets.Make("T-1"), TestContext.Current.CancellationToken);

        var routing = _drafter.Routings.Should().ContainSingle().Which;
        routing.ServiceTeams.Should().Equal("Team A");
        routing.Assignee.Should().Be("alice");
    }

    [Fact]
    public async Task Stream_Empty_ReturnsNoSuggestions_PerAC1()
    {
        var ct = TestContext.Current.CancellationToken;

        var result = await _pipeline.TriageAsync(Tickets.Stream(), ct).ToListAsync(ct);

        result.Should().BeEmpty();
        _log.Steps.Should().BeEmpty();
    }

    [Fact]
    public async Task Triage_PassesTicketAndConfiguredTopToSimilarSource_PerAC2()
    {
        var ct = TestContext.Current.CancellationToken;

        await _pipeline.TriageAsync(Tickets.Make("T-1"), ct);

        _similar.Calls.Should().ContainSingle();
        _similar.Calls[0].Ticket.Key.Should().Be("T-1");
        _similar.Calls[0].Top.Should().Be(7);
    }

    [Fact]
    public async Task Triage_TeamAssigneeFromRouterAndPriorityFromMatrix_PerAC3()
    {
        var ct = TestContext.Current.CancellationToken;
        var ticket = Tickets.Make("T-1") with { Assignee = "mallory", Priority = "Lowest", ServiceTeams = ["Other"] };

        var suggestion = await _pipeline.TriageAsync(ticket, ct);

        suggestion.ServiceTeams.Should().Equal("Team A");
        suggestion.Assignee.Should().Be("alice");
        suggestion.Priority.Should().Be(PriorityMatrix.Resolve(Urgency.High, Impact.Significant));
    }

    [Fact]
    public async Task Triage_RunsStepsInFr3Order()
    {
        await _pipeline.TriageAsync(Tickets.Make("T-1"), TestContext.Current.CancellationToken);

        _log.Steps.Should().Equal("similar", "classify", "route", "draft");
    }

    [Fact]
    public async Task Triage_SimilarSourceAlwaysFails_ReturnsFallbackInsteadOfThrowing_PerAC5()
    {
        var pipeline = new TriagePipeline(
            new TicketNormalizer(NullLogger<TicketNormalizer>.Instance),
            new ThrowingSimilarSource(),
            new FakeClassifier(_log),
            new FakeRouter(_log),
            new FakeRoutingStatisticsSource(),
            new FakeDrafter(_log),
            Options.Create(new TriageOptions { RetryCount = 2, RetryDelayMilliseconds = 0 }),
            new RecordingFailureStore(),
            NullLogger<TriagePipeline>.Instance);

        var suggestion = await pipeline.TriageAsync(Tickets.Make("T-1"), TestContext.Current.CancellationToken);

        suggestion.DraftComment.Should().BeNull();
        suggestion.ServiceTeams.Should().BeEmpty("no similar tickets means no service to route by");
    }

    private sealed class ThrowingSimilarSource : TicketTriage.Core.Abstractions.ISimilarTicketSource
    {
        public Task<IReadOnlyList<SimilarTicket>> FindSimilarAsync(Ticket ticket, int top, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("boom");
    }
}
