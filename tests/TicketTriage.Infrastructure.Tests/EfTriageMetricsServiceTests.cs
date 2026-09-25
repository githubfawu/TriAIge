using TicketTriage.Core.Domain;
using TicketTriage.Infrastructure.Persistence;
using TicketTriage.Infrastructure.Review;

namespace TicketTriage.Infrastructure.Tests;

public sealed class EfTriageMetricsServiceTests
{
    private static readonly DateTime T0 = new(2026, 9, 25, 12, 0, 0, DateTimeKind.Utc);

    private static async Task SeedAsync(
        SqliteTestDatabase database,
        int id,
        ReviewDecision decision = ReviewDecision.Pending,
        TicketOrigin origin = TicketOrigin.Challenge,
        int? openedAfterMinutes = null,
        int? decidedAfterMinutes = null,
        params SuggestionField[] edits)
    {
        var ct = TestContext.Current.CancellationToken;
        await using var db = await database.Factory.CreateDbContextAsync(ct);
        db.Tickets.Add(new TicketEntity
        {
            Id = id,
            Summary = $"S{id}",
            Description = "d",
            Origin = origin,
            SourceKey = origin == TicketOrigin.Training ? null : $"#{id}",
            SourcePayload = "{}",
            IngestedAt = T0,
            StatusId = TicketStatusIds.Reviewing,
            CreatedDate = T0,
        });
        db.Suggestions.Add(new TriageSuggestionEntity
        {
            TicketId = id,
            WorkType = WorkType.Incident,
            Urgency = Urgency.Medium,
            Impact = Impact.Moderate,
            Priority = PriorityMatrix.Resolve(Urgency.Medium, Impact.Moderate),
            CreatedAtUtc = T0,
            Decision = decision,
            FirstOpenedAtUtc = openedAfterMinutes is { } o ? T0.AddMinutes(o) : null,
            DecidedAtUtc = decidedAfterMinutes is { } d ? T0.AddMinutes(d) : null,
        });
        foreach (var field in edits)
        {
            db.SuggestionEdits.Add(new SuggestionEditEntity { TicketId = id, Field = field.ToString(), EditedAtUtc = T0 });
        }

        await db.SaveChangesAsync(ct);
    }

    [Fact]
    public async Task Get_EmptyDatabase_ReturnsZerosAndNulls_PerFR25()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var database = await SqliteTestDatabase.CreateAsync(ct);

        var m = await new EfTriageMetricsService(database.Factory).GetAsync(ct);

        m.Suggested.Should().Be(0);
        m.Decided.Should().Be(0);
        m.AcceptanceRate.Should().BeNull();
        m.MedianIngestToFirstOpen.Should().BeNull();
        m.MedianIngestToDecision.Should().BeNull();
        m.EditsPerField.Keys.Should().BeEquivalentTo(Enum.GetValues<SuggestionField>());
        m.EditsPerField.Values.Should().AllSatisfy(v => v.Should().Be(0));
    }

    [Fact]
    public async Task Get_MixedDecisions_AcceptanceIsUnchangedApprovedOverDecided_PerFR25()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var database = await SqliteTestDatabase.CreateAsync(ct);
        await SeedAsync(database, 1, ReviewDecision.Approved);
        await SeedAsync(database, 2, ReviewDecision.Approved, edits: SuggestionField.Urgency);
        await SeedAsync(database, 3, ReviewDecision.Rejected);
        await SeedAsync(database, 4, ReviewDecision.Approved);
        await SeedAsync(database, 5);

        var m = await new EfTriageMetricsService(database.Factory).GetAsync(ct);

        m.Suggested.Should().Be(5);
        m.Decided.Should().Be(4);
        m.Approved.Should().Be(3);
        m.ApprovedWithoutEdits.Should().Be(2);
        m.Rejected.Should().Be(1);
        m.AcceptanceRate.Should().Be(0.5);
    }

    [Fact]
    public async Task Get_Edits_AreGroupedPerField_PerFR25()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var database = await SqliteTestDatabase.CreateAsync(ct);
        await SeedAsync(database, 1, ReviewDecision.Approved, edits: [SuggestionField.Urgency, SuggestionField.DraftComment]);
        await SeedAsync(database, 2, ReviewDecision.Approved, edits: [SuggestionField.Urgency]);

        var m = await new EfTriageMetricsService(database.Factory).GetAsync(ct);

        m.EditsPerField[SuggestionField.Urgency].Should().Be(2);
        m.EditsPerField[SuggestionField.DraftComment].Should().Be(1);
        m.EditsPerField[SuggestionField.Assignee].Should().Be(0);
    }

    [Fact]
    public async Task Get_Medians_OddAndEvenCounts_PerFR25()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var database = await SqliteTestDatabase.CreateAsync(ct);
        await SeedAsync(database, 1, ReviewDecision.Approved, openedAfterMinutes: 1, decidedAfterMinutes: 10);
        await SeedAsync(database, 2, ReviewDecision.Approved, openedAfterMinutes: 9, decidedAfterMinutes: 20);
        await SeedAsync(database, 3, ReviewDecision.Rejected, openedAfterMinutes: 5);

        var m = await new EfTriageMetricsService(database.Factory).GetAsync(ct);

        m.MedianIngestToFirstOpen.Should().Be(TimeSpan.FromMinutes(5));
        m.MedianIngestToDecision.Should().Be(TimeSpan.FromMinutes(15));
    }

    [Fact]
    public async Task Get_TrainingOrigin_IsIgnored_PerFR25()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var database = await SqliteTestDatabase.CreateAsync(ct);
        await SeedAsync(database, 1, ReviewDecision.Approved, TicketOrigin.Training, 3, 4, SuggestionField.Impact);
        await SeedAsync(database, 2, ReviewDecision.Approved);

        var m = await new EfTriageMetricsService(database.Factory).GetAsync(ct);

        m.Suggested.Should().Be(1);
        m.EditsPerField[SuggestionField.Impact].Should().Be(0);
        m.MedianIngestToFirstOpen.Should().BeNull();
    }

    [Fact]
    [Trait("Category", "Smoke")]
    public async Task Get_SmokeRealisticMix_CompletesWithSaneShape()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var database = await SqliteTestDatabase.CreateAsync(ct);
        await SeedAsync(database, 1, ReviewDecision.Approved, openedAfterMinutes: 2, decidedAfterMinutes: 6);
        await SeedAsync(database, 2, ReviewDecision.Rejected, openedAfterMinutes: 3, decidedAfterMinutes: 8);
        await SeedAsync(database, 3);

        var m = await new EfTriageMetricsService(database.Factory).GetAsync(ct);

        m.Suggested.Should().Be(3);
        m.AcceptanceRate.Should().BeInRange(0, 1);
    }
}
