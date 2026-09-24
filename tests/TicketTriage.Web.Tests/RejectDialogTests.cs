using Microsoft.Extensions.DependencyInjection;
using MudBlazor;
using TicketTriage.Web.Components.Shared;
using TicketTriage.Web.Tests.Support;

namespace TicketTriage.Web.Tests;

/// <summary>AC9: the reject reason is mandatory - Confirm stays disabled until one is entered. Rendered through
/// the real <see cref="IDialogService"/>/<see cref="MudDialogProvider"/> pipeline, since MudBlazor's own
/// &lt;MudDialog&gt; renders nothing when hosted outside of it.</summary>
public sealed class RejectDialogTests : TriageBunitContext
{
    [Fact]
    public async Task Confirm_NoReason_IsDisabled()
    {
        var providerCut = Render<MudDialogProvider>();
        var dialogService = Services.GetRequiredService<IDialogService>();

        await dialogService.ShowAsync<RejectDialog>("Reject ticket");

        providerCut.Find("button.mud-button-filled").HasAttribute("disabled").Should().BeTrue();
    }

    [Fact]
    public async Task Confirm_WithReason_IsEnabled_AndClosesWithReason()
    {
        var providerCut = Render<MudDialogProvider>();
        var dialogService = Services.GetRequiredService<IDialogService>();

        var dialogRef = await dialogService.ShowAsync<RejectDialog>("Reject ticket");

        providerCut.Find("textarea").Input("Duplicate of TT-9");
        var confirmButton = providerCut.Find("button.mud-button-filled");
        confirmButton.HasAttribute("disabled").Should().BeFalse();

        confirmButton.Click();

        var result = await dialogRef.Result;
        result.Should().NotBeNull();
        result!.Canceled.Should().BeFalse();
        result.Data.Should().Be("Duplicate of TT-9");
    }
}
