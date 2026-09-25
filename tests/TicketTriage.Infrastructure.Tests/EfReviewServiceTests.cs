
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
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

public sealed class EfReviewServiceTests
{
    private static readonly DateTimeOffset T0 = new(2026, 9, 25, 12, 0, 0, TimeSpan.Zero);

    private sealed class FixedTime(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }

    private static EfReviewService Service(SqliteTestDatabase database, int retryCount = 3) =>
        new(database.Factory, Options.Create(new TriageOptions { RetryCount = retryCount }), new FixedTime(T0));

    private static async Task SeedAsync(
        SqliteTestDatabase database,
        int id,
        int statusId = TicketStatusIds.Reviewing,
        TicketOrigin origin = TicketOrigin.Challenge,
        bool withSuggestion = true,
        long version = 1,
        int retries = 0)
    {
        await using var db = await database.Factory.CreateDbContextAsync(TestContext.Current.CancellationToken);
        db.Tickets.Add(new TicketEntity
        {
            Id = id,
            Summary = $"Summary {id}",
            Description = "desc",
            Origin = origin,
            SourceKey = origin == TicketOrigin.Training ? null : $"#{id}",
            SourcePayload = $$"""{"Summary":"Summary {{id}}","Description":"desc"}""",
            IngestedAt = T0.UtcDateTime,
            StatusId = statusId,
            Version = version,
            Retries = retries,
            CreatedDate = T0.UtcDateTime,
        });
        if (withSuggestion)
        {
            db.Suggestions.Add(new TriageSuggestionEntity
            {
                TicketId = id,
                WorkType = WorkType.Incident,
                AffectedServices = ["Trading Platform"],
                ServiceTeams = ["Team A"],
                Assignee = "alice",
                Urgency = Urgency.Medium,
                Impact = Impact.Moderate,
                Priority = PriorityMatrix.Resolve(Urgency.Medium, Impact.Moderate),
                ResolutionStatus = ResolutionStatus.Done,
                DraftComment = "AI comment",
                CreatedAtUtc = T0.UtcDateTime,
            });
        }

        await db.SaveChangesAsync(TestContext.Current.CancellationToken);
    }

    private static async Task<(TicketEntity Ticket, TriageSuggestionEntity? Suggestion, List<SuggestionEditEntity> Edits)> LoadAsync(
        SqliteTestDatabase database, int id)
    {
        await using var db = await database.Factory.CreateDbContextAsync(TestContext.Current.CancellationToken);
        var ct = TestContext.Current.CancellationToken;
        return (
            await db.Tickets.AsNoTracking().SingleAsync(t => t.Id == id, ct),
            await db.Suggestions.AsNoTracking().SingleOrDefaultAsync(s => s.TicketId == id, ct),
            await db.SuggestionEdits.AsNoTracking().Where(e => e.TicketId == id).ToListAsync(ct));
    }

    [Fact]
    public async Task Open_SetsFirstOpenedOnce_LeavesVersionUnchanged_PerAC4()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var database = await SqliteTestDatabase.CreateAsync(ct);
        await SeedAsync(database, 1, version: 7);

        var first = await Service(database).OpenAsync(1, ct);
        var laterService = new EfReviewService(database.Factory, Options.Create(new TriageOptions()), new FixedTime(T0.AddHours(1)));
        var second = await laterService.OpenAsync(1, ct);

