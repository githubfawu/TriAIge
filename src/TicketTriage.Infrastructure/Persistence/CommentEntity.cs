namespace TicketTriage.Infrastructure.Persistence;

/// <summary>A comment attached to a <see cref="TicketEntity"/> (the "AllComments" one-to-many).</summary>
public sealed class CommentEntity
{
    public int CommentId { get; set; }

    public required string CommentText { get; set; }

    public int TicketId { get; set; }
}
