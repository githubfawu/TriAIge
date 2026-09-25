namespace TicketTriage.Core.Domain;

/// <summary>Where a stored ticket came from. Values are persisted; never reorder or renumber.</summary>
public enum TicketOrigin
{
    /// <summary>Historical knowledge (training file). Never analysed, only used for retrieval and statistics.</summary>
    Training = 0,
    Challenge = 1,
    Intake = 2,
}
