namespace TicketTriage.Infrastructure.Persistence;

/// <summary>One field the analyst changed relative to the AI suggestion.</summary>
public sealed class SuggestionEditEntity
{
    public int Id { get; set; }

    public int TicketId { get; set; }

    /// <summary>Name of a <see cref="Core.Domain.SuggestionField"/>.</summary>
    public required string Field { get; set; }

    public string? AiValue { get; set; }

    public string? FinalValue { get; set; }

    public DateTime EditedAtUtc { get; set; }
}
