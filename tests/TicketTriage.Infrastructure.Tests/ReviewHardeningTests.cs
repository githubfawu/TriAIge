using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using TicketTriage.Core.Abstractions;
using TicketTriage.Core.Domain;
using TicketTriage.Infrastructure.Persistence;
using TicketTriage.Infrastructure.Pipeline;
using TicketTriage.Infrastructure.Review;

namespace TicketTriage.Infrastructure.Tests;

public sealed class ReviewHardeningTests
{
    private static readonly DateTimeOffset T0 = new(2026, 9, 25, 12, 0, 0, TimeSpan.Zero);

    private sealed class FixedTime(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }

    private static EfReviewService Service(IDbContextFactory<TriageDbContext> factory) =>
        new(factory, Options.Create(new TriageOptions()), new FixedTime(T0));

    private static async Task SeedAsync(SqliteTestDatabase database, string? payload)
    {
        await using var db = await database.Factory.CreateDbContextAsync(TestContext.Current.CancellationToken);
        db.Tickets.Add(new TicketEntity
        {
            Id = 1,
            Summary = "Own summary",
            Description = "own description",
            Origin = TicketOrigin.Challenge,
            SourceKey = "#1",
            SourcePayload = payload,
            IngestedAt = T0.UtcDateTime,
            StatusId = TicketStatusIds.Reviewing,
            Version = 1,
            CreatedDate = T0.UtcDateTime,
        });
        db.Suggestions.Add(new TriageSuggestionEntity
        {
            TicketId = 1,
            WorkType = WorkType.Incident,
            AffectedServices = ["Trading Platform"],
            ServiceTeams = ["Trading Support"],
            Assignee = "alice",
            Urgency = Urgency.Medium,
            Impact = Impact.Moderate,
            Priority = PriorityMatrix.Resolve(Urgency.Medium, Impact.Moderate),
            ResolutionStatus = ResolutionStatus.Done,
            DraftComment = "AI comment",
            CreatedAtUtc = T0.UtcDateTime,
        });
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task Open_CorruptPayload_ReturnsTicketFromEntityColumns_PerAC4()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var database = await SqliteTestDatabase.CreateAsync(ct);
        await SeedAsync(database, "{not json");

        var review = await Service(database.Factory).OpenAsync(1, ct);

        review.Should().NotBeNull();
        review!.Ticket.Summary.Should().Be("Own summary");
        review.Ticket.Description.Should().Be("own description");
        review.Ticket.Key.Should().Be("DB-1");
    }

    [Fact]
    public async Task SaveEdits_UnknownTeam_IsInvalid_PerAC5()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var database = await SqliteTestDatabase.CreateAsync(ct);
        await SeedAsync(database, """{"Summary":"s"}""");

        var result = await Service(database.Factory).SaveEditsAsync(1, 1, new ReviewEdits { ServiceTeams = "Made Up Team" }, ct);

        result.Outcome.Should().Be(ReviewOutcome.Invalid);
        await using var db = await database.Factory.CreateDbContextAsync(ct);
        (await db.SuggestionEdits.CountAsync(ct)).Should().Be(0);
    }

    [Fact]
    public async Task SaveEdits_KnownTeamAnyCasing_StoresCanonicalName_PerAC5()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var database = await SqliteTestDatabase.CreateAsync(ct);
        await SeedAsync(database, """{"Summary":"s"}""");

        var result = await Service(database.Factory).SaveEditsAsync(1, 1, new ReviewEdits { ServiceTeams = " service desk " }, ct);

        result.Outcome.Should().Be(ReviewOutcome.Success);
        await using var db = await database.Factory.CreateDbContextAsync(ct);
        (await db.SuggestionEdits.SingleAsync(ct)).FinalValue.Should().Be("[\"Service Desk\"]");
    }

    [Fact]
    public async Task SaveEdits_AssigneeWithControlCharacters_StoresCleanedValue_PerAC5()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var database = await SqliteTestDatabase.CreateAsync(ct);
        await SeedAsync(database, """{"Summary":"s"}""");

        var result = await Service(database.Factory).SaveEditsAsync(1, 1, new ReviewEdits { Assignee = "bo\u0000b\r\n" }, ct);

        result.Outcome.Should().Be(ReviewOutcome.Success);
        await using var db = await database.Factory.CreateDbContextAsync(ct);
        (await db.SuggestionEdits.SingleAsync(ct)).FinalValue.Should().Be("bob");
    }

    [Fact]
    public async Task ServiceTeamCatalog_MatchesLookupSeed()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var database = await SqliteTestDatabase.CreateAsync(ct);
        await using var db = await database.Factory.CreateDbContextAsync(ct);

        var seeded = await db.ServiceTeams.AsNoTracking().Select(t => t.Name).ToListAsync(ct);

        seeded.Should().BeEquivalentTo(ServiceTeamCatalog.All);
    }

    [Fact]
    public async Task SaveEdits_ConcurrentDuplicateEditRow_IsConflictNotException_PerAC5()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var database = await SqliteTestDatabase.CreateAsync(ct);
        await SeedAsync(database, """{"Summary":"s"}""");
        var service = Service(new DuplicateEditFactory(database.Factory));

        var result = await service.SaveEditsAsync(1, 1, new ReviewEdits { Assignee = "bob" }, ct);

        result.Outcome.Should().Be(ReviewOutcome.Conflict);
    }

    // A second writer stores the same (TicketId, Field) edit row between the service's read and its save.
    private sealed class DuplicateEditFactory(IDbContextFactory<TriageDbContext> inner) : IDbContextFactory<TriageDbContext>
    {
        public TriageDbContext CreateDbContext() => throw new NotSupportedException();

        public async Task<TriageDbContext> CreateDbContextAsync(CancellationToken cancellationToken = default)
        {
            var context = await inner.CreateDbContextAsync(cancellationToken);
            context.SavingChanges += (_, _) =>
            {
                using var other = inner.CreateDbContext();
                other.Database.ExecuteSqlRaw(
                    "INSERT INTO SuggestionEdit (TicketId, Field, AiValue, FinalValue, EditedAtUtc) VALUES (1, 'Assignee', 'alice', 'carol', '2026-09-25 12:00:00')");
            };
            return context;
        }
    }
}
