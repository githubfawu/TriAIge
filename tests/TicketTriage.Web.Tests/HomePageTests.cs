using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Logging.Abstractions;
using TicketTriage.Core.Domain;
using TicketTriage.Web.Components.Pages;
using TicketTriage.Web.Tests.Support;
using TicketTriage.Web.Triage;

namespace TicketTriage.Web.Tests;

public sealed class HomePageTests : TriageBunitContext
{
    private static Ticket MakeTicket(string key) => new() { Key = key, Summary = $"Summary for {key}" };

    [Fact]
    public void Dashboard_ShowsCountsApprovalRateAcceptanceRateAndEdits_PerAC13()
    {
        var store = new TriageSessionStore(NullLogger<TriageSessionStore>.Instance);
        var boardQuery = new FakeBoardQuery
        {
            Rows =
            [
                new TicketBoardRow(1, "TT-1", "Summary", "Incident", null, "—", null, TicketDisplayState.Pending, null),
                new TicketBoardRow(2, "TT-2", "Summary", "Incident", null, "—", null, TicketDisplayState.Failed, "boom"),
            ],
            Totals = new DecisionTotals(3, 1),
        };
        store.Register(10, "TT-10", MakeTicket("TT-10"), uploadId: 1);
        store.RecordDecision(10, ReviewDecision.Approved, editedFields: [], rejectReason: null);
        store.Register(11, "TT-11", MakeTicket("TT-11"), uploadId: 1);
        store.RecordDecision(11, ReviewDecision.Approved, editedFields: [ReviewField.Urgency], rejectReason: null);

        Services.AddSingleton(store);
        Services.AddSingleton<ITriageBoardQuery>(boardQuery);
        Services.AddSingleton<HealthCheckService>(new FakeHealthCheckService());

        var cut = Render<Home>();

        cut.Markup.Should().Contain(TicketDisplayState.Pending.ToString());
        cut.Markup.Should().Contain(TicketDisplayState.Failed.ToString());
        cut.Markup.Should().Contain("3 approved / 1 rejected");
        cut.Markup.Should().Contain(0.75.ToString("P0")); // approval rate 3/(3+1)
        cut.Markup.Should().Contain(0.5.ToString("P0")); // acceptance rate: 1 of 2 approved with 0 edits
        cut.Markup.Should().Contain("Urgency: 1");
    }

    [Fact]
    public void NoDecisionsYet_ApprovalAndAcceptanceRatesShowPlaceholder_PerAC13()
    {
        var store = new TriageSessionStore(NullLogger<TriageSessionStore>.Instance);
        Services.AddSingleton(store);
        Services.AddSingleton<ITriageBoardQuery>(new FakeBoardQuery());
        Services.AddSingleton<HealthCheckService>(new FakeHealthCheckService());

        var cut = Render<Home>();

        cut.Markup.Should().Contain("—");
    }

    [Fact]
    public void StateCard_LinksToFilteredTicketsPage_PerAC13()
    {
        var store = new TriageSessionStore(NullLogger<TriageSessionStore>.Instance);
        Services.AddSingleton(store);
        Services.AddSingleton<ITriageBoardQuery>(new FakeBoardQuery());
        Services.AddSingleton<HealthCheckService>(new FakeHealthCheckService());

        var cut = Render<Home>();

        cut.Find($"a[href='/tickets?state={TicketDisplayState.Failed}']").Should().NotBeNull();
    }

    [Fact]
    public void TicketChanged_ReloadsBoardAndMetrics_WithoutReprobingHealth_PerTechnicalConstraints()
    {
        var store = new TriageSessionStore(NullLogger<TriageSessionStore>.Instance);
        var boardQuery = new FakeBoardQuery();
        var health = new FakeHealthCheckService();
        Services.AddSingleton(store);
        Services.AddSingleton<ITriageBoardQuery>(boardQuery);
        Services.AddSingleton<HealthCheckService>(health);

        var cut = Render<Home>();
        var callsAfterLoad = health.CallCount;
        callsAfterLoad.Should().BeGreaterThan(0);

        cut.Find($"a[href='/tickets?state={TicketDisplayState.Pending}'] h5").TextContent.Should().Be("0");

        boardQuery.Rows = [new TicketBoardRow(1, "TT-1", "Summary", "Incident", null, "—", null, TicketDisplayState.Pending, null)];
        store.Register(1, "TT-1", MakeTicket("TT-1"), uploadId: 1);

        cut.WaitForAssertion(() => cut.Find($"a[href='/tickets?state={TicketDisplayState.Pending}'] h5").TextContent.Should().Be("1"));
        health.CallCount.Should().Be(callsAfterLoad);
    }

    private sealed class FakeBoardQuery : ITriageBoardQuery
    {
        public IReadOnlyList<TicketBoardRow> Rows { get; set; } = [];

        public DecisionTotals Totals { get; set; } = new(0, 0);

        public Task<IReadOnlyList<TicketBoardRow>> GetRowsAsync(CancellationToken cancellationToken) => Task.FromResult(Rows);

        public Task<TicketReviewData?> GetReviewAsync(int id, CancellationToken cancellationToken) => Task.FromResult<TicketReviewData?>(null);

        public Task<int?> GetNextPendingIdAsync(int excludeId, CancellationToken cancellationToken) => Task.FromResult<int?>(null);

        public Task<DecisionTotals> GetDecisionTotalsAsync(CancellationToken cancellationToken) => Task.FromResult(Totals);
    }
}
