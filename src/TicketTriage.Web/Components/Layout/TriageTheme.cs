using MudBlazor;

namespace TicketTriage.Web.Components.Layout;

/// <summary>
/// Visual style modelled on the Kirridesk service-desk design: light grey canvas, white surfaces, green primary
/// actions, near-black secondary actions, compact Inter typography. The field-owner badges keep the pipeline colours
/// of architecture.md §4 (FR23); the traffic light keeps its own semantic palette in <c>app.css</c>.
/// </summary>
public static class TriageTheme
{
    public static MudTheme Default { get; } = new MudTheme
    {
        PaletteLight = new PaletteLight
        {
            Primary = "#22a55b",
            PrimaryContrastText = "#ffffff",
            Secondary = "#111827",
            Tertiary = "#7c3aed",
            Info = "#0284c7",
            Success = "#16a34a",
            Warning = "#d97706",
            Error = "#dc2626",
            Background = "#eef1f5",
            Surface = "#ffffff",
            AppbarBackground = "#ffffff",
            AppbarText = "#111827",
            DrawerBackground = "#f7f8fa",
            DrawerText = "#374151",
            DrawerIcon = "#6b7280",
            TextPrimary = "#111827",
            TextSecondary = "#6b7280",
            LinesDefault = "#e5e7eb",
            TableLines = "#eef0f3",
            TableHover = "#f7f8fa",
            Divider = "#e5e7eb",
            ActionDefault = "#6b7280",
        },
        Typography = new Typography
        {
            Default = new DefaultTypography
            {
                FontFamily = ["Inter", "Helvetica", "Arial", "sans-serif"],
                FontSize = "0.875rem",
                LineHeight = "1.5",
            },
            H4 = new H4Typography { FontFamily = ["Inter", "sans-serif"], FontSize = "1.5rem", FontWeight = "600", LineHeight = "1.3" },
            H5 = new H5Typography { FontFamily = ["Inter", "sans-serif"], FontSize = "1.35rem", FontWeight = "600" },
            H6 = new H6Typography { FontFamily = ["Inter", "sans-serif"], FontSize = "1rem", FontWeight = "600" },
            Subtitle1 = new Subtitle1Typography { FontFamily = ["Inter", "sans-serif"], FontSize = "0.8125rem", FontWeight = "600" },
            Body1 = new Body1Typography { FontFamily = ["Inter", "sans-serif"], FontSize = "0.875rem" },
            Body2 = new Body2Typography { FontFamily = ["Inter", "sans-serif"], FontSize = "0.8125rem" },
            Caption = new CaptionTypography { FontFamily = ["Inter", "sans-serif"], FontSize = "0.75rem" },
            Button = new ButtonTypography { FontFamily = ["Inter", "sans-serif"], FontSize = "0.8125rem", FontWeight = "600", TextTransform = "none" },
        },
        LayoutProperties = new LayoutProperties
        {
            DefaultBorderRadius = "8px",
            DrawerWidthLeft = "240px",
            AppbarHeight = "56px",
        },
    };
}
