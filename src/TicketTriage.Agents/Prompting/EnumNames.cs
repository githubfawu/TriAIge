using System.Reflection;
using System.Text.Json.Serialization;

namespace TicketTriage.Agents.Prompting;

/// <summary>Maps Core enums to and from the names used in the Jira export (<c>JsonStringEnumMemberName</c>).</summary>
internal static class EnumNames
{
    public static IReadOnlyList<string> All<TEnum>() where TEnum : struct, Enum =>
        [.. Enum.GetValues<TEnum>().Select(NameOf)];

    public static string NameOf<TEnum>(TEnum value) where TEnum : struct, Enum =>
        typeof(TEnum).GetField(value.ToString())?.GetCustomAttribute<JsonStringEnumMemberNameAttribute>()?.Name
        ?? value.ToString();

    public static TEnum Parse<TEnum>(string? text, string fieldName) where TEnum : struct, Enum
    {
        var candidate = text?.Trim();
        foreach (var value in Enum.GetValues<TEnum>())
        {
            if (string.Equals(NameOf(value), candidate, StringComparison.OrdinalIgnoreCase))
            {
                return value;
            }
        }

        throw new InvalidOperationException($"The model returned an invalid {fieldName}.");
    }
}
