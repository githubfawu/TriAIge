using TicketTriage.Web.Triage;

namespace TicketTriage.Web.Tests;

public class TriageWorkQueueTests
{
    [Fact]
    public void TryEnqueue_ThenDequeue_IsFifo()
    {
        var queue = new TriageWorkQueue();

        queue.TryEnqueue(1).Should().BeTrue();
        queue.TryEnqueue(2).Should().BeTrue();
        queue.TryEnqueue(3).Should().BeTrue();

        queue.TryDequeue(out var first).Should().BeTrue();
        queue.TryDequeue(out var second).Should().BeTrue();
        queue.TryDequeue(out var third).Should().BeTrue();

        (first, second, third).Should().Be((1, 2, 3));
    }

    [Fact]
    public void TryEnqueue_AlreadyQueued_IsNoOp()
    {
        var queue = new TriageWorkQueue();

        queue.TryEnqueue(1).Should().BeTrue();
        queue.TryEnqueue(1).Should().BeFalse();
        queue.Count.Should().Be(1);
    }

    [Fact]
    public void MoveToFront_QueuedTicket_DequeuedFirst()
    {
        var queue = new TriageWorkQueue();
        queue.TryEnqueue(1);
        queue.TryEnqueue(2);
        queue.TryEnqueue(3);

        queue.MoveToFront(3);

        queue.TryDequeue(out var first).Should().BeTrue();
        first.Should().Be(3);
    }

    [Fact]
    public void TryDequeue_EmptyQueue_ReturnsFalse()
    {
        var queue = new TriageWorkQueue();

        queue.TryDequeue(out _).Should().BeFalse();
    }
}
