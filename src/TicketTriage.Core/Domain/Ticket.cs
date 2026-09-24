using System.Text.Json.Serialization;

namespace TicketTriage.Core.Domain;

/// <summary>
/// A Jira-style ticket as found in the training / challenge JSON files.
/// Classification fields are raw strings because the source data is not guaranteed to be clean;
/// the typed values live on <see cref="TriageSuggestion"/>.
/// </summary>
/// <remarks>TODO: verify every JSON property name against the real input files once available.</remarks>
public sealed record Ticket
{
    [JsonPropertyName("Issue key")]
    public required string Key { get; init; }

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
