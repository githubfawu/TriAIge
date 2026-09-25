using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using TicketTriage.Core.Abstractions;
using TicketTriage.Core.Domain;
using TicketTriage.Infrastructure.Analysis;
using TicketTriage.Infrastructure.Ingestion;
using TicketTriage.Infrastructure.Persistence;
using TicketTriage.Infrastructure.Pipeline;

namespace TicketTriage.Infrastructure.Tests;

/// <summary>Review fixes for backend-completion: heartbeat, poison payloads, claim/ingest races, drain, startup release, ingest caps.</summary>
public sealed class AnalysisHardeningTests
{
    private sealed class MutableTime(DateTimeOffset start) : TimeProvider
    {
        public DateTimeOffset Current { get; set; } = start;

        public override DateTimeOffset GetUtcNow() => Current;
    }

    private sealed class FakePipeline(Func<Ticket, CancellationToken, Task<TriageSuggestion>> handler) : ITriagePipeline
    {
        public Task<TriageSuggestion> TriageAsync(Ticket ticket, CancellationToken cancellationToken) => handler(ticket, cancellationToken);

        public IAsyncEnumerable<TriageSuggestion> TriageAsync(IAsyncEnumerable<Ticket> tickets, CancellationToken cancellationToken) =>
            throw new NotSupportedException();
    }

    private static readonly DateTimeOffset T0 = new(2026, 9, 25, 12, 0, 0, TimeSpan.Zero);

    private static Ticket Make(int n) => new() { Key = $"#{n}", Summary = $"Summary {n}", Description = $"Description {n}" };

    private static TriageSuggestion Suggest(Ticket ticket) => new()
    {
        TicketKey = ticket.Key ?? "?",
        WorkType = WorkType.Incident,
        Urgency = Urgency.Medium,
        Impact = Impact.Moderate,
        DraftComment = "ok",
    };

    private static async Task IngestAsync(SqliteTestDatabase database, int count, TicketOrigin origin = TicketOrigin.Challenge)
    {
        var ct = TestContext.Current.CancellationToken;
        await using var db = await database.Factory.CreateDbContextAsync(ct);
        await new EfTicketIngestor(db, TimeProvider.System).IngestAsync([.. Enumerable.Range(1, count).Select(Make)], origin, ct);
    }

    private static TicketClaimStore Store(SqliteTestDatabase database, TimeProvider time, int retryCount = 3) =>
        new(database.Factory, Options.Create(new AnalysisOptions()), Options.Create(new TriageOptions { RetryCount = retryCount }), time);

    private static async Task<List<TicketEntity>> TicketsAsync(SqliteTestDatabase database)
    {
        await using var db = await database.Factory.CreateDbContextAsync(TestContext.Current.CancellationToken);
        return await db.Tickets.AsNoTracking().OrderBy(t => t.Id).ToListAsync(TestContext.Current.CancellationToken);
    }

