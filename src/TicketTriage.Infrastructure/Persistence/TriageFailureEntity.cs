namespace TicketTriage.Infrastructure.Persistence;

/// <summary>A failed triage attempt. TicketId is deliberately not a foreign key so failures survive unknown/removed tickets.</summary>
public sealed class TriageFailureEntity
{
    public int Id { get; set; }

    public int? TicketId { get; set; }

    public required string TicketKey { get; set; }

    public int Attempt { get; set; }

    public required string Reason { get; set; }

    public required string ExceptionType { get; set; }

    public string? StackTrace { get; set; }

    public DateTime OccurredAtUtc { get; set; }
}
