using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using TicketTriage.Core.Abstractions;
using TicketTriage.Core.Domain;
using TicketTriage.Infrastructure.Analysis;
using TicketTriage.Infrastructure.Ingestion;
using TicketTriage.Infrastructure.Persistence;

namespace TicketTriage.Infrastructure.Tests;

public sealed class AnalysisCycleTests
{
    private sealed class FakePipeline(Func<Ticket, CancellationToken, Task<TriageSuggestion>> handler) : ITriagePipeline
    {
        public Task<TriageSuggestion> TriageAsync(Ticket ticket, CancellationToken cancellationToken) =>
            handler(ticket, cancellationToken);

        public IAsyncEnumerable<TriageSuggestion> TriageAsync(IAsyncEnumerable<Ticket> tickets, CancellationToken cancellationToken) =>
            throw new NotSupportedException();
    }

    private static TriageSuggestion Suggest(Ticket ticket, string? comment = "ok") => new()
    {
        TicketKey = ticket.Key ?? "?",
        WorkType = WorkType.Incident,
        Urgency = Urgency.Medium,
        Impact = Impact.Moderate,
        DraftComment = comment,
    };

    private static Ticket Make(int n) => new()
    {
        Key = $"#{n}",
        Summary = $"Summary {n}",
        Description = $"Description {n}",
    };

    private static async Task<(IAnalysisCycle Cycle, SqliteTestDatabase Database)> SetupAsync(
        Func<Ticket, CancellationToken, Task<TriageSuggestion>> handler,
        bool dataReady = true,
        int tickets = 3,
        TicketOrigin origin = TicketOrigin.Challenge)
    {
        var ct = TestContext.Current.CancellationToken;
        var database = await SqliteTestDatabase.CreateAsync(ct);

        await using (var db = await database.Factory.CreateDbContextAsync(ct))
        {
            await new EfTicketIngestor(db, TimeProvider.System)
                .IngestAsync([.. Enumerable.Range(1, tickets).Select(Make)], origin, ct);
        }

        if (dataReady)
        {
            await using var db = await database.Factory.CreateDbContextAsync(ct);
            db.SystemMarkers.Add(new SystemMarkerEntity { Name = SystemMarkerNames.TrainingDataReady, SetAtUtc = DateTime.UtcNow });
            await db.SaveChangesAsync(ct);
        }

        var services = new ServiceCollection();
        services.AddSingleton(database.Factory);
        services.AddSingleton(TimeProvider.System);
        services.AddSingleton(Options.Create(new AnalysisOptions()));
        services.AddLogging();
        services.AddScoped<TicketClaimStore>();
        services.AddScoped<ITriagePipeline>(_ => new FakePipeline(handler));
        services.AddSingleton<IAnalysisCycle, AnalysisCycle>();
        var provider = services.BuildServiceProvider();
        return (provider.GetRequiredService<IAnalysisCycle>(), database);
    }

