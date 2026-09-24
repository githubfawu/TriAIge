using MudBlazor;

namespace TicketTriage.Web.Components.Layout;

/// <summary>FR23: theme colours are Code (Primary), LLM+Code (Secondary) and LLM (Tertiary), distinct from the
/// traffic-light palette in <c>app.css</c>.</summary>
public static class TriageTheme
{
    public static MudTheme Default { get; } = new MudTheme
    {
        PaletteLight = new PaletteLight
        {
            Primary = "#2563eb",
            Secondary = "#7c3aed",
            Tertiary = "#d97706",
        },
    };
}
