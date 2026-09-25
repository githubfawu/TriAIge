using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using TicketTriage.Core.Abstractions;
using TicketTriage.Core.Domain;
using TicketTriage.Web.Components.Pages;
using TicketTriage.Web.Tests.Support;
using TicketTriage.Web.Triage;

namespace TicketTriage.Web.Tests;

public sealed class HomePageTests : TriageBunitContext
{
    private FakeTicketBoardQuery RegisterServices(FakeTriageMetricsService? metrics = null, FakeHealthCheckService? health = null)
    {
        var board = new FakeTicketBoardQuery();
        Services.AddSingleton<ITicketBoardQuery>(board);
        Services.AddSingleton<ITriageMetricsService>(metrics ?? new FakeTriageMetricsService());
        Services.AddSingleton<HealthCheckService>(health ?? new FakeHealthCheckService());
        return board;
    }

    [Fact]
    public void Dashboard_ShowsCountsAndMetrics_PerFR22()
    {
        var board = RegisterServices(new FakeTriageMetricsService
        {
            Metrics = new TriageMetrics(4, 4, 3, 2, 1, 0.5, new Dictionary<SuggestionField, int> { [SuggestionField.Urgency] = 1 }, null, null),
        });
        board.Rows =
        [
            new TicketBoardRow(1, 1, "DB-1", "Summary", "Incident", null, "—", null, TicketDisplayState.Pending, null),
            new TicketBoardRow(2, 1, "DB-2", "Summary", "Incident", null, "—", null, TicketDisplayState.Failed, "boom"),
        ];

        var cut = Render<Home>();

        cut.Markup.Should().Contain(TicketDisplayState.Pending.ToString());
        cut.Markup.Should().Contain(TicketDisplayState.Failed.ToString());
        cut.Markup.Should().Contain("3 approved / 1 rejected");
        cut.Markup.Should().Contain(0.5.ToString("P0"));
        cut.Markup.Should().Contain("Urgency: 1");
    }

    [Fact]
    public void NoDecisionsYet_AcceptanceRateShowsPlaceholder_PerFR22()
    {
        RegisterServices();

        var cut = Render<Home>();

        cut.Markup.Should().Contain("—");
    }

    [Fact]
    public void StateCard_LinksToFilteredTicketsPage_PerFR22()
    {
        RegisterServices();

        var cut = Render<Home>();

        cut.Find($"a[href='/tickets?state={TicketDisplayState.Failed}']").Should().NotBeNull();
    }

    [Fact]
    public void RefreshButton_ReloadsBoardAndHealth()
    {
        var health = new FakeHealthCheckService();
        var board = RegisterServices(health: health);
        board.Rows = [new TicketBoardRow(1, 1, "DB-1", "Summary", "Incident", null, "—", null, TicketDisplayState.Pending, null)];

        var cut = Render<Home>();
        var callsAfterLoad = health.CallCount;
        callsAfterLoad.Should().BeGreaterThan(0);

        board.Rows = [.. board.Rows, new TicketBoardRow(2, 1, "DB-2", "Summary", "Incident", null, "—", null, TicketDisplayState.Pending, null)];
        cut.Find("button").Click();

        cut.WaitForAssertion(() => cut.Find($"a[href='/tickets?state={TicketDisplayState.Pending}'] h5").TextContent.Should().Be("2"));
        health.CallCount.Should().Be(callsAfterLoad + 1);
    }
}
