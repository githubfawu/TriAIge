using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using TicketTriage.Core.Abstractions;
using TicketTriage.Core.Domain;
using TicketTriage.Infrastructure.Analysis;
using TicketTriage.Infrastructure.Ingestion;
using TicketTriage.Infrastructure.Persistence;
using TicketTriage.Infrastructure.Pipeline;
using TicketTriage.Infrastructure.Routing;

namespace TicketTriage.Infrastructure.Tests;

public sealed class EfAnalysisMonitorTests
{
    private sealed class FakePipeline : ITriagePipeline
    {
        public Task<TriageSuggestion> TriageAsync(Ticket ticket, CancellationToken cancellationToken) =>
            Task.FromResult(new TriageSuggestion
            {
                TicketKey = ticket.Key,
                WorkType = WorkType.ServiceRequest,
                AffectedServices = [],
                ServiceTeams = ["Team X"],
                Assignee = "Jane",
                Urgency = Urgency.High,
                Impact = Impact.Minor,
                ResolutionStatus = ResolutionStatus.Clarification,
                DraftComment = "Draft for " + ticket.Summary,
                SimilarTicketKeys = ["DB-99"],
            });

        public IAsyncEnumerable<TriageSuggestion> TriageAsync(IAsyncEnumerable<Ticket> tickets, CancellationToken cancellationToken) =>
            throw new NotSupportedException();
    }

    private static Ticket Make(int n) => new() { Key = $"#{n}", Summary = $"Summary {n}", Description = $"Description {n}" };

    private static async Task<(SqliteTestDatabase Database, IAnalysisCycle Cycle, IAnalysisMonitor Monitor, IReadOnlyList<int> Ids)> SetupAsync(
        int count = 3)
    {
        var ct = TestContext.Current.CancellationToken;
        var database = await SqliteTestDatabase.CreateAsync(ct);
        IReadOnlyList<IngestResult> results;
        await using (var db = await database.Factory.CreateDbContextAsync(ct))
        {
            results = await new EfTicketIngestor(db, TimeProvider.System)
                .IngestAsync([.. Enumerable.Range(1, count).Select(Make)], TicketOrigin.Challenge, ct);
        }

        await using (var db = await database.Factory.CreateDbContextAsync(ct))
        {
            db.SystemMarkers.Add(new SystemMarkerEntity { Name = SystemMarkerNames.TrainingDataReady, SetAtUtc = DateTime.UtcNow });
            await db.SaveChangesAsync(ct);
        }

        var services = new ServiceCollection();
        services.AddSingleton(database.Factory);
        services.AddSingleton(TimeProvider.System);
        services.AddSingleton(Options.Create(new AnalysisOptions()));
        services.AddLogging();
        services.AddScoped<TicketClaimStore>();
        services.AddScoped<ITriagePipeline, FakePipeline>();
        services.AddSingleton<IAnalysisCycle, AnalysisCycle>();
        var provider = services.BuildServiceProvider();
        return (database, provider.GetRequiredService<IAnalysisCycle>(), new EfAnalysisMonitor(database.Factory), [.. results.Select(r => r.TicketId)]);
    }

    [Fact]
    public async Task GetStates_AfterIngest_AllPendingWithRebuiltTicket_PerAC6()
    {
        var ct = TestContext.Current.CancellationToken;
        var (database, _, monitor, ids) = await SetupAsync();
        await using var _ = database;

        var states = await monitor.GetStatesAsync(ids, ct);

        states.Should().OnlyContain(s => s.IsPending && s.Suggestion == null);
        states.Select(s => s.TicketId).Should().Equal(ids);
        states.Select(s => s.Ticket.Id).Should().Equal(ids.Select(i => (int?)i));
        states.Select(s => s.Ticket.Key).Should().Equal(ids.Select(i => $"DB-{i}"));
        states.Select(s => s.Ticket.Summary).Should().Equal("Summary 1", "Summary 2", "Summary 3");
    }

