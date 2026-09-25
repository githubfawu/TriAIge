namespace TicketTriage.Core.Domain;

/// <summary>What ingesting one ticket did to its stored row.</summary>
public enum IngestOutcome
{
    Created,
    Unchanged,
    Updated,

    /// <summary>A changed payload arrived for a ticket an analyst already decided; the row was left untouched.</summary>
    Locked,
}

/// <summary>Result of ingesting one input ticket: the stored ticket id and what happened.</summary>
public sealed record IngestResult(int TicketId, IngestOutcome Outcome);
