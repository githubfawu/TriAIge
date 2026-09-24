using Microsoft.Extensions.Logging.Abstractions;
using TicketTriage.Core.Domain;
using TicketTriage.Web.Triage;

namespace TicketTriage.Web.Tests;

public class TriageSessionStoreTests
{
    private static Ticket MakeTicket(string key) => new() { Key = key, Summary = $"Summary for {key}" };

    private static TriageSessionStore CreateStore() => new(NullLogger<TriageSessionStore>.Instance);

    [Fact]
    public void Register_NewTicket_IsQueued_AndRaisesTicketChanged()
    {
        var store = CreateStore();
        var changed = new List<int>();
        store.TicketChanged += changed.Add;

        store.Register(1, "TT-1", MakeTicket("TT-1"), uploadId: 1);

        store.Get(1)!.Phase.Should().Be(QueuePhase.Queued);
        changed.Should().Contain(1);
    }

    [Fact]
    public void Register_AlreadyQueued_IsNoOp()
    {
        var store = CreateStore();
        store.Register(1, "TT-1", MakeTicket("TT-1"), uploadId: 1);

        store.Register(1, "TT-1", MakeTicket("TT-1"), uploadId: 1);

        store.GetUploadProgress(1).Total.Should().Be(1);
    }

    [Fact]
    public async Task DequeueAsync_RegisteredTicket_MarksAnalysing_AndIncrementsAttempts()
    {
        var store = CreateStore();
        store.Register(1, "TT-1", MakeTicket("TT-1"), uploadId: 1);

        var entry = await store.DequeueAsync(Xunit.TestContext.Current.CancellationToken);

        entry.TicketId.Should().Be(1);
        entry.Phase.Should().Be(QueuePhase.Analysing);
        entry.Attempts.Should().Be(1);
        store.Get(1)!.Phase.Should().Be(QueuePhase.Analysing);
    }

    [Fact]
    public async Task FailAttempt_BelowMaxAttempts_RequeuesAsQueued()
    {
        var store = CreateStore();
        store.Register(1, "TT-1", MakeTicket("TT-1"), uploadId: 1);
        await store.DequeueAsync(Xunit.TestContext.Current.CancellationToken);

        store.FailAttempt(1, "boom", maxAttempts: 3);

        var entry = store.Get(1)!;
        entry.Phase.Should().Be(QueuePhase.Queued);
        entry.FailureReason.Should().Be("boom");
    }

    [Fact]
    public async Task FailAttempt_AtMaxAttempts_MarksFailed_PerAC11()
    {
        var store = CreateStore();
        store.Register(1, "TT-1", MakeTicket("TT-1"), uploadId: 1);

        for (var attempt = 1; attempt <= 3; attempt++)
        {
            await store.DequeueAsync(Xunit.TestContext.Current.CancellationToken);
            store.FailAttempt(1, $"attempt {attempt}", maxAttempts: 3);
        }

        store.Get(1)!.Phase.Should().Be(QueuePhase.Failed);
    }

    [Fact]
    public async Task Requeue_FailedTicket_BackToQueued_WithAttemptsReset_PerAC11()
    {
        var store = CreateStore();
        store.Register(1, "TT-1", MakeTicket("TT-1"), uploadId: 1);
        await store.DequeueAsync(Xunit.TestContext.Current.CancellationToken);
        store.FailAttempt(1, "boom", maxAttempts: 1);
        store.Get(1)!.Phase.Should().Be(QueuePhase.Failed);

        var requeued = store.Requeue(1);

        requeued.Should().BeTrue();
        var entry = store.Get(1)!;
        entry.Phase.Should().Be(QueuePhase.Queued);
        entry.Attempts.Should().Be(0);
    }

    [Fact]
    public void Requeue_TicketNotFailed_ReturnsFalse()
    {
        var store = CreateStore();
        store.Register(1, "TT-1", MakeTicket("TT-1"), uploadId: 1);

        store.Requeue(1).Should().BeFalse();
    }

    [Fact]
    public async Task CompleteAnalysis_StoresSuggestionAndNotInCatalog()
    {
        var store = CreateStore();
        store.Register(1, "TT-1", MakeTicket("TT-1"), uploadId: 1);
        await store.DequeueAsync(Xunit.TestContext.Current.CancellationToken);

        var suggestion = new TriageSuggestion
        {
            TicketKey = "TT-1",
            WorkType = WorkType.Incident,
            Urgency = Urgency.Medium,
            Impact = Impact.Moderate,
        };
        store.CompleteAnalysis(1, suggestion, ["ServiceTeam"]);

        var entry = store.Get(1)!;
        entry.Suggestion.Should().Be(suggestion);
        entry.NotInCatalog.Should().ContainSingle().Which.Should().Be("ServiceTeam");
    }

    [Fact]
    public async Task RegisterAndDequeue_ManyTicketsConcurrently_StaysConsistent_PerNFR7()
    {
        var store = CreateStore();
        const int count = 200;

        var registerTasks = Enumerable.Range(1, count)
            .Select(id => Task.Run(() => store.Register(id, $"TT-{id}", MakeTicket($"TT-{id}"), uploadId: 1)));
        await Task.WhenAll(registerTasks);

        var dequeued = new System.Collections.Concurrent.ConcurrentBag<int>();
        var dequeueTasks = Enumerable.Range(0, count)
            .Select(async _ =>
            {
                var entry = await store.DequeueAsync(Xunit.TestContext.Current.CancellationToken);
                dequeued.Add(entry.TicketId);
            });
        await Task.WhenAll(dequeueTasks);

        dequeued.Distinct().Should().HaveCount(count);
    }
}