    [Fact]
    public async Task GetStates_AfterCycle_ReturnsStoredSuggestionsInRequestedOrder_PerAC6()
    {
        var ct = TestContext.Current.CancellationToken;
        var (database, cycle, monitor, ids) = await SetupAsync();
        await using var _ = database;

        await cycle.RunOnceAsync(ct);
        var reversed = ids.Reverse().ToList();
        var states = await monitor.GetStatesAsync(reversed, ct);

        states.Select(s => s.TicketId).Should().Equal(reversed);
        states.Should().OnlyContain(s => !s.IsPending && s.Suggestion != null);
        states.Select(s => s.Suggestion!.DraftComment).Should().Equal("Draft for Summary 3", "Draft for Summary 2", "Draft for Summary 1");
        await using var db = await database.Factory.CreateDbContextAsync(ct);
        var stored = await db.Suggestions.AsNoTracking().ToListAsync(ct);
        foreach (var state in states)
        {
            var entity = stored.Single(s => s.TicketId == state.TicketId);
            state.Suggestion.Should().BeEquivalentTo(SuggestionMapper.ToDomain(entity));
            state.Suggestion!.Priority.Should().Be(PriorityMatrix.Resolve(Urgency.High, Impact.Minor));
            state.Suggestion.ResolutionStatus.Should().Be(ResolutionStatus.Clarification);
        }
    }

    [Fact]
    public async Task GetStates_DuplicateIds_ReturnedPerPosition_PerAC6()
    {
        var ct = TestContext.Current.CancellationToken;
        var (database, _, monitor, ids) = await SetupAsync(1);
        await using var _ = database;

        var states = await monitor.GetStatesAsync([ids[0], ids[0]], ct);

        states.Should().HaveCount(2);
    }

    [Fact]
    public async Task GetStates_UnknownId_Throws_PerAC6()
    {
        var ct = TestContext.Current.CancellationToken;
        var (database, _, monitor, _) = await SetupAsync(1);
        await using var _ = database;

        var act = () => monitor.GetStatesAsync([12345], ct);

        await act.Should().ThrowAsync<InvalidOperationException>();
    }

    [Fact]
    public async Task GetWorkerHeartbeat_NoWorker_IsNullThenSetAfterCycle_PerAC6()
    {
        var ct = TestContext.Current.CancellationToken;
        var (database, cycle, monitor, _) = await SetupAsync(1);
        await using var _ = database;

        (await monitor.GetWorkerHeartbeatAsync(ct)).Should().BeNull();
        await cycle.RunOnceAsync(ct);

        var beat = await monitor.GetWorkerHeartbeatAsync(ct);
        beat.Should().NotBeNull();
        beat!.Value.Kind.Should().Be(DateTimeKind.Utc);
        beat.Value.Should().BeCloseTo(DateTime.UtcNow, TimeSpan.FromMinutes(1));
    }

    [Fact]
    public async Task DeterministicFallbackProvider_NoSimilarTickets_ReturnsFallbackSuggestion_PerAC6()
    {
        var ct = TestContext.Current.CancellationToken;
        var provider = new DeterministicFallbackProvider(
            new TicketNormalizer(NullLogger<TicketNormalizer>.Instance),
            new EmptySimilarSource(),
            new EmptyStatisticsSource(),
            new FakeWorkload(),
            Options.Create(new TriageOptions()),
            NullLogger<DeterministicFallbackProvider>.Instance);

        var suggestion = await provider.CreateAsync(Make(1) with { Id = 7, Key = "DB-7" }, ct);

        suggestion.IsFallback.Should().BeTrue();
        suggestion.TicketKey.Should().Be("DB-7");
        suggestion.Priority.Should().Be(PriorityMatrix.Resolve(Urgency.Medium, Impact.Moderate));
    }

    private sealed class EmptySimilarSource : ISimilarTicketSource
    {
        public Task<IReadOnlyList<SimilarTicket>> FindSimilarAsync(Ticket ticket, int top, CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<SimilarTicket>>([]);
    }

    private sealed class EmptyStatisticsSource : IRoutingStatisticsSource
    {
        public ValueTask<RoutingStatistics> GetAsync(CancellationToken cancellationToken) => ValueTask.FromResult(RoutingStatistics.Empty);
    }
}
