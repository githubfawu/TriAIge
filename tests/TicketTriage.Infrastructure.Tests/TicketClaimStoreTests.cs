using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using TicketTriage.Core.Domain;
using TicketTriage.Infrastructure.Analysis;
using TicketTriage.Infrastructure.Persistence;
using TicketTriage.Infrastructure.Pipeline;

namespace TicketTriage.Infrastructure.Tests;

public sealed class TicketClaimStoreTests
{
    private sealed class MutableTime(DateTimeOffset start) : TimeProvider
    {
        public DateTimeOffset Current { get; set; } = start;

        public override DateTimeOffset GetUtcNow() => Current;
    }

    private static readonly DateTimeOffset T0 = new(2026, 9, 25, 12, 0, 0, TimeSpan.Zero);

    private static TicketClaimStore Store(SqliteTestDatabase database, MutableTime? time = null, int leaseMinutes = 20) =>
        new(database.Factory, Options.Create(new AnalysisOptions { LeaseMinutes = leaseMinutes }), Options.Create(new TriageOptions()), time ?? new MutableTime(T0));

    private static async Task AddAsync(
        SqliteTestDatabase database,
        int id,
        TicketOrigin origin,
        int statusId = TicketStatusIds.New,
        int minutesAgo = 0,
        DateTime? claimedAt = null)
    {
        await using var db = await database.Factory.CreateDbContextAsync(TestContext.Current.CancellationToken);
        db.Tickets.Add(new TicketEntity
        {
            Id = id,
            Summary = $"Summary {id}",
            Origin = origin,
            SourceKey = origin == TicketOrigin.Training ? null : $"#{id}",
            SourcePayload = $$"""{"Summary":"Summary {{id}}","Description":"d{{id}}","Issue key":"#{{id}}"}""",
            IngestedAt = T0.UtcDateTime.AddMinutes(-minutesAgo),
            StatusId = statusId,
            ClaimedAt = claimedAt,
            CreatedDate = T0.UtcDateTime,
        });
        await db.SaveChangesAsync(TestContext.Current.CancellationToken);
    }

    private static TriageSuggestion Suggestion(int id) => new()
    {
        TicketKey = $"DB-{id}",
        WorkType = WorkType.Incident,
        Urgency = Urgency.Medium,
        Impact = Impact.Moderate,
        DraftComment = "ok",
    };

    private static async Task<TicketEntity> LoadAsync(SqliteTestDatabase database, int id)
    {
        await using var db = await database.Factory.CreateDbContextAsync(TestContext.Current.CancellationToken);
        return await db.Tickets.AsNoTracking().SingleAsync(t => t.Id == id, TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task Claim_OnlyNewNonTrainingUnclaimedTickets_PerAC3()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var database = await SqliteTestDatabase.CreateAsync(ct);
        await AddAsync(database, 1, TicketOrigin.Training);
        await AddAsync(database, 2, TicketOrigin.Challenge);
        await AddAsync(database, 3, TicketOrigin.Intake);
        await AddAsync(database, 4, TicketOrigin.Challenge, statusId: TicketStatusIds.Reviewing);
        await AddAsync(database, 5, TicketOrigin.Challenge, claimedAt: T0.UtcDateTime);

        var claimed = await Store(database).ClaimAsync(10, ct);

        claimed.Select(c => c.Ticket.Id).Should().BeEquivalentTo([2, 3]);
        claimed.Should().OnlyContain(c => c.Ticket.Key == $"DB-{c.Ticket.Id}");
        claimed.Select(c => c.Ticket.Description).Should().BeEquivalentTo(["d2", "d3"]);
        (await LoadAsync(database, 1)).ClaimedAt.Should().BeNull();
    }

    [Fact]
    public async Task Claim_RespectsBatchSizeAndIngestOrder_PerAC3()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var database = await SqliteTestDatabase.CreateAsync(ct);
        await AddAsync(database, 1, TicketOrigin.Challenge, minutesAgo: 1);
        await AddAsync(database, 2, TicketOrigin.Challenge, minutesAgo: 30);
        await AddAsync(database, 3, TicketOrigin.Challenge, minutesAgo: 10);
        await AddAsync(database, 4, TicketOrigin.Challenge, minutesAgo: 10);

        var claimed = await Store(database).ClaimAsync(3, ct);

        claimed.Select(c => c.Ticket.Id).Should().Equal(2, 3, 4);
    }

