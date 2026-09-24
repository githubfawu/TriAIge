using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using TicketTriage.Core.Domain;
using TicketTriage.Web.Components.Pages;
using TicketTriage.Web.Tests.Support;
using TicketTriage.Web.Triage;

namespace TicketTriage.Web.Tests;

public sealed class TicketsPageTests : TriageBunitContext
{
    private static Ticket MakeTicket(string key) => new() { Key = key, Summary = $"Summary for {key}" };

    [Fact]
    public async Task Row_TransitionsQueuedToAnalysingToPending_WithoutReload_PerFR14()
    {
        var store = new TriageSessionStore(NullLogger<TriageSessionStore>.Instance);
        var boardQuery = new FakeBoardQuery();
        Services.AddSingleton(store);
        Services.AddSingleton<ITriageBoardQuery>(boardQuery);

        store.Register(1, "TT-1", MakeTicket("TT-1"), uploadId: 1);
        boardQuery.Rows = [new TicketBoardRow(1, "TT-1", "Summary for TT-1", "Incident", null, "—", null, TicketDisplayState.Queued, null)];

        var cut = Render<Tickets>();
        cut.WaitForAssertion(() => cut.Markup.Should().Contain("Queued"));

        boardQuery.Rows = [new TicketBoardRow(1, "TT-1", "Summary for TT-1", "Incident", null, "—", null, TicketDisplayState.Analysing, null)];
        await store.DequeueAsync(Xunit.TestContext.Current.CancellationToken);
        cut.WaitForAssertion(() => cut.Markup.Should().Contain("Analysing"));

        var suggestion = new TriageSuggestion { TicketKey = "TT-1", WorkType = WorkType.Incident, Urgency = Urgency.Medium, Impact = Impact.Moderate };
        boardQuery.Rows = [new TicketBoardRow(1, "TT-1", "Summary for TT-1", "Incident", "Incident", "—", "Medium", TicketDisplayState.Pending, null)];
        store.CompleteAnalysis(1, suggestion, []);
        cut.WaitForAssertion(() => cut.Markup.Should().Contain("Pending"));
    }

    [Fact]
    public void FailedRow_ShowsRequeueButton_PerAC11()
    {
        var store = new TriageSessionStore(NullLogger<TriageSessionStore>.Instance);
        var boardQuery = new FakeBoardQuery
        {
            Rows = [new TicketBoardRow(1, "TT-1", "Summary for TT-1", "Incident", null, "—", null, TicketDisplayState.Failed, "InvalidOperationException")],
        };
        Services.AddSingleton(store);
        Services.AddSingleton<ITriageBoardQuery>(boardQuery);

        var cut = Render<Tickets>();

        cut.Markup.Should().Contain("Re-queue");
    }

    [Fact]
    public void Rows_NeverIncludeTrainingTickets_PerFR13()
    {
        // TriageBoardQuery itself never returns Finished-status (training) rows (see TriageBoardQueryTests);
        // this asserts the page renders exactly what the query returns, nothing more.
        var store = new TriageSessionStore(NullLogger<TriageSessionStore>.Instance);
        var boardQuery = new FakeBoardQuery
        {
            Rows = [new TicketBoardRow(1, "TT-1", "Summary for TT-1", "Incident", null, "—", null, TicketDisplayState.Pending, null)],
        };
        Services.AddSingleton(store);
        Services.AddSingleton<ITriageBoardQuery>(boardQuery);

        var cut = Render<Tickets>();

        cut.FindAll("tr").Should().HaveCountGreaterThan(0);
        cut.Markup.Should().NotContain("training");
    }

    [Fact]
    public void SupplyStateFromQuery_PreselectsFilterChip_PerAC13()
    {
        var store = new TriageSessionStore(NullLogger<TriageSessionStore>.Instance);
        var boardQuery = new FakeBoardQuery
        {
            Rows =
            [
                new TicketBoardRow(1, "TT-1", "Summary for TT-1", "Incident", null, "—", null, TicketDisplayState.Failed, "boom"),
                new TicketBoardRow(2, "TT-2", "Summary for TT-2", "Incident", null, "—", null, TicketDisplayState.Pending, null),
            ],
        };
        Services.AddSingleton(store);
        Services.AddSingleton<ITriageBoardQuery>(boardQuery);

        // [SupplyParameterFromQuery] parameters are only supplied via the (fake) NavigationManager, not
        // Render(p => p.Add(...)) - bunit throws with this exact guidance if you try the latter.
        var navigation = Services.GetRequiredService<NavigationManager>();
        navigation.NavigateTo(navigation.GetUriWithQueryParameter("state", "Failed"));

        var cut = Render<Tickets>();

        cut.Markup.Should().Contain("TT-1");
        cut.Markup.Should().NotContain("TT-2");
    }

    private sealed class FakeBoardQuery : ITriageBoardQuery
    {
        public IReadOnlyList<TicketBoardRow> Rows { get; set; } = [];

        public Task<IReadOnlyList<TicketBoardRow>> GetRowsAsync(CancellationToken cancellationToken) =>
            Task.FromResult(Rows);

        public Task<TicketReviewData?> GetReviewAsync(int id, CancellationToken cancellationToken) =>
            Task.FromResult<TicketReviewData?>(null);

        public Task<int?> GetNextPendingIdAsync(int excludeId, CancellationToken cancellationToken) =>
            Task.FromResult<int?>(null);

        public Task<DecisionTotals> GetDecisionTotalsAsync(CancellationToken cancellationToken) =>
            Task.FromResult(new DecisionTotals(0, 0));
    }
}
