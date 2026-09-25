namespace TicketTriage.Infrastructure.Persistence;

/// <summary>Named, persisted flag (e.g. training data imported, worker heartbeat) shared between processes.</summary>
public sealed class SystemMarkerEntity
{
    public required string Name { get; set; }

    public DateTime SetAtUtc { get; set; }
}
