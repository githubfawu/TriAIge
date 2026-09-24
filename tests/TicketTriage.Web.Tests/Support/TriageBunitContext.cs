using Bunit;
using MudBlazor.Services;

namespace TicketTriage.Web.Tests.Support;

/// <summary>Shared bUnit base for TriAIge component tests: registers MudBlazor services and relaxes JS interop
/// (MudBlazor components call into JS for popovers/portals that bUnit doesn't render).</summary>
public class TriageBunitContext : BunitContext
{
    protected TriageBunitContext()
    {
        Services.AddMudServices();
        JSInterop.Mode = JSRuntimeMode.Loose;
    }
}
