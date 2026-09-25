namespace TicketTriage.Core.Domain;

/// <summary>Known service teams and the rules for analyst-entered team and assignee values.</summary>
public static class ServiceTeamCatalog
{
    public const int MaxNameLength = 100;

    // Mirrors the ServiceTeams lookup seed (a test pins both lists together).
    public static IReadOnlyList<string> All { get; } =
    [
        "Service Desk",
        "Enterprise Applications",
        "Investment Operations",
        "Affected Business or IT Services",
        "Securities Operations",
        "Risk & Controls",
        "Valuation & Pricing",
        "Client Services",
        "Market Data Services",
        "Trading Support",
        "Tax & Reporting",
        "Treasury & Cash",
    ];

    /// <summary>The canonical spelling of a known team, or null when the name is not a known team.</summary>
    public static string? Find(string? name)
    {
        var trimmed = name?.Trim();
        return string.IsNullOrEmpty(trimmed)
            ? null
            : All.FirstOrDefault(t => string.Equals(t, trimmed, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>Trims and drops control characters; returns null when nothing usable or too long remains.</summary>
    public static string? NormalizeAssignee(string? value)
    {
        if (value is null)
        {
            return null;
        }

        var cleaned = new string([.. value.Where(c => !char.IsControl(c))]).Trim();
        return cleaned.Length is 0 or > MaxNameLength ? null : cleaned;
    }
}
