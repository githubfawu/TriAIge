using System.Text.Json.Serialization;

namespace TicketTriage.Core.Domain;

/// <summary>
/// A Jira-style ticket as found in the training / challenge JSON files.
/// Classification fields are raw strings because the source data is not guaranteed to be clean;
/// the typed values live on <see cref="TriageSuggestion"/>.
/// </summary>
/// <remarks>
/// Property names verified against the real training and challenge exports. Those files carry no
/// <c>Issue key</c> and the export's <c>Created date</c> is not ISO, so <see cref="Created"/> stays unmapped.
/// </remarks>
public sealed record Ticket
{
    /// <summary>Database id; not part of the JSON files.</summary>
    [JsonIgnore]
    public int? Id { get; init; }

    /// <summary>Jira key if the record has one, else blank; the batch reader then assigns a positional identity.</summary>
    [JsonPropertyName("Issue key")]
    public string Key { get; init; } = "";

    [JsonPropertyName("Summary")]
    public required string Summary { get; init; }

    [JsonPropertyName("Description")]
    public string? Description { get; init; }

    [JsonPropertyName("Work type")]
    public string? WorkType { get; init; }

    [JsonPropertyName("Affected Business or IT Services")]
    public IReadOnlyList<string> AffectedServices { get; init; } = [];

    [JsonPropertyName("Service Team(s)")]
    public IReadOnlyList<string> ServiceTeams { get; init; } = [];

    [JsonPropertyName("Assignee")]
    public string? Assignee { get; init; }

    [JsonPropertyName("Urgency")]
    public string? Urgency { get; init; }

    [JsonPropertyName("Impact")]
    public string? Impact { get; init; }

    [JsonPropertyName("Priority")]
    public string? Priority { get; init; }

    [JsonPropertyName("Resolution")]
    public string? Resolution { get; init; }

    [JsonPropertyName("Created")]
    public DateTimeOffset? Created { get; init; }

    [JsonPropertyName("All Comments")]
    public IReadOnlyList<string> Comments { get; init; } = [];
}