    private static async Task MarkReadyAsync(SqliteTestDatabase database)
    {
        await using var db = await database.Factory.CreateDbContextAsync(TestContext.Current.CancellationToken);
        db.SystemMarkers.Add(new SystemMarkerEntity { Name = SystemMarkerNames.TrainingDataReady, SetAtUtc = DateTime.UtcNow });
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);
    }

    private static IAnalysisCycle BuildCycle(
        SqliteTestDatabase database,
        TimeProvider time,
        Func<Ticket, CancellationToken, Task<TriageSuggestion>> handler)
    {
        var services = new ServiceCollection();
        services.AddSingleton(database.Factory);
        services.AddSingleton(time);
        services.AddSingleton(Options.Create(new AnalysisOptions()));
        services.AddSingleton(Options.Create(new TriageOptions()));
        services.AddLogging();
        services.AddScoped<TicketClaimStore>();
        services.AddScoped<ITriagePipeline>(_ => new FakePipeline(handler));
        services.AddSingleton<IAnalysisCycle, AnalysisCycle>();
        return services.BuildServiceProvider().GetRequiredService<IAnalysisCycle>();
    }

    private static async Task<DateTime> HeartbeatAsync(SqliteTestDatabase database)
    {
        await using var db = await database.Factory.CreateDbContextAsync(TestContext.Current.CancellationToken);
        return (await db.SystemMarkers.AsNoTracking()
            .SingleAsync(m => m.Name == SystemMarkerNames.AnalysisWorkerHeartbeat, TestContext.Current.CancellationToken)).SetAtUtc;
    }

    // Fix 1

    [Fact]
    public async Task RunOnce_SeveralTickets_HeartbeatMovesAfterEachTicket_PerAC3()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var database = await SqliteTestDatabase.CreateAsync(ct);
        await IngestAsync(database, 3);
        await MarkReadyAsync(database);
        var time = new MutableTime(T0);
        List<DateTime> seenAtTicketStart = [];
        var cycle = BuildCycle(database, time, async (t, _) =>
        {
            seenAtTicketStart.Add(await HeartbeatAsync(database));
            time.Current = time.Current.AddMinutes(1);
            return Suggest(t);
        });

        await cycle.RunOnceAsync(ct);

        seenAtTicketStart.Should().Equal(T0.UtcDateTime, T0.UtcDateTime.AddMinutes(1), T0.UtcDateTime.AddMinutes(2));
        (await HeartbeatAsync(database)).Should().Be(T0.UtcDateTime.AddMinutes(3));
    }

    // Fix 2

    [Theory]
    [InlineData(null)]
    [InlineData("{not json")]
    [InlineData("null")]
    public async Task Claim_PoisonPayload_DoesNotThrowReleasesAndMarksFailed_PerAC3(string? payload)
    {
        var ct = TestContext.Current.CancellationToken;
        await using var database = await SqliteTestDatabase.CreateAsync(ct);
        await IngestAsync(database, 2);
        await using (var db = await database.Factory.CreateDbContextAsync(ct))
        {
            await db.Tickets.Where(t => t.SourceKey == "#1")
                .ExecuteUpdateAsync(s => s.SetProperty(t => t.SourcePayload, payload), ct);
        }

        var store = Store(database, new MutableTime(T0));
        var claimed = await store.ClaimAsync(5, ct);

        claimed.Should().ContainSingle().Which.Ticket.Summary.Should().Be("Summary 2");
        var rows = await TicketsAsync(database);
        rows[0].ClaimedAt.Should().BeNull();
        rows[0].Retries.Should().Be(3);
        rows[0].StatusId.Should().Be(TicketStatusIds.New);
        (await store.ClaimAsync(5, ct)).Should().BeEmpty("a failed ticket stays out of the queue");
    }

    [Fact]
    public async Task Claim_TicketWithExhaustedRetries_IsNotClaimed_PerAC3()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var database = await SqliteTestDatabase.CreateAsync(ct);
        await IngestAsync(database, 2);
        await using (var db = await database.Factory.CreateDbContextAsync(ct))
        {
            await db.Tickets.Where(t => t.SourceKey == "#2").ExecuteUpdateAsync(s => s.SetProperty(t => t.Retries, 3), ct);
        }

        var claimed = await Store(database, new MutableTime(T0)).ClaimAsync(5, ct);

        claimed.Should().ContainSingle().Which.Ticket.Summary.Should().Be("Summary 1");
    }

    [Fact]
    public async Task GetStates_CorruptPayload_UsesEntityColumnsInsteadOfThrowing_PerAC6()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var database = await SqliteTestDatabase.CreateAsync(ct);
        await IngestAsync(database, 1);
        await using (var db = await database.Factory.CreateDbContextAsync(ct))
        {
            await db.Tickets.ExecuteUpdateAsync(s => s.SetProperty(t => t.SourcePayload, "{broken"), ct);
        }

        var id = (await TicketsAsync(database)).Single().Id;
        var states = await new EfAnalysisMonitor(database.Factory).GetStatesAsync([id], ct);

        states.Should().ContainSingle();
        states[0].Ticket.Summary.Should().Be("Summary 1");
        states[0].Ticket.Description.Should().Be("Description 1");
        states[0].IsPending.Should().BeTrue();
    }

    // Fix 3

    [Fact]
    public async Task Save_AfterReingestChangedPayload_ReturnsFalseAndStoresNothing_PerAC3()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var database = await SqliteTestDatabase.CreateAsync(ct);
        await IngestAsync(database, 1);
        var store = Store(database, new MutableTime(T0));
        var claimed = (await store.ClaimAsync(5, ct)).Single();

        await using (var db = await database.Factory.CreateDbContextAsync(ct))
        {
            await new EfTicketIngestor(db, TimeProvider.System)
                .IngestAsync([Make(1) with { Description = "changed" }], TicketOrigin.Challenge, ct);
        }

        // Same lease value again, so only the version can reject the save.
        await using (var db = await database.Factory.CreateDbContextAsync(ct))
        {
            await db.Tickets.ExecuteUpdateAsync(s => s.SetProperty(t => t.ClaimedAt, (DateTime?)claimed.Claim), ct);
        }

        (await store.SaveAsync(claimed, Suggest(claimed.Ticket), ct)).Should().BeFalse();
        var row = (await TicketsAsync(database)).Single();
        row.StatusId.Should().Be(TicketStatusIds.New);
        row.Version.Should().Be(claimed.Version + 1);
        await using var check = await database.Factory.CreateDbContextAsync(ct);
        (await check.Suggestions.CountAsync(ct)).Should().Be(0);
    }

    // Fix 4

    [Fact]
    public async Task RunOnce_SevenTickets_DrainedInOneCycle_PerAC3()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var database = await SqliteTestDatabase.CreateAsync(ct);
        await IngestAsync(database, 7);
        await MarkReadyAsync(database);
        var cycle = BuildCycle(database, TimeProvider.System, (t, _) => Task.FromResult(Suggest(t)));

        await cycle.RunOnceAsync(ct);

        (await TicketsAsync(database)).Should().OnlyContain(t => t.StatusId == TicketStatusIds.Reviewing);
    }

    [Fact]
    public async Task RunOnce_FailingTicket_AttemptedOncePerCycleOthersStillDrained_PerAC3()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var database = await SqliteTestDatabase.CreateAsync(ct);
        await IngestAsync(database, 7);
        await MarkReadyAsync(database);
        Dictionary<string, int> calls = [];
        var cycle = BuildCycle(database, TimeProvider.System, (t, _) =>
        {
            calls[t.Summary] = calls.GetValueOrDefault(t.Summary) + 1;
            return t.Summary == "Summary 2" ? throw new InvalidOperationException("boom") : Task.FromResult(Suggest(t));
        });

        await cycle.RunOnceAsync(ct);

        calls.Values.Should().OnlyContain(c => c == 1);
        calls.Should().HaveCount(7);
        var rows = await TicketsAsync(database);
        rows.Count(t => t.StatusId == TicketStatusIds.Reviewing).Should().Be(6);
        rows.Single(t => t.StatusId == TicketStatusIds.New).ClaimedAt.Should().BeNull();
    }

    // Fix 5

    [Fact]
    public async Task RunOnce_FreshLeaseFromPreviousProcess_ReleasedAtStartAndProcessed_PerAC3()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var database = await SqliteTestDatabase.CreateAsync(ct);
        await IngestAsync(database, 2);
        await MarkReadyAsync(database);
        var time = new MutableTime(T0);
        await using (var db = await database.Factory.CreateDbContextAsync(ct))
        {
            await db.Tickets.Where(t => t.SourceKey == "#1")
                .ExecuteUpdateAsync(s => s.SetProperty(t => t.ClaimedAt, (DateTime?)T0.UtcDateTime), ct);
        }

        var cycle = BuildCycle(database, time, (t, _) => Task.FromResult(Suggest(t)));
        await cycle.RunOnceAsync(ct);

        (await TicketsAsync(database)).Should().OnlyContain(t => t.StatusId == TicketStatusIds.Reviewing && t.ClaimedAt == null);
    }

    [Fact]
    public async Task ReleaseAll_ClearsEveryNewClaim_PerAC3()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var database = await SqliteTestDatabase.CreateAsync(ct);
        await IngestAsync(database, 2);
        var store = Store(database, new MutableTime(T0));
        (await store.ClaimAsync(5, ct)).Should().HaveCount(2);

        (await store.ReleaseAllAsync(ct)).Should().Be(2);

        (await TicketsAsync(database)).Should().OnlyContain(t => t.ClaimedAt == null);
    }

    // Fix 6

    private sealed class ConflictInterceptor(Func<int, Task> onSaving) : SaveChangesInterceptor
    {
        private int _calls;

        public override async ValueTask<InterceptionResult<int>> SavingChangesAsync(
            DbContextEventData eventData,
            InterceptionResult<int> result,
            CancellationToken cancellationToken = default)
        {
            await onSaving(++_calls);
            return result;
        }
    }

    private static EfTicketIngestor IngestorWith(SqliteTestDatabase database, Func<int, Task> onSaving)
    {
        var options = new DbContextOptionsBuilder<TriageDbContext>()
            .UseSqlite(database.Connection)
            .AddInterceptors(new ConflictInterceptor(onSaving))
            .Options;
        return new EfTicketIngestor(new TriageDbContext(options), TimeProvider.System);
    }

    [Fact]
    public async Task Ingest_ConcurrentInsertOfSameKey_RetriesOnceAndSucceeds_PerAC2()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var database = await SqliteTestDatabase.CreateAsync(ct);
        var ingestor = IngestorWith(database, async call =>
        {
            if (call == 1)
            {
                await IngestAsync(database, 1);
            }
        });

        var results = await ingestor.IngestAsync([Make(1)], TicketOrigin.Challenge, ct);

        results.Should().ContainSingle().Which.Outcome.Should().Be(IngestOutcome.Unchanged);
        (await TicketsAsync(database)).Should().ContainSingle();
    }

    [Fact]
    public async Task Ingest_ConflictPersists_ThrowsClearErrorWithoutTicketText_PerAC2()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var database = await SqliteTestDatabase.CreateAsync(ct);
        var ingestor = IngestorWith(database, _ => throw new DbUpdateException("secret ticket text"));

        var act = () => ingestor.IngestAsync([Make(1)], TicketOrigin.Challenge, ct);

        var thrown = await act.Should().ThrowAsync<InvalidOperationException>();
        thrown.Which.Message.Should().StartWith("Ticket ingest conflict").And.NotContain("secret").And.NotContain("Summary");
    }

    // Fix 9

    [Fact]
    public async Task Ingest_PayloadOverCap_ThrowsArgumentExceptionWithCountsOnly_PerAC2()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var database = await SqliteTestDatabase.CreateAsync(ct);
        await using var db = await database.Factory.CreateDbContextAsync(ct);
        var huge = Make(1) with { Description = new string('x', EfTicketIngestor.MaxPayloadLength) };

        var act = () => new EfTicketIngestor(db, TimeProvider.System).IngestAsync([huge], TicketOrigin.Challenge, ct);

        var thrown = await act.Should().ThrowAsync<ArgumentException>();
        thrown.Which.Message.Should().Contain("too large").And.NotContain("xxxx");
    }

    [Fact]
    public async Task Ingest_TooManyTickets_ThrowsArgumentException_PerAC2()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var database = await SqliteTestDatabase.CreateAsync(ct);
        await using var db = await database.Factory.CreateDbContextAsync(ct);
        var tickets = Enumerable.Range(1, EfTicketIngestor.MaxTicketsPerCall + 1).Select(Make).ToList();

        var act = () => new EfTicketIngestor(db, TimeProvider.System).IngestAsync(tickets, TicketOrigin.Challenge, ct);

        var thrown = await act.Should().ThrowAsync<ArgumentException>();
        thrown.Which.Message.Should().Contain("Too many tickets");
    }

    [Fact]
    [Trait("Category", "Smoke")]
    public async Task Smoke_HardenedCycle_DrainsIngestedTicketsWithHeartbeat()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var database = await SqliteTestDatabase.CreateAsync(ct);
        await IngestAsync(database, 6);
        await MarkReadyAsync(database);
        var cycle = BuildCycle(database, TimeProvider.System, (t, _) => Task.FromResult(Suggest(t)));

        await cycle.RunOnceAsync(ct);

        (await TicketsAsync(database)).Should().OnlyContain(t => t.StatusId == TicketStatusIds.Reviewing);
        (await HeartbeatAsync(database)).Should().BeCloseTo(DateTime.UtcNow, TimeSpan.FromMinutes(1));
    }
}