        first!.Version.Should().Be(7);
        first.Suggestion.Should().NotBeNull();
        first.Decision!.FirstOpenedAtUtc.Should().Be(T0.UtcDateTime);
        second!.Decision!.FirstOpenedAtUtc.Should().Be(T0.UtcDateTime);
        var (ticket, suggestion, _) = await LoadAsync(database, 1);
        ticket.Version.Should().Be(7);
        suggestion!.FirstOpenedAtUtc.Should().Be(T0.UtcDateTime);
    }

    [Fact]
    public async Task Open_TicketWithoutSuggestion_IsAnalysing_AndDoesNotTouchAnything_PerAC4()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var database = await SqliteTestDatabase.CreateAsync(ct);
        await SeedAsync(database, 1, TicketStatusIds.New, withSuggestion: false, version: 0, retries: 3);

        var view = await Service(database).OpenAsync(1, ct);

        view!.IsAnalysing.Should().BeTrue();
        view.IsFailed.Should().BeTrue();
        view.Suggestion.Should().BeNull();
        view.Decision.Should().BeNull();
        view.StatusName.Should().Be("New");
    }

    [Fact]
    public async Task Open_HasNoLlmDependencies_PerAC4()
    {
        var ctorTypes = typeof(EfReviewService).GetConstructors()
            .SelectMany(c => c.GetParameters())
            .Select(p => p.ParameterType)
            .ToList();
        ctorTypes.Should().NotContain(t => t == typeof(ITriagePipeline) || t == typeof(ITicketClassifier) || t == typeof(IResolutionDrafter));
        ctorTypes.Should().NotContain(t => t.Name == "IChatClient");

        var ct = TestContext.Current.CancellationToken;
        await using var database = await SqliteTestDatabase.CreateAsync(ct);
        await SeedAsync(database, 1);
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["ConnectionStrings:triage-db"] = "Data Source=:memory:" })
            .Build();
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddTriageInfrastructure(config);
        services.AddSingleton(database.Factory);
        // The LLM-facing services throw when resolved; the review path must never ask for them.
        services.AddScoped<ITicketClassifier>(_ => throw new InvalidOperationException("LLM classifier resolved"));
        services.AddScoped<IResolutionDrafter>(_ => throw new InvalidOperationException("LLM drafter resolved"));
        services.AddScoped<ITriagePipeline>(_ => throw new InvalidOperationException("pipeline resolved"));
        await using var provider = services.BuildServiceProvider();
        await using var scope = provider.CreateAsyncScope();

        var view = await scope.ServiceProvider.GetRequiredService<IReviewService>().OpenAsync(1, ct);

        view.Should().NotBeNull();
    }

    [Fact]
    public async Task Approve_WithoutEdits_SetsDecision_NoEditRows_BumpsVersion_PerAC5()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var database = await SqliteTestDatabase.CreateAsync(ct);
        await SeedAsync(database, 1, version: 4);

        var result = await Service(database).ApproveAsync(1, 4, null, ct);

        result.Outcome.Should().Be(ReviewOutcome.Success);
        result.Version.Should().Be(5);
        var (ticket, suggestion, edits) = await LoadAsync(database, 1);
        ticket.StatusId.Should().Be(TicketStatusIds.HumanApproved);
        ticket.Version.Should().Be(5);
        suggestion!.Decision.Should().Be(ReviewDecision.Approved);
        suggestion.DecidedAtUtc.Should().Be(T0.UtcDateTime);
        edits.Should().BeEmpty();
    }

    [Fact]
    public async Task Approve_WithUrgencyAndServiceEdits_StoresTwoRows_PriorityFollowsMatrix_PerAC5()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var database = await SqliteTestDatabase.CreateAsync(ct);
        await SeedAsync(database, 1);
        var service = Service(database);

        var result = await service.ApproveAsync(
            1, 1, new ReviewEdits { Urgency = Urgency.Critical, AffectedServices = ["order management"] }, ct);

        result.Outcome.Should().Be(ReviewOutcome.Success);
        var (_, suggestion, edits) = await LoadAsync(database, 1);
        edits.Should().HaveCount(2);
        edits.Single(e => e.Field == nameof(SuggestionField.Urgency)).Should().Match<SuggestionEditEntity>(
            e => e.AiValue == "Medium" && e.FinalValue == "Critical");
        edits.Single(e => e.Field == nameof(SuggestionField.AffectedServices)).FinalValue.Should().Be("[\"Order Management\"]");
        suggestion!.Urgency.Should().Be(Urgency.Medium, "the AI values stay stored");

        var view = await service.OpenAsync(1, ct);
        view!.EffectiveSuggestion!.Urgency.Should().Be(Urgency.Critical);
        view.EffectiveSuggestion.AffectedServices.Should().Equal("Order Management");
        view.EffectiveSuggestion.Priority.Should().Be(PriorityMatrix.Resolve(Urgency.Critical, Impact.Moderate));
        view.Suggestion!.Priority.Should().Be(PriorityMatrix.Resolve(Urgency.Medium, Impact.Moderate));
    }

    [Fact]
    public async Task SaveEdits_RevertToAiValue_RemovesRow_LastEditWins_PerAC5()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var database = await SqliteTestDatabase.CreateAsync(ct);
        await SeedAsync(database, 1);
        var service = Service(database);

        var first = await service.SaveEditsAsync(1, 1, new ReviewEdits { Urgency = Urgency.Low, DraftComment = "mine" }, ct);
        var second = await service.SaveEditsAsync(1, first.Version!.Value, new ReviewEdits { Urgency = Urgency.High }, ct);
        (await LoadAsync(database, 1)).Edits.Single(e => e.Field == "Urgency").FinalValue.Should().Be("High");

        var third = await service.SaveEditsAsync(1, second.Version!.Value, new ReviewEdits { Urgency = Urgency.Medium }, ct);

        third.Outcome.Should().Be(ReviewOutcome.Success);
        var (_, _, edits) = await LoadAsync(database, 1);
        edits.Select(e => e.Field).Should().Equal(nameof(SuggestionField.DraftComment));
    }

    [Fact]
    public async Task SaveEdits_MovesToReviewed_ThenApproveWorks_PerAC5()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var database = await SqliteTestDatabase.CreateAsync(ct);
        await SeedAsync(database, 1);
        var service = Service(database);

        var saved = await service.SaveEditsAsync(1, 1, new ReviewEdits { Assignee = "bob" }, ct);
        (await LoadAsync(database, 1)).Ticket.StatusId.Should().Be(TicketStatusIds.Reviewed);
        var approved = await service.ApproveAsync(1, saved.Version!.Value, null, ct);

        approved.Outcome.Should().Be(ReviewOutcome.Success);
        approved.Version.Should().Be(3);
        (await LoadAsync(database, 1)).Ticket.StatusId.Should().Be(TicketStatusIds.HumanApproved);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public async Task Reject_WithoutReason_IsInvalid_NothingChanged_PerAC5(string? reason)
    {
        var ct = TestContext.Current.CancellationToken;
        await using var database = await SqliteTestDatabase.CreateAsync(ct);
        await SeedAsync(database, 1);

        var result = await Service(database).RejectAsync(1, 1, reason!, ct);

        result.Outcome.Should().Be(ReviewOutcome.Invalid);
        var (ticket, suggestion, _) = await LoadAsync(database, 1);
        ticket.StatusId.Should().Be(TicketStatusIds.Reviewing);
        ticket.Version.Should().Be(1);
        suggestion!.Decision.Should().Be(ReviewDecision.Pending);
    }

    [Fact]
    public async Task Reject_TooLongReason_IsInvalid_PerAC5()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var database = await SqliteTestDatabase.CreateAsync(ct);
        await SeedAsync(database, 1);

        var result = await Service(database).RejectAsync(1, 1, new string('x', 501), ct);

        result.Outcome.Should().Be(ReviewOutcome.Invalid);
    }

    [Fact]
    public async Task Reject_StoresReasonAndTimestamp_PerAC5()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var database = await SqliteTestDatabase.CreateAsync(ct);
        await SeedAsync(database, 1);

        var result = await Service(database).RejectAsync(1, 1, "  wrong service  ", ct);

        result.Outcome.Should().Be(ReviewOutcome.Success);
        var (ticket, suggestion, _) = await LoadAsync(database, 1);
        ticket.StatusId.Should().Be(TicketStatusIds.HumanRejected);
        ticket.Version.Should().Be(2);
        suggestion!.Decision.Should().Be(ReviewDecision.Rejected);
        suggestion.RejectReason.Should().Be("wrong service");
        suggestion.DecidedAtUtc.Should().Be(T0.UtcDateTime);
    }

    [Fact]
    public async Task Approve_StaleVersion_IsConflict_NothingChanged_PerAC5()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var database = await SqliteTestDatabase.CreateAsync(ct);
        await SeedAsync(database, 1, version: 3);

        var result = await Service(database).ApproveAsync(1, 2, new ReviewEdits { Urgency = Urgency.Low }, ct);

        result.Outcome.Should().Be(ReviewOutcome.Conflict);
        var (ticket, suggestion, edits) = await LoadAsync(database, 1);
        ticket.StatusId.Should().Be(TicketStatusIds.Reviewing);
        ticket.Version.Should().Be(3);
        suggestion!.Decision.Should().Be(ReviewDecision.Pending);
        edits.Should().BeEmpty();
    }

    [Fact]
    public async Task Approve_VersionChangedByOtherWriterAfterRead_IsConflict_PerAC5()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var database = await SqliteTestDatabase.CreateAsync(ct);
        await SeedAsync(database, 1, version: 1);
        var racing = new RacingFactory(database.Factory);
        var service = new EfReviewService(racing, Options.Create(new TriageOptions()), new FixedTime(T0));

        var result = await service.ApproveAsync(1, 1, null, ct);

        result.Outcome.Should().Be(ReviewOutcome.Conflict);
        (await LoadAsync(database, 1)).Ticket.StatusId.Should().Be(TicketStatusIds.Reviewing);
    }

    // Bumps the version through a second context after the service loaded the ticket, before it saves.
    private sealed class RacingFactory(IDbContextFactory<TriageDbContext> inner) : IDbContextFactory<TriageDbContext>
    {
        public TriageDbContext CreateDbContext() => throw new NotSupportedException();

        public async Task<TriageDbContext> CreateDbContextAsync(CancellationToken cancellationToken = default)
        {
            var context = await inner.CreateDbContextAsync(cancellationToken);
            context.SavingChanges += (_, _) =>
            {
                using var other = inner.CreateDbContext();
                other.Database.ExecuteSqlRaw("UPDATE Ticket SET Version = Version + 1");
            };
            return context;
        }
    }

    [Theory]
    [InlineData(TicketStatusIds.New)]
    [InlineData(TicketStatusIds.HumanApproved)]
    [InlineData(TicketStatusIds.HumanRejected)]
    public async Task Decide_NewOrDecidedTicket_IsInvalidState_PerAC5(int statusId)
    {
        var ct = TestContext.Current.CancellationToken;
        await using var database = await SqliteTestDatabase.CreateAsync(ct);
        await SeedAsync(database, 1, statusId, withSuggestion: statusId != TicketStatusIds.New);
        var service = Service(database);

        (await service.ApproveAsync(1, 1, null, ct)).Outcome.Should().Be(ReviewOutcome.InvalidState);
        (await service.RejectAsync(1, 1, "no", ct)).Outcome.Should().Be(ReviewOutcome.InvalidState);
        (await service.SaveEditsAsync(1, 1, new ReviewEdits { Urgency = Urgency.Low }, ct)).Outcome.Should().Be(ReviewOutcome.InvalidState);
        (await LoadAsync(database, 1)).Ticket.StatusId.Should().Be(statusId);
    }

    [Fact]
    public async Task Approve_UnknownService_IsInvalid_NothingChanged_PerAC5()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var database = await SqliteTestDatabase.CreateAsync(ct);
        await SeedAsync(database, 1);

        var result = await Service(database).ApproveAsync(
            1, 1, new ReviewEdits { Urgency = Urgency.Low, AffectedServices = ["Warp Drive"] }, ct);

        result.Outcome.Should().Be(ReviewOutcome.Invalid);
        var (ticket, _, edits) = await LoadAsync(database, 1);
        ticket.StatusId.Should().Be(TicketStatusIds.Reviewing);
        edits.Should().BeEmpty();
    }

    [Fact]
    public async Task SaveEdits_EmptyOrOversizedComment_IsInvalid_PerAC5()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var database = await SqliteTestDatabase.CreateAsync(ct);
        await SeedAsync(database, 1);
        var service = Service(database);

        (await service.SaveEditsAsync(1, 1, new ReviewEdits { DraftComment = "  " }, ct)).Outcome.Should().Be(ReviewOutcome.Invalid);
        (await service.SaveEditsAsync(1, 1, new ReviewEdits { DraftComment = new string('x', 2001) }, ct)).Outcome.Should().Be(ReviewOutcome.Invalid);
        (await service.SaveEditsAsync(1, 1, new ReviewEdits { Urgency = (Urgency)99 }, ct)).Outcome.Should().Be(ReviewOutcome.Invalid);
        (await service.SaveEditsAsync(1, 1, new ReviewEdits(), ct)).Outcome.Should().Be(ReviewOutcome.Invalid);
    }

    [Fact]
    public async Task UnknownIdAndTrainingTicket_AreNotFound_PerAC5()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var database = await SqliteTestDatabase.CreateAsync(ct);
        await SeedAsync(database, 1, origin: TicketOrigin.Training);
        var service = Service(database);

        (await service.OpenAsync(1, ct)).Should().BeNull();
        (await service.OpenAsync(99, ct)).Should().BeNull();
        (await service.ApproveAsync(1, 1, null, ct)).Outcome.Should().Be(ReviewOutcome.NotFound);
        (await service.RejectAsync(99, 1, "x", ct)).Outcome.Should().Be(ReviewOutcome.NotFound);
    }

    [Fact]
    [Trait("Category", "Smoke")]
    public async Task Smoke_IngestClaimSave_ThenOpenEditApprove_PerAC4And5()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var database = await SqliteTestDatabase.CreateAsync(ct);
        var time = new FixedTime(T0);
        await using (var db = await database.Factory.CreateDbContextAsync(ct))
        {
            var ingest = await new EfTicketIngestor(db, time).IngestAsync(
                [new Ticket { Key = "#1", Summary = "VPN broken", Description = "cannot connect" }], TicketOrigin.Challenge, ct);
            ingest.Single().Outcome.Should().Be(IngestOutcome.Created);
        }

        var store = new TicketClaimStore(database.Factory, Options.Create(new AnalysisOptions()), Options.Create(new TriageOptions()), time);
        var claimed = (await store.ClaimAsync(5, ct)).Single();
        var suggestion = new TriageSuggestion
        {
            TicketKey = "DB-1",
            WorkType = WorkType.Incident,
            AffectedServices = ["Outlook & Email"],
            ServiceTeams = ["Team A"],
            Urgency = Urgency.High,
            Impact = Impact.Minor,
            ResolutionStatus = ResolutionStatus.Clarification,
            DraftComment = "Please retry",
        };
        (await store.SaveAsync(claimed, suggestion, ct)).Should().BeTrue();
        var service = Service(database);

        var view = await service.OpenAsync(claimed.Ticket.Id!.Value, ct);
        view!.StatusName.Should().Be("Reviewing");
        view.Ticket.Summary.Should().Be("VPN broken");
        var saved = await service.SaveEditsAsync(view.Ticket.Id!.Value, view.Version, new ReviewEdits { Impact = Impact.Major }, ct);
        var approved = await service.ApproveAsync(view.Ticket.Id!.Value, saved.Version!.Value, null, ct);

        approved.Outcome.Should().Be(ReviewOutcome.Success);
        var final = await service.OpenAsync(view.Ticket.Id!.Value, ct);
        final!.StatusName.Should().Be("HumanApproved");
        final.EffectiveSuggestion!.Priority.Should().Be(PriorityMatrix.Resolve(Urgency.High, Impact.Major));
        final.Edits.Should().ContainSingle();
    }
}
