using System.Text.Json.Serialization;

namespace TicketTriage.Core.Domain;

// Serialized names match the values in the Jira export (e.g. "Service Request", "No Impact").

[JsonConverter(typeof(JsonStringEnumConverter<WorkType>))]
public enum WorkType
{
    Incident,

    [JsonStringEnumMemberName("Service Request")]
    ServiceRequest,
}

/// <remarks>Declaration order is significant: it is the row order of <see cref="PriorityMatrix"/>.</remarks>
[JsonConverter(typeof(JsonStringEnumConverter<Urgency>))]
public enum Urgency
{
    Critical,
    High,
    Medium,
    Low,
    Lowest,
}

/// <remarks>Declaration order is significant: it is the column order of <see cref="PriorityMatrix"/>.</remarks>
[JsonConverter(typeof(JsonStringEnumConverter<Impact>))]
public enum Impact
{
    Major,
    Significant,
    Moderate,
    Minor,

    [JsonStringEnumMemberName("No Impact")]
    NoImpact,
}

[JsonConverter(typeof(JsonStringEnumConverter<Priority>))]
public enum Priority
{
    Highest,
    High,
    Medium,
    Low,
    Lowest,
}

[JsonConverter(typeof(JsonStringEnumConverter<ResolutionStatus>))]
public enum ResolutionStatus
{
    Done,
    Cancelled,
    Clarification,

    [JsonStringEnumMemberName("Cannot Reproduce")]
    CannotReproduce,
}

/// <summary>Outcome of the human review of a <see cref="TriageSuggestion"/>.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<ReviewDecision>))]
public enum ReviewDecision
{
    Pending,
    Approved,
    Rejected,
}

[JsonConverter(typeof(JsonStringEnumConverter<ServiceRating>))]
public enum ServiceRating
{
    Critical,
    NonCritical,
}
