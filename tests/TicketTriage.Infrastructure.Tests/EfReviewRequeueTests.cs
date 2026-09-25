using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using TicketTriage.Core.Abstractions;
using TicketTriage.Core.Domain;
using TicketTriage.Infrastructure.Analysis;
using TicketTriage.Infrastructure.Ingestion;
using TicketTriage.Infrastructure.Persistence;
using TicketTriage.Infrastructure.Pipeline;
using TicketTriage.Infrastructure.Review;

namespace TicketTriage.Infrastructure.Tests;

public sealed class EfReviewRequeueTests
{
    private const int RetryCount = 3;

    private sealed class FakePipeline : ITriagePipeline
    {
        public int Calls { get; private set; }

        public Task<TriageSuggestion> TriageAsync(Ticket ticket, CancellationToken cancellationToken)
        {
            Calls++;
            return Task.FromResult(new TriageSuggestion
            {
                TicketKey = ticket.Key ?? "?",
                WorkType = WorkType.Incident,
                Urgency = Urgency.Medium,
                Impact = Impact.Moderate,
                DraftComment = "ok",
            });
        }

        public IAsyncEnumerable<TriageSuggestion> TriageAsync(IAsyncEnumerable<Ticket> tickets, CancellationToken cancellationToken) =>
            throw new NotSupportedException();
    }

    private static EfReviewService Service(SqliteTestDatabase database) =>
        new(database.Factory, Options.Create(new TriageOptions { RetryCount = RetryCount }), TimeProvider.System);

    private static IAnalysisCycle Cycle(SqliteTestDatabase database, FakePipeline pipeline)
    {
        var services = new ServiceCollection();
        services.AddSingleton(database.Factory);
        services.AddSingleton(TimeProvider.System);
        services.AddSingleton(Options.Create(new AnalysisOptions()));
        services.AddLogging();
        services.AddScoped<TicketClaimStore>();
        services.AddScoped<ITriagePipeline>(_ => pipeline);
        services.AddSingleton<IAnalysisCycle, AnalysisCycle>();
        return services.BuildServiceProvider().GetRequiredService<IAnalysisCycle>();
    }

    // Ingest one ticket, let the cycle analyse it (Reviewing + suggestion), then mark it failed and add an edit.
    private static async Task<SqliteTestDatabase> SeedFailedAsync(
        FakePipeline pipeline, TicketOrigin origin, int retries = RetryCount, int status = TicketStatusIds.Reviewing)
    {
        var ct = TestContext.Current.CancellationToken;
        var database = await SqliteTestDatabase.CreateAsync(ct);
        await using (var db = await database.Factory.CreateDbContextAsync(ct))
        {
            await new EfTicketIngestor(db, TimeProvider.System)
                .IngestAsync([new Ticket { Key = "#1", Summary = "S", Description = "D" }], origin, ct);
            db.SystemMarkers.Add(new SystemMarkerEntity { Name = SystemMarkerNames.TrainingDataReady, SetAtUtc = DateTime.UtcNow });
            await db.SaveChangesAsync(ct);
        }

        await Cycle(database, pipeline).RunOnceAsync(ct);

        await using (var db = await database.Factory.CreateDbContextAsync(ct))
        {
            db.SuggestionEdits.Add(new SuggestionEditEntity
            {
                TicketId = 1,
                Field = nameof(SuggestionField.Assignee),
                AiValue = "a",
                FinalValue = "b",
                EditedAtUtc = DateTime.UtcNow,
            });
            var ticket = await db.Tickets.SingleAsync(ct);
            ticket.Retries = retries;
            ticket.StatusId = status;
            await db.SaveChangesAsync(ct);
        }

        return database;
    }

    private static async Task<(TicketEntity Ticket, int Suggestions, int Edits)> LoadAsync(SqliteTestDatabase database)
    {
        var ct = TestContext.Current.CancellationToken;
        await using var db = await database.Factory.CreateDbContextAsync(ct);
        return (await db.Tickets.AsNoTracking().SingleAsync(ct), await db.Suggestions.CountAsync(ct), await db.SuggestionEdits.CountAsync(ct));
    }

