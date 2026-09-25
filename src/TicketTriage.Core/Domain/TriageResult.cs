using System.Text.Json.Serialization;

namespace TicketTriage.Core.Domain;

/// <summary>
/// The predicted fields of one scored record. It carries no key: the batch output mirrors the input record
/// and overwrites exactly these properties, so <c>Issue key</c> is never invented.
/// </summary>
/// <remarks>Field names and vocabulary are unconfirmed by the organizers (requirements §7 no. 2); change them here only.</remarks>
public sealed record TriageResult
{
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

    /// <summary>Jira vocabulary, see <see cref="JiraVocabulary"/>.</summary>
    [JsonPropertyName("Urgency")]
    public required string Urgency { get; init; }

    /// <summary>Jira vocabulary, see <see cref="JiraVocabulary"/>.</summary>
    [JsonPropertyName("Impact")]
    public required string Impact { get; init; }

    /// <summary>Resolution status in the export vocabulary (done, cancelled, clarification, cannot reproduce).</summary>
    [JsonPropertyName("Resolution")]
    public ResolutionStatus? Resolution { get; init; }

    [JsonPropertyName("All Comments")]
    public IReadOnlyList<string> Comments { get; init; } = [];

    public static TriageResult From(TriageSuggestion suggestion) => new()
    {
        WorkType = suggestion.WorkType,
        AffectedServices = suggestion.AffectedServices,
        ServiceTeams = suggestion.ServiceTeams,
        Assignee = suggestion.Assignee,
        Priority = suggestion.Priority,
        Urgency = JiraVocabulary.ToJira(suggestion.Urgency),
        Impact = JiraVocabulary.ToJira(suggestion.Impact),
        Resolution = suggestion.ResolutionStatus,
        Comments = !string.IsNullOrWhiteSpace(suggestion.DraftComment) ? [suggestion.DraftComment] : [],
    };
}
