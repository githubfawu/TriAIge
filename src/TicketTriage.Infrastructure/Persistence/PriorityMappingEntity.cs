namespace TicketTriage.Infrastructure.Persistence;

/// <summary>
/// Plain Urgency x Impact -> Priority lookup table, so queries can resolve priority by a join
/// instead of code (e.g. "low urgency and low impact" -> "low priority").
/// </summary>
public sealed class PriorityMappingEntity
{
    public int Id { get; set; }

    public int UrgencyId { get; set; }

    public int ImpactId { get; set; }

    public int PriorityId { get; set; }
}
