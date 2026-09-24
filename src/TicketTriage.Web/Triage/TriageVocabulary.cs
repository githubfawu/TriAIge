using System.Diagnostics.CodeAnalysis;
using System.Text.Json;
using TicketTriage.Core.Domain;

namespace TicketTriage.Web.Triage;

/// <summary>
/// Core enum &lt;-&gt; Jira/DB name conversions. Names always come from the Core enums' own JSON converters
/// (never hand-mapped strings, per CLAUDE.md); Impact additionally needs a label translation because the DB
/// lookup table uses a different severity scale (kept in sync with <c>TrainingDataImporter.ImpactNameTranslation</c>).
/// </summary>
public static class TriageVocabulary
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    // Raw/Core Impact JSON name -> DB Impact lookup name. Copy of TrainingDataImporter.ImpactNameTranslation;
    // must stay in sync with it (see docs/features/web-triage-ui/requirements.md Technical Constraints).
    private static readonly IReadOnlyDictionary<string, string> ImpactNameTranslation = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
    {
        ["Major"] = "Highest",
        ["Significant"] = "High",
        ["Moderate"] = "Medium",
        ["Minor"] = "Low",
        ["No Impact"] = "Lowest",
    };

    public static string ToJsonName<TEnum>(TEnum value) where TEnum : struct, Enum =>
        JsonSerializer.Serialize(value, JsonOptions).Trim('"');

    /// <summary>Parses a raw string against a Core enum's JSON names; unknown/empty values map to null (never throw).</summary>
    public static TEnum? ParseEnumOrNull<TEnum>(string? raw) where TEnum : struct, Enum
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            return null;
        }

        try
        {
            return JsonSerializer.Deserialize<TEnum>(JsonSerializer.Serialize(raw), JsonOptions);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>Translates a raw Jira/Core impact name to the DB Impact lookup name; unrecognised input passes through
    /// unchanged so the subsequent lookup can report "unknown", exactly like <c>TrainingDataImporter</c>.</summary>
    [return: NotNullIfNotNull(nameof(rawImpact))]
    public static string? TranslateImpactNameToDb(string? rawImpact) =>
        rawImpact is not null && ImpactNameTranslation.TryGetValue(rawImpact, out var translated) ? translated : rawImpact;

    /// <summary>DB Impact lookup name for a Core <see cref="Impact"/> value (always resolvable: the enum's five
    /// JSON names are exactly the keys of <see cref="ImpactNameTranslation"/>, see <c>TriageVocabularyTests</c>).</summary>
    public static string DbImpactNameFor(Impact impact) => TranslateImpactNameToDb(ToJsonName(impact))!;

    [return: NotNullIfNotNull(nameof(value))]
    public static string? Truncate(string? value, int maxLength) =>
        value is null ? null : value.Length <= maxLength ? value : value[..maxLength];
}
