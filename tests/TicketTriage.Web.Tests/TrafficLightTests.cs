using MudBlazor;
using TicketTriage.Web.Components.Shared;
using TicketTriage.Web.Tests.Support;
using TicketTriage.Web.Triage;

namespace TicketTriage.Web.Tests;

/// <summary>FR12: every state must show a distinct colour (CSS class), icon and text - never just one of the three.</summary>
public sealed class TrafficLightTests : TriageBunitContext
{
    public static TheoryData<TicketDisplayState, string, string, string> States => new()
    {
        { TicketDisplayState.Queued, "tl-queued", "Queued", Icons.Material.Filled.Schedule },
        { TicketDisplayState.Analysing, "tl-analysing", "Analysing", Icons.Material.Filled.Autorenew },
        { TicketDisplayState.Failed, "tl-failed", "Failed", Icons.Material.Filled.WarningAmber },
        { TicketDisplayState.Pending, "tl-pending", "Pending", Icons.Material.Filled.RateReview },
        { TicketDisplayState.Approved, "tl-approved", "Approved", Icons.Material.Filled.CheckCircle },
        { TicketDisplayState.Rejected, "tl-rejected", "Rejected", Icons.Material.Filled.Cancel },
    };

    [Theory]
    [MemberData(nameof(States))]
    public void Render_EachState_ShowsClassIconAndText(TicketDisplayState state, string cssClass, string text, string iconPath)
    {
        var cut = Render<TrafficLight>(p => p.Add(x => x.State, state));

        cut.Markup.Should().Contain(cssClass);
        cut.Markup.Should().Contain(text);
        cut.Markup.Should().Contain(iconPath);
    }
}
