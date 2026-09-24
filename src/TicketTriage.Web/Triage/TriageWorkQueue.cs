namespace TicketTriage.Web.Triage;

/// <summary>
/// FIFO order of ticket ids waiting to be analysed. Not thread-safe by itself: callers (only
/// <see cref="TriageSessionStore"/>) must hold its lock while mutating or reading this queue.
/// </summary>
internal sealed class TriageWorkQueue
{
    private readonly LinkedList<int> _order = [];

    public int Count => _order.Count;

    /// <summary>Appends to the back; no-op (returns false) if the id is already queued.</summary>
    public bool TryEnqueue(int ticketId)
    {
        if (_order.Contains(ticketId))
        {
            return false;
        }

        _order.AddLast(ticketId);
        return true;
    }

    public void MoveToFront(int ticketId)
    {
        var node = _order.Find(ticketId);
        if (node is null)
        {
            return;
        }

        _order.Remove(node);
        _order.AddFirst(ticketId);
    }

    public bool TryDequeue(out int ticketId)
    {
        if (_order.First is null)
        {
            ticketId = 0;
            return false;
        }

        ticketId = _order.First.Value;
        _order.RemoveFirst();
        return true;
    }
}
