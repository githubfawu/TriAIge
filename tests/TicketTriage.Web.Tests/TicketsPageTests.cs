using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;
using TicketTriage.Core.Abstractions;
using TicketTriage.Web.Components.Pages;
using TicketTriage.Web.Tests.Support;
using TicketTriage.Web.Triage;

namespace TicketTriage.Web.Tests;

public sealed class TicketsPageTests : TriageBunitContext
{
    [Fact]
    public void FailedRow_ShowsRequeueButton_PerAC11()
    {
        var board = new FakeTicketBoardQuery
        {
            Rows = [new TicketBoardRow(1, 1, "DB-1", "Summary for TT-1", "Incident", null, "—", null, TicketDisplayState.Failed, "InvalidOperationException")],
        };
        Services.AddSingleton<ITicketBoardQuery>(board);
        Services.AddSingleton<IReviewService>(new FakeReviewService());

        var cut = Render<Tickets>();

        cut.Markup.Should().Contain("Re-queue");
    }

    [Fact]
    public void RequeueButton_CallsReviewServiceWithRowVersion_PerAC11()
    {
        var board = new FakeTicketBoardQuery
        {
            Rows = [new TicketBoardRow(7, 3, "DB-7", "Summary", "Incident", null, "—", null, TicketDisplayState.Failed, "boom")],
        };
        var reviewService = new FakeReviewService();
        Services.AddSingleton<ITicketBoardQuery>(board);
        Services.AddSingleton<IReviewService>(reviewService);

        var cut = Render<Tickets>();
        cut.Find("button.mud-button-outlined").Click();

        reviewService.RequeueCalls.Should().ContainSingle().Which.Should().Be(7);
    }

    [Fact]
    public void Rows_OnlyShowWhatTheQueryReturns_PerFR13()
    {
        var board = new FakeTicketBoardQuery
        {
            Rows = [new TicketBoardRow(1, 1, "DB-1", "Summary for TT-1", "Incident", null, "—", null, TicketDisplayState.Pending, null)],
        };
        Services.AddSingleton<ITicketBoardQuery>(board);
        Services.AddSingleton<IReviewService>(new FakeReviewService());

        var cut = Render<Tickets>();

        cut.FindAll("tr").Should().HaveCountGreaterThan(0);
        cut.Markup.Should().NotContain("training");
    }

    [Fact]
    public void SupplyStateFromQuery_PreselectsFilterChip_PerAC13()
    {
        var board = new FakeTicketBoardQuery
        {
            Rows =
            [
                new TicketBoardRow(1, 1, "DB-1", "Summary for TT-1", "Incident", null, "—", null, TicketDisplayState.Failed, "boom"),
                new TicketBoardRow(2, 1, "DB-2", "Summary for TT-2", "Incident", null, "—", null, TicketDisplayState.Pending, null),
            ],
        };
        Services.AddSingleton<ITicketBoardQuery>(board);
        Services.AddSingleton<IReviewService>(new FakeReviewService());

        // [SupplyParameterFromQuery] parameters are only supplied via the (fake) NavigationManager, not
        // Render(p => p.Add(...)) - bunit throws with this exact guidance if you try the latter.
        var navigation = Services.GetRequiredService<NavigationManager>();
        navigation.NavigateTo(navigation.GetUriWithQueryParameter("state", "Failed"));

        var cut = Render<Tickets>();

        cut.Markup.Should().Contain("DB-1");
        cut.Markup.Should().NotContain("DB-2");
    }
}
