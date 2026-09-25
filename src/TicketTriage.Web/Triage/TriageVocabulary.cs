using System.Text.Json;

namespace TicketTriage.Web.Triage;

/// <summary>
/// Display helper for Core enums: renders the same Jira-style names the enums' own JSON converters use
/// (<c>"Service Request"</c>, <c>"No Impact"</c>, …), so the UI never hand-maps a label (CLAUDE.md).
/// </summary>
public static class TriageVocabulary
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public static string ToJsonName<TEnum>(TEnum value) where TEnum : struct, Enum =>
        JsonSerializer.Serialize(value, JsonOptions).Trim('"');
}
