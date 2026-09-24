using TicketTriage.Core.Domain;

namespace TicketTriage.Infrastructure.Persistence;

/// <summary>A historical ticket imported from the training set. List properties are stored as JSON columns.</summary>
public sealed class TrainingTicketEntity
{
    public required string Key { get; set; }

    public required string Summary { get; set; }

    public string? Description { get; set; }

    public string? WorkType { get; set; }

    public List<string> AffectedServices { get; set; } = [];

    public List<string> ServiceTeams { get; set; } = [];

    public string? Assignee { get; set; }

    public string? Urgency { get; set; }

    public string? Impact { get; set; }

    public string? Priority { get; set; }

    public string? Resolution { get; set; }

    public DateTimeOffset? Created { get; set; }

    public List<string> Comments { get; set; } = [];

    public DateTimeOffset ImportedAt { get; set; }

    public static TrainingTicketEntity FromTicket(Ticket ticket, DateTimeOffset importedAt) => new()
    {
        Key = ticket.Key,
        Summary = ticket.Summary,
        Description = ticket.Description,
        WorkType = ticket.WorkType,
        AffectedServices = [.. ticket.AffectedServices],
        ServiceTeams = [.. ticket.ServiceTeams],
        Assignee = ticket.Assignee,
        Urgency = ticket.Urgency,
        Impact = ticket.Impact,
        Priority = ticket.Priority,
        Resolution = ticket.Resolution,
        Created = ticket.Created,
        Comments = [.. ticket.Comments],
        ImportedAt = importedAt,
    };

    public Ticket ToTicket() => new()
    {
        Key = Key,
        Summary = Summary,
        Description = Description,
        WorkType = WorkType,
        AffectedServices = AffectedServices,
        ServiceTeams = ServiceTeams,
        Assignee = Assignee,
        Urgency = Urgency,
        Impact = Impact,
        Priority = Priority,
        Resolution = Resolution,
        Created = Created,
        Comments = Comments,
    };
}
