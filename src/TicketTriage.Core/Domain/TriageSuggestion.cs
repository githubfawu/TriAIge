using System.Text.Json.Serialization;

namespace TicketTriage.Core.Domain;

/// <summary>What the triage pipeline proposes for a ticket, before human review.</summary>
public sealed record TriageSuggestion
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

    [JsonPropertyName("Urgency")]
    public required Urgency Urgency { get; init; }

    [JsonPropertyName("Impact")]
    public required Impact Impact { get; init; }

    /// <summary>Always derived via <see cref="PriorityMatrix"/>, never predicted.</summary>
    [JsonPropertyName("Priority")]
    public Priority Priority => PriorityMatrix.Resolve(Urgency, Impact);

    [JsonPropertyName("Resolution")]
    public ResolutionStatus? ResolutionStatus { get; init; }

    [JsonPropertyName("Draft Comment")]
    public string? DraftComment { get; init; }

    /// <summary>Keys of the historical tickets that informed this suggestion (explainability).</summary>
    [JsonPropertyName("Similar Tickets")]
    public IReadOnlyList<string> SimilarTicketKeys { get; init; } = [];
}