    private static async Task<List<TicketEntity>> TicketsAsync(SqliteTestDatabase database)
    {
        await using var db = await database.Factory.CreateDbContextAsync(TestContext.Current.CancellationToken);
        return await db.Tickets.AsNoTracking().OrderBy(t => t.Id).ToListAsync(TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task RunOnce_ThreeTickets_AllReviewingWithSuggestionsAndVersionBump_PerAC3()
    {
        var ct = TestContext.Current.CancellationToken;
        var (cycle, database) = await SetupAsync((t, _) => Task.FromResult(Suggest(t)));
        await using var _ = database;

        await cycle.RunOnceAsync(ct);

        var rows = await TicketsAsync(database);
        rows.Should().OnlyContain(t => t.StatusId == TicketStatusIds.Reviewing && t.ClaimedAt == null && t.Version == 1);
        await using var db = await database.Factory.CreateDbContextAsync(ct);
        (await db.Suggestions.CountAsync(ct)).Should().Be(3);
    }

    [Fact]
    public async Task RunOnce_PipelineThrowsForOneTicket_OthersSavedFailedOneReleased_PerAC3()
    {
        var ct = TestContext.Current.CancellationToken;
        var (cycle, database) = await SetupAsync((t, _) =>
            t.Summary == "Summary 2" ? throw new InvalidOperationException("boom secret text") : Task.FromResult(Suggest(t)));
        await using var _ = database;

        await cycle.RunOnceAsync(ct);

        var rows = await TicketsAsync(database);
        rows[0].StatusId.Should().Be(TicketStatusIds.Reviewing);
        rows[2].StatusId.Should().Be(TicketStatusIds.Reviewing);
        rows[1].StatusId.Should().Be(TicketStatusIds.New);
        rows[1].ClaimedAt.Should().BeNull();
        rows[1].Version.Should().Be(0);
    }

    [Fact]
    public async Task RunOnce_PipelineStopSystemCancellationWhileHostRuns_TreatedAsFailedTicket_PerAC3()
    {
        var ct = TestContext.Current.CancellationToken;
        var (cycle, database) = await SetupAsync((t, _) =>
            t.Summary == "Summary 1" ? throw new OperationCanceledException() : Task.FromResult(Suggest(t)));
        await using var _ = database;

        await cycle.RunOnceAsync(ct);

        var rows = await TicketsAsync(database);
        rows[0].StatusId.Should().Be(TicketStatusIds.New);
        rows[0].ClaimedAt.Should().BeNull();
        rows.Skip(1).Should().OnlyContain(t => t.StatusId == TicketStatusIds.Reviewing);
    }

    [Fact]
    public async Task RunOnce_DataNotReady_ClaimsNothingButBeats_PerAC3()
    {
        var ct = TestContext.Current.CancellationToken;
        var called = 0;
        var (cycle, database) = await SetupAsync(
            (t, _) =>
            {
                called++;
                return Task.FromResult(Suggest(t));
            },
            dataReady: false);
        await using var _ = database;

        await cycle.RunOnceAsync(ct);

        called.Should().Be(0);
        (await TicketsAsync(database)).Should().OnlyContain(t => t.StatusId == TicketStatusIds.New && t.ClaimedAt == null);
        await using var db = await database.Factory.CreateDbContextAsync(ct);
        (await db.SystemMarkers.AnyAsync(m => m.Name == SystemMarkerNames.AnalysisWorkerHeartbeat, ct)).Should().BeTrue();
    }

    [Fact]
    public async Task RunOnce_FallbackSuggestion_StoredAsFallback_PerAC3()
    {
        var ct = TestContext.Current.CancellationToken;
        var (cycle, database) = await SetupAsync((t, _) => Task.FromResult(Suggest(t, comment: "")), tickets: 1);
        await using var _ = database;

        await cycle.RunOnceAsync(ct);

        await using var db = await database.Factory.CreateDbContextAsync(ct);
        (await db.Suggestions.SingleAsync(ct)).IsFallback.Should().BeTrue();
    }

    [Fact]
    public async Task RunOnce_SecondCycle_DoesNothing_PerAC3()
    {
        var ct = TestContext.Current.CancellationToken;
        var called = 0;
        var (cycle, database) = await SetupAsync((t, _) =>
        {
            called++;
            return Task.FromResult(Suggest(t));
        });
        await using var _ = database;

        await cycle.RunOnceAsync(ct);
        await cycle.RunOnceAsync(ct);

        called.Should().Be(3);
        (await TicketsAsync(database)).Should().OnlyContain(t => t.Version == 1);
    }

    [Fact]
    public async Task RunOnce_HostCancelledMidBatch_ReleasesRemainingClaimsAndRethrows_PerAC3()
    {
        var ct = TestContext.Current.CancellationToken;
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var (cycle, database) = await SetupAsync(async (t, token) =>
        {
            if (t.Summary == "Summary 2")
            {
                await cts.CancelAsync();
                token.ThrowIfCancellationRequested();
            }

            return Suggest(t);
        });
        await using var _ = database;

        var act = () => cycle.RunOnceAsync(cts.Token);

        await act.Should().ThrowAsync<OperationCanceledException>();
        var rows = await TicketsAsync(database);
        rows[0].StatusId.Should().Be(TicketStatusIds.Reviewing);
        rows[1].StatusId.Should().Be(TicketStatusIds.New);
        rows[2].StatusId.Should().Be(TicketStatusIds.New);
        rows.Should().OnlyContain(t => t.ClaimedAt == null);
    }

    [Fact]
    public async Task RunOnce_PipelineReceivesTicketFromPayloadWithDbKey_PerAC3()
    {
        var ct = TestContext.Current.CancellationToken;
        List<Ticket> seen = [];
        var (cycle, database) = await SetupAsync((t, _) =>
        {
            seen.Add(t);
            return Task.FromResult(Suggest(t));
        });
        await using var _ = database;

        await cycle.RunOnceAsync(ct);

        var rows = await TicketsAsync(database);
        seen.Select(t => t.Key).Should().Equal(rows.Select(r => $"DB-{r.Id}"));
        seen.Select(t => t.Id).Should().Equal(rows.Select(r => (int?)r.Id));
        seen.Select(t => t.Description).Should().Equal("Description 1", "Description 2", "Description 3");
    }

    [Fact]
    public async Task RunOnce_TrainingOriginTickets_NeverClaimed_PerAC3()
    {
        var ct = TestContext.Current.CancellationToken;
        var called = 0;
        var (cycle, database) = await SetupAsync(
            (t, _) =>
            {
                called++;
                return Task.FromResult(Suggest(t));
            },
            tickets: 0);
        await using var _ = database;
        await database.AddTicketAsync(100, "old", statusId: TicketStatusIds.New, origin: TicketOrigin.Training, cancellationToken: ct);

        await cycle.RunOnceAsync(ct);

        called.Should().Be(0);
        (await TicketsAsync(database)).Single().Should().Match<TicketEntity>(t => t.StatusId == TicketStatusIds.New && t.ClaimedAt == null);
    }

    [Fact]
    [Trait("Category", "Smoke")]
    public async Task Smoke_CycleDrainsQueue()
    {
        var ct = TestContext.Current.CancellationToken;
        var (cycle, database) = await SetupAsync((t, _) => Task.FromResult(Suggest(t)), tickets: 7);
        await using var _ = database;

        await cycle.RunOnceAsync(ct);
        await cycle.RunOnceAsync(ct);

        (await TicketsAsync(database)).Should().OnlyContain(t => t.StatusId == TicketStatusIds.Reviewing);
    }
}