    [Fact]
    public async Task Requeue_FailedReviewingTicket_ResetsToNew_DeletesSuggestionAndEdits_AndIsClaimedAgain()
    {
        var ct = TestContext.Current.CancellationToken;
        var pipeline = new FakePipeline();
        await using var database = await SeedFailedAsync(pipeline, TicketOrigin.Challenge);
        var before = (await LoadAsync(database)).Ticket;

        var result = await Service(database).RequeueFailedAsync(1, before.Version, ct);

        result.Outcome.Should().Be(ReviewOutcome.Success);
        result.Version.Should().Be(before.Version + 1);
        var (ticket, suggestions, edits) = await LoadAsync(database);
        ticket.StatusId.Should().Be(TicketStatusIds.New);
        ticket.Retries.Should().Be(0);
        ticket.ClaimedAt.Should().BeNull();
        ticket.Version.Should().Be(before.Version + 1);
        suggestions.Should().Be(0);
        edits.Should().Be(0);

        await Cycle(database, pipeline).RunOnceAsync(ct);

        pipeline.Calls.Should().Be(2);
        var (again, againSuggestions, _) = await LoadAsync(database);
        again.StatusId.Should().Be(TicketStatusIds.Reviewing);
        againSuggestions.Should().Be(1);
    }

    [Fact]
    public async Task Requeue_TicketNotFailed_ReturnsInvalidState_AndChangesNothing()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var database = await SeedFailedAsync(new FakePipeline(), TicketOrigin.Challenge, retries: RetryCount - 1);
        var before = (await LoadAsync(database)).Ticket;

        var result = await Service(database).RequeueFailedAsync(1, before.Version, ct);

        result.Outcome.Should().Be(ReviewOutcome.InvalidState);
        await AssertUnchangedAsync(database, before);
    }

    [Fact]
    public async Task Requeue_DecidedTicket_ReturnsInvalidState_AndChangesNothing()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var database = await SeedFailedAsync(new FakePipeline(), TicketOrigin.Challenge, status: TicketStatusIds.HumanApproved);
        var before = (await LoadAsync(database)).Ticket;

        var result = await Service(database).RequeueFailedAsync(1, before.Version, ct);

        result.Outcome.Should().Be(ReviewOutcome.InvalidState);
        await AssertUnchangedAsync(database, before);
    }

    [Fact]
    public async Task Requeue_TrainingTicket_ReturnsNotFound()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var database = await SeedFailedAsync(new FakePipeline(), TicketOrigin.Challenge);
        await using (var db = await database.Factory.CreateDbContextAsync(ct))
        {
            await db.Tickets.ExecuteUpdateAsync(s => s.SetProperty(t => t.Origin, TicketOrigin.Training), ct);
        }

        var before = (await LoadAsync(database)).Ticket;

        var result = await Service(database).RequeueFailedAsync(1, before.Version, ct);

        result.Outcome.Should().Be(ReviewOutcome.NotFound);
        (await Service(database).RequeueFailedAsync(999, 0, ct)).Outcome.Should().Be(ReviewOutcome.NotFound);
    }

    [Fact]
    public async Task Requeue_StaleVersion_ReturnsConflict_AndChangesNothing()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var database = await SeedFailedAsync(new FakePipeline(), TicketOrigin.Challenge);
        var before = (await LoadAsync(database)).Ticket;

        var result = await Service(database).RequeueFailedAsync(1, before.Version + 5, ct);

        result.Outcome.Should().Be(ReviewOutcome.Conflict);
        await AssertUnchangedAsync(database, before);
    }

    private static async Task AssertUnchangedAsync(SqliteTestDatabase database, TicketEntity before)
    {
        var (ticket, suggestions, edits) = await LoadAsync(database);
        ticket.StatusId.Should().Be(before.StatusId);
        ticket.Retries.Should().Be(before.Retries);
        ticket.Version.Should().Be(before.Version);
        suggestions.Should().Be(1);
        edits.Should().Be(1);
    }
}
