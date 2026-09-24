using System.Text.Json.Serialization;

namespace TicketTriage.Core.Domain;

/// <summary>One entry of the batch output file that is submitted for scoring.</summary>
/// <remarks>TODO: align field set and names with the official scoring format.</remarks>
public sealed record TriageResult
{
    [JsonPropertyName("Issue key")]
    public required string TicketKey { get; init; }

    [JsonPropertyName("Work type")]
    public required WorkType WorkType { get; init; }

    [JsonPropertyName("Affected Business or IT Services")]
    public IReadOnlyList<string> AffectedServices { get; init; } = [];

    [JsonPropertyName("Service Team(s)")]
    public IReadOnlyList<string> ServiceTeams { get; init; } = [];

    [JsonPropertyName("Assignee")]
    public string? Assignee { get; init; }

    [JsonPropertyName("Priority")]
    public required Priority Priority { get; init; }

    [JsonPropertyName("All Comments")]
    public IReadOnlyList<string> Comments { get; init; } = [];

    public static TriageResult From(TriageSuggestion suggestion) => new()
    {
        TicketKey = suggestion.TicketKey,
        WorkType = suggestion.WorkType,
        AffectedServices = suggestion.AffectedServices,
        ServiceTeams = suggestion.ServiceTeams,
        Assignee = suggestion.Assignee,
        Priority = suggestion.Priority,
        Comments = suggestion.DraftComment is { Length: > 0 } draft ? [draft] : [],
    };
}
