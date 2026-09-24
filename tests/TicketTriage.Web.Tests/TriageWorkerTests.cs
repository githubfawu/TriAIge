using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using TicketTriage.Core.Abstractions;
using TicketTriage.Core.Domain;
using TicketTriage.Infrastructure.Persistence;
using TicketTriage.Web.Tests.Support;
using TicketTriage.Web.Triage;

namespace TicketTriage.Web.Tests;

public sealed class TriageWorkerTests : IAsyncLifetime
{
    private TestDatabase _database = null!;
    private LookupCatalog _catalog = null!;
    private TriageSessionStore _store = null!;
    private FakeTriagePipeline _pipeline = null!;
    private ServiceProvider _serviceProvider = null!;

    public async ValueTask InitializeAsync()
    {
        var cancellationToken = Xunit.TestContext.Current.CancellationToken;
        _database = await TestDatabase.CreateAsync(cancellationToken);
        _catalog = new LookupCatalog(_database.CreateFactory());
        await _catalog.EnsureLoadedAsync(cancellationToken);
        _pipeline = new FakeTriagePipeline();
        _store = new TriageSessionStore(NullLogger<TriageSessionStore>.Instance);

        var services = new ServiceCollection();
        services.AddSingleton(_database.CreateFactory());
        services.AddSingleton(_catalog);
        services.AddSingleton<ITriagePipeline>(_pipeline);
        services.AddScoped<ISuggestionWriter, SuggestionWriter>();
        _serviceProvider = services.BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true, ValidateOnBuild = true });
    }

    public async ValueTask DisposeAsync()
    {
        await _serviceProvider.DisposeAsync();
        await _database.DisposeAsync();
    }

    private TriageWorker CreateWorker(int maxAttempts = 3, TimeSpan? timeout = null) => new(
        _store,
        _serviceProvider.GetRequiredService<IServiceScopeFactory>(),
        Options.Create(new TriageWorkerOptions { MaxAttempts = maxAttempts, Timeout = timeout ?? TimeSpan.FromSeconds(60) }),
        NullLogger<TriageWorker>.Instance);

    private async Task<int> InsertNewTicketAsync(CancellationToken cancellationToken)
    {
        await using var db = _database.CreateContext();
        var ticket = new TicketEntity
        {
            WorkTypeId = _catalog.DefaultWorkTypeId,
            Summary = "Worker test ticket",
            StatusId = _catalog.NewStatusId,
            CreatedDate = DateTime.UtcNow,
        };
        db.Tickets.Add(ticket);
        await db.SaveChangesAsync(cancellationToken);
        return ticket.Id;
    }

    private static async Task WaitUntilAsync(Func<bool> condition, CancellationToken cancellationToken)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        cts.CancelAfter(TimeSpan.FromSeconds(10));
        while (!condition())
        {
            cts.Token.ThrowIfCancellationRequested();
            await Task.Delay(20, cts.Token);
        }
    }

    [Fact]
    public async Task Queued_Succeeds_BecomesPending()
    {
        var cancellationToken = Xunit.TestContext.Current.CancellationToken;
        var ticketId = await InsertNewTicketAsync(cancellationToken);
        _store.Register(ticketId, "TT-1", new Ticket { Key = "TT-1", Summary = "x" }, uploadId: 1);

        var worker = CreateWorker();
        await worker.StartAsync(cancellationToken);
        try
        {
            await WaitUntilAsync(() => _store.Get(ticketId)?.Suggestion is not null, cancellationToken);
        }
        finally
        {
            await worker.StopAsync(CancellationToken.None);
        }

        await using var db = _database.CreateContext();
        var ticket = await db.Tickets.FindAsync([ticketId], cancellationToken);
        ticket!.WorkTypeChangedId.Should().NotBeNull();
    }

    [Fact]
    public async Task ThreeFailures_MarksFailed_CallsEqualsThree_NoChangedColumnsPersisted_PerAC11()
    {
        var cancellationToken = Xunit.TestContext.Current.CancellationToken;
        var ticketId = await InsertNewTicketAsync(cancellationToken);
        _pipeline.Behavior = FakeBehavior.Throw;
        _store.Register(ticketId, "TT-1", new Ticket { Key = "TT-1", Summary = "x" }, uploadId: 1);

        var worker = CreateWorker(maxAttempts: 3);
        await worker.StartAsync(cancellationToken);
        try
        {
            await WaitUntilAsync(() => _store.Get(ticketId)?.Phase == QueuePhase.Failed, cancellationToken);
        }
        finally
        {
            await worker.StopAsync(CancellationToken.None);
        }

        _pipeline.Calls.Should().Be(3);

        await using var db = _database.CreateContext();
        var ticket = await db.Tickets.FindAsync([ticketId], cancellationToken);
        ticket!.WorkTypeChangedId.Should().BeNull("a failed attempt must never persist a partial suggestion");
    }

    [Fact]
    public async Task Timeout_CountsAsAFailedAttempt()
    {
        var cancellationToken = Xunit.TestContext.Current.CancellationToken;
        var ticketId = await InsertNewTicketAsync(cancellationToken);
        _pipeline.Behavior = FakeBehavior.Hang;
        _store.Register(ticketId, "TT-1", new Ticket { Key = "TT-1", Summary = "x" }, uploadId: 1);

        var worker = CreateWorker(maxAttempts: 1, timeout: TimeSpan.FromMilliseconds(100));
        await worker.StartAsync(cancellationToken);
        try
        {
            await WaitUntilAsync(() => _store.Get(ticketId)?.Phase == QueuePhase.Failed, cancellationToken);
        }
        finally
        {
            await worker.StopAsync(CancellationToken.None);
        }

        _store.Get(ticketId)!.Attempts.Should().Be(1);
    }

    [Fact]
    public async Task Requeue_AfterFailure_BecomesPending_PerAC11()
    {
        var cancellationToken = Xunit.TestContext.Current.CancellationToken;
        var ticketId = await InsertNewTicketAsync(cancellationToken);
        _pipeline.Behavior = FakeBehavior.Throw;
        _store.Register(ticketId, "TT-1", new Ticket { Key = "TT-1", Summary = "x" }, uploadId: 1);

        var worker = CreateWorker(maxAttempts: 1);
        await worker.StartAsync(cancellationToken);
        try
        {
            await WaitUntilAsync(() => _store.Get(ticketId)?.Phase == QueuePhase.Failed, cancellationToken);

            _pipeline.Behavior = FakeBehavior.Succeed;
            _store.Requeue(ticketId);

            await WaitUntilAsync(() => _store.Get(ticketId)?.Suggestion is not null, cancellationToken);
        }
        finally
        {
            await worker.StopAsync(CancellationToken.None);
        }

        _store.Get(ticketId)!.Phase.Should().NotBe(QueuePhase.Failed);
    }

    [Fact]
    public async Task OneBadTicket_DoesNotBlockTheNextTicket()
    {
        var cancellationToken = Xunit.TestContext.Current.CancellationToken;
        var failingId = await InsertNewTicketAsync(cancellationToken);
        var goodId = await InsertNewTicketAsync(cancellationToken);

        _pipeline.SuggestionFactory = ticket => ticket.Key == "BAD"
            ? throw new InvalidOperationException("bad ticket")
            : new TriageSuggestion { TicketKey = ticket.Key, WorkType = WorkType.Incident, Urgency = Urgency.Medium, Impact = Impact.Moderate };

        _store.Register(failingId, "BAD", new Ticket { Key = "BAD", Summary = "x" }, uploadId: 1);
        _store.Register(goodId, "GOOD", new Ticket { Key = "GOOD", Summary = "y" }, uploadId: 1);

        var worker = CreateWorker(maxAttempts: 1);
        await worker.StartAsync(cancellationToken);
        try
        {
            await WaitUntilAsync(() => _store.Get(goodId)?.Suggestion is not null, cancellationToken);
        }
        finally
        {
            await worker.StopAsync(CancellationToken.None);
        }

        _store.Get(failingId)!.Phase.Should().Be(QueuePhase.Failed);
    }
}
