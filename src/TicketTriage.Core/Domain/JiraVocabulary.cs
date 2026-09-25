namespace TicketTriage.Core.Domain;

/// <summary>
/// Maps <see cref="Urgency"/> and <see cref="Impact"/> to the scale used by the real Jira export
/// (<c>Highest / High / Medium / Low / Lowest</c>) and back.
/// </summary>
/// <remarks>
/// Assumption (requirements §7 no. 1): Critical = Highest and Major = Highest, No Impact = Lowest; the export
/// uses one five-step scale for both fields. The casing (challenge file "Highest", training file "highest")
/// is not confirmed by the organizers.
/// </remarks>
public static class JiraVocabulary
{
    public static string ToJira(Urgency urgency) => urgency switch
    {
        Urgency.Critical => "Highest",
        Urgency.High => "High",
        Urgency.Medium => "Medium",
        Urgency.Low => "Low",
        Urgency.Lowest => "Lowest",
        _ => throw new ArgumentOutOfRangeException(nameof(urgency), urgency, null),
    };

    public static string ToJira(Impact impact) => impact switch
    {
        Impact.Major => "Highest",
        Impact.Significant => "High",
        Impact.Moderate => "Medium",
        Impact.Minor => "Low",
        Impact.NoImpact => "Lowest",
        _ => throw new ArgumentOutOfRangeException(nameof(impact), impact, null),
    };

    public static bool TryParseUrgency(string? value, out Urgency urgency)
    {
        var parsed = (value?.Trim().ToLowerInvariant()) switch
        {
            "highest" or "critical" => (Urgency?)Urgency.Critical,
            "high" => Urgency.High,
            "medium" => Urgency.Medium,
            "low" => Urgency.Low,
            "lowest" => Urgency.Lowest,
            _ => null,
        };
        urgency = parsed.GetValueOrDefault();
        return parsed.HasValue;
    }

    public static bool TryParseImpact(string? value, out Impact impact)
    {
        var parsed = (value?.Trim().ToLowerInvariant()) switch
        {
            "highest" or "major" => (Impact?)Impact.Major,
            "high" or "significant" => Impact.Significant,
            "medium" or "moderate" => Impact.Moderate,
            "low" or "minor" => Impact.Minor,
            "lowest" or "no impact" => Impact.NoImpact,
            _ => null,
        };
        impact = parsed.GetValueOrDefault();
        return parsed.HasValue;
    }
}