    [Fact]
    public async Task Claim_Twice_ReturnsDisjointSetsWithDistinctClaims_PerAC3()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var database = await SqliteTestDatabase.CreateAsync(ct);
        for (var id = 1; id <= 5; id++)
        {
            await AddAsync(database, id, TicketOrigin.Challenge, minutesAgo: 10 - id);
        }

        var time = new MutableTime(T0);
        var first = await Store(database, time).ClaimAsync(3, ct);
        var second = await Store(database, time).ClaimAsync(3, ct);

        first.Should().HaveCount(3);
        second.Should().HaveCount(2);
        first.Select(c => c.Ticket.Id).Intersect(second.Select(c => c.Ticket.Id)).Should().BeEmpty();
        first[0].Claim.Should().NotBe(second[0].Claim, "the claim value must be unique even when the clock does not advance");
    }

    [Fact]
    public async Task ReleaseStale_ReleasesExpiredKeepsFresh_PerAC3()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var database = await SqliteTestDatabase.CreateAsync(ct);
        await AddAsync(database, 1, TicketOrigin.Challenge, claimedAt: T0.UtcDateTime.AddMinutes(-21));
        await AddAsync(database, 2, TicketOrigin.Challenge, claimedAt: T0.UtcDateTime.AddMinutes(-5));

        var released = await Store(database).ReleaseStaleAsync(ct);

        released.Should().Be(1);
        (await LoadAsync(database, 1)).ClaimedAt.Should().BeNull();
        (await LoadAsync(database, 2)).ClaimedAt.Should().NotBeNull();
    }

    [Fact]
    public async Task Save_WithOwnLease_ReviewingClearsClaimBumpsVersionStoresSuggestion_PerAC3()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var database = await SqliteTestDatabase.CreateAsync(ct);
        await AddAsync(database, 1, TicketOrigin.Challenge);
        var store = Store(database);
        var claimed = (await store.ClaimAsync(5, ct)).Single();

        var saved = await store.SaveAsync(claimed, Suggestion(1), ct);

        saved.Should().BeTrue();
        var row = await LoadAsync(database, 1);
        row.StatusId.Should().Be(TicketStatusIds.Reviewing);
        row.ClaimedAt.Should().BeNull();
        row.Version.Should().Be(1);
        await using var db = await database.Factory.CreateDbContextAsync(ct);
        (await db.Suggestions.SingleAsync(ct)).TicketId.Should().Be(1);
    }

    [Fact]
    public async Task Save_AfterLeaseSwept_ReturnsFalseAndStoresNothing_PerAC3()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var database = await SqliteTestDatabase.CreateAsync(ct);
        await AddAsync(database, 1, TicketOrigin.Challenge);
        var time = new MutableTime(T0);
        var store = Store(database, time);
        var claimed = (await store.ClaimAsync(5, ct)).Single();

        time.Current = T0.AddMinutes(30);
        await store.ReleaseStaleAsync(ct);

        (await store.SaveAsync(claimed, Suggestion(1), ct)).Should().BeFalse();
        (await LoadAsync(database, 1)).StatusId.Should().Be(TicketStatusIds.New);
        await using var db = await database.Factory.CreateDbContextAsync(ct);
        (await db.Suggestions.CountAsync(ct)).Should().Be(0);
    }

    [Fact]
    public async Task Save_AfterTicketReclaimedByOtherWorker_ReturnsFalse_PerAC3()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var database = await SqliteTestDatabase.CreateAsync(ct);
        await AddAsync(database, 1, TicketOrigin.Challenge);
        var time = new MutableTime(T0);
        var store = Store(database, time);
        var first = (await store.ClaimAsync(5, ct)).Single();
        time.Current = T0.AddMinutes(30);
        await store.ReleaseStaleAsync(ct);
        var second = (await store.ClaimAsync(5, ct)).Single();

        (await store.SaveAsync(first, Suggestion(1), ct)).Should().BeFalse();
        (await store.SaveAsync(second, Suggestion(1), ct)).Should().BeTrue();
    }

    [Fact]
    [Trait("Category", "Smoke")]
    public async Task Smoke_GateBeatSweepClaimSave_DrainsQueue()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var database = await SqliteTestDatabase.CreateAsync(ct);
        for (var id = 1; id <= 3; id++)
        {
            await AddAsync(database, id, TicketOrigin.Challenge, minutesAgo: 10 - id);
        }

        var store = Store(database);
        await store.BeatAsync(ct);
        await store.ReleaseStaleAsync(ct);
        foreach (var item in await store.ClaimAsync(5, ct))
        {
            (await store.SaveAsync(item, Suggestion(item.Ticket.Id!.Value), ct)).Should().BeTrue();
        }

        (await store.ClaimAsync(5, ct)).Should().BeEmpty();
        await using var db = await database.Factory.CreateDbContextAsync(ct);
        (await db.Suggestions.CountAsync(ct)).Should().Be(3);
    }

    [Fact]
    public async Task Release_ClearsOnlyOwnClaims_PerAC3()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var database = await SqliteTestDatabase.CreateAsync(ct);
        await AddAsync(database, 1, TicketOrigin.Challenge);
        await AddAsync(database, 2, TicketOrigin.Challenge, claimedAt: T0.UtcDateTime.AddMinutes(-1));
        var store = Store(database);
        var mine = (await store.ClaimAsync(5, ct)).Single();

        await store.ReleaseAsync(mine.Claim, ct);

        (await LoadAsync(database, 1)).ClaimedAt.Should().BeNull();
        (await LoadAsync(database, 2)).ClaimedAt.Should().NotBeNull();
    }

    [Fact]
    public async Task IsDataReady_FalseWithoutMarker_TrueWithMarker_PerAC3()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var database = await SqliteTestDatabase.CreateAsync(ct);
        var store = Store(database);

        (await store.IsDataReadyAsync(ct)).Should().BeFalse();

        await using (var db = await database.Factory.CreateDbContextAsync(ct))
        {
            db.SystemMarkers.Add(new SystemMarkerEntity { Name = SystemMarkerNames.TrainingDataReady, SetAtUtc = T0.UtcDateTime });
            await db.SaveChangesAsync(ct);
        }

        (await store.IsDataReadyAsync(ct)).Should().BeTrue();
    }

    [Fact]
    public async Task Beat_Twice_UpdatesSingleRow_PerAC3()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var database = await SqliteTestDatabase.CreateAsync(ct);
        var time = new MutableTime(T0);
        var store = Store(database, time);

        await store.BeatAsync(ct);
        time.Current = T0.AddSeconds(30);
        await store.BeatAsync(ct);

        await using var db = await database.Factory.CreateDbContextAsync(ct);
        var rows = await db.SystemMarkers.Where(m => m.Name == SystemMarkerNames.AnalysisWorkerHeartbeat).ToListAsync(ct);
        rows.Should().ContainSingle().Which.SetAtUtc.Should().Be(T0.AddSeconds(30).UtcDateTime);
    }

    private static AnalysisOptionsValidator Validator(TriageOptions? triage = null) =>
        new(Options.Create(triage ?? new TriageOptions()));

    [Fact]
    public void Options_Defaults_AreValid_PerAC3()
    {
        Validator().Validate(null, new AnalysisOptions()).Succeeded.Should().BeTrue();
    }

    [Fact]
    public void Options_LeaseShorterThanWorstCaseBatch_Fails_PerAC3()
    {
        var result = Validator().Validate(null, new AnalysisOptions { LeaseMinutes = 10 });

        result.Failed.Should().BeTrue();
        result.FailureMessage.Should().Contain("LeaseMinutes");
    }

    [Fact]
    public void Options_OutOfRange_Fails_PerAC3()
    {
        Validator().Validate(null, new AnalysisOptions { BatchSize = 0 }).Failed.Should().BeTrue();
    }

    [Fact]
    public void Options_LongerTicketTimeout_RaisesRequiredLease_PerAC3()
    {
        var triage = new TriageOptions { TicketTimeoutSeconds = 120 };

        Validator(triage).Validate(null, new AnalysisOptions()).Failed.Should().BeTrue();
    }
}
