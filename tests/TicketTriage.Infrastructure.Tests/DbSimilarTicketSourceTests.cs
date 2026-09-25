using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using TicketTriage.Core.Domain;
using TicketTriage.Infrastructure.Pipeline;
using TicketTriage.Infrastructure.Retrieval;
using TicketTriage.Infrastructure.Sources;

namespace TicketTriage.Infrastructure.Tests;

public sealed class DbSimilarTicketSourceTests : IAsyncLifetime
{
    private SqliteTestDatabase _db = null!;
    private SimilarTicketIndexProvider _provider = null!;
    private DbSimilarTicketSource _source = null!;

    public async ValueTask InitializeAsync()
    {
        _db = await SqliteTestDatabase.CreateAsync(TestContext.Current.CancellationToken);
        _provider = new SimilarTicketIndexProvider(_db.Factory, NullLogger<SimilarTicketIndexProvider>.Instance);
        _source = new DbSimilarTicketSource(_db.Factory, _provider, new LookupNamesProvider(_db.Factory), NullLogger<DbSimilarTicketSource>.Instance);
    }

    public async ValueTask DisposeAsync()
    {
        _provider.Dispose();
        await _db.DisposeAsync();
    }

    private async Task SeedAsync()
    {
        var ct = TestContext.Current.CancellationToken;
        await _db.AddTicketAsync(1, "Outlook crashes when opening the calendar", cancellationToken: ct);
        await _db.AddTicketAsync(2, "Printer on floor three shows paper jam", cancellationToken: ct);
        await _db.AddTicketAsync(3, "VPN connection drops every hour", cancellationToken: ct);
    }

    [Fact]
    public async Task FindSimilar_DistinctiveTerm_ReturnsThatRowFirst_PerAC1()
    {
        await SeedAsync();

        var result = await _source.FindSimilarAsync(Tickets.Make("NEW-1", "paper jam again"), 3, TestContext.Current.CancellationToken);

        result.Should().NotBeEmpty();
        result[0].Ticket.Id.Should().Be(2);
    }

    [Fact]
    public async Task FindSimilar_MapsKeyNamesCommentsAndNullsSeverity_PerAC5()
    {
        var ct = TestContext.Current.CancellationToken;
        await _db.AddTicketAsync(
            7,
            "Fund pricing feed is stale",
            comments: ["first", "second"],
            workTypeId: 1,
            serviceId: 4,
            teamId: 6,
            created: new DateTime(2024, 5, 6, 7, 8, 9, DateTimeKind.Unspecified),
            assignee: "alice",
            resolution: "restarted feed",
            cancellationToken: ct);

        var result = await _source.FindSimilarAsync(Tickets.Make("NEW-1", "pricing feed stale"), 5, ct);

        var mapped = result.Should().ContainSingle().Subject.Ticket;
        mapped.Key.Should().Be("DB-7");
        mapped.Id.Should().Be(7);
        mapped.Summary.Should().Be("Summary 7");
        mapped.Description.Should().Be("Fund pricing feed is stale");
        mapped.WorkType.Should().Be("Service Request");
        mapped.AffectedServices.Should().Equal("Fund Pricing");
        mapped.ServiceTeams.Should().Equal("Valuation & Pricing");
        mapped.Assignee.Should().Be("alice");
        mapped.Resolution.Should().Be("restarted feed");
        mapped.Comments.Should().Equal("first", "second");
        mapped.Created.Should().Be(new DateTimeOffset(2024, 5, 6, 7, 8, 9, TimeSpan.Zero));
        mapped.Urgency.Should().BeNull();
        mapped.Impact.Should().BeNull();
        mapped.Priority.Should().BeNull();
    }

    [Fact]
    public async Task FindSimilar_AtMostTop_DescendingScores_PerAC2()
    {
        var ct = TestContext.Current.CancellationToken;
        for (var i = 1; i <= 6; i++)
        {
            await _db.AddTicketAsync(i, $"printer issue {new string('x', i)} common", cancellationToken: ct);
        }

        var result = await _source.FindSimilarAsync(Tickets.Make("NEW-1", "printer issue common"), 3, ct);

        result.Should().HaveCount(3);
        result.Select(r => r.Score).Should().BeInDescendingOrder().And.OnlyContain(s => s > 0 && s <= 1);
    }

    [Fact]
    public async Task FindSimilar_QueryWithSameId_NeverReturnsOwnRow_PerAC3()
    {
        await SeedAsync();
        var own = Tickets.Make("DB-2", "Printer on floor three shows paper jam") with { Id = 2 };

        var result = await _source.FindSimilarAsync(own, 5, TestContext.Current.CancellationToken);

        result.Should().NotContain(r => r.Ticket.Id == 2);
    }

    [Fact]
    public async Task FindSimilar_QueryWithoutId_IsNotDeduplicated()
    {
        await SeedAsync();

        var result = await _source.FindSimilarAsync(
            Tickets.Make("NEW-1", "Printer on floor three shows paper jam"), 5, TestContext.Current.CancellationToken);

        result[0].Ticket.Id.Should().Be(2);
        result[0].Score.Should().BeApproximately(1.0, 1e-9);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("  \t\n ")]
    public async Task FindSimilar_BlankDescription_ReturnsEmpty_PerAC4(string? description)
    {
        await SeedAsync();

        var result = await _source.FindSimilarAsync(Tickets.Make("NEW-1", description), 5, TestContext.Current.CancellationToken);

        result.Should().BeEmpty();
    }

    [Fact]
    public async Task FindSimilar_TopZero_ReturnsEmpty_PerAC4()
    {
        await SeedAsync();

        var result = await _source.FindSimilarAsync(Tickets.Make("NEW-1", "printer"), 0, TestContext.Current.CancellationToken);

        result.Should().BeEmpty();
    }

    [Fact]
    public async Task FindSimilar_EmptyDatabase_ReturnsEmpty_PerAC4()
    {
        var result = await _source.FindSimilarAsync(Tickets.Make("NEW-1", "printer"), 5, TestContext.Current.CancellationToken);

        result.Should().BeEmpty();
    }

    [Fact]
    public async Task FindSimilar_RowsWithBlankDescription_AreNeverCandidates()
    {
        var ct = TestContext.Current.CancellationToken;
        await _db.AddTicketAsync(1, null, cancellationToken: ct);
        await _db.AddTicketAsync(2, "   ", cancellationToken: ct);
        await _db.AddTicketAsync(3, "printer jam", cancellationToken: ct);

        var result = await _source.FindSimilarAsync(Tickets.Make("NEW-1", "printer jam"), 5, ct);

        result.Select(r => r.Ticket.Id).Should().Equal(3);
    }

    [Fact]
    [Trait("Category", "Smoke")]
    public async Task Pipeline_WithDbSimilarSource_SuggestionCarriesDbKeys()
    {
        var ct = TestContext.Current.CancellationToken;
        await SeedAsync();
        var log = new CallLog();
        var pipeline = new TriagePipeline(
            new TicketNormalizer(NullLogger<TicketNormalizer>.Instance),
            _source,
            new FakeClassifier(log),
            new FakeRouter(log),
            new FakeRoutingStatisticsSource(),
            new FakeDrafter(log),
            Options.Create(new TriageOptions { SimilarTicketCount = 2 }),
            new RecordingFailureStore(),
            NullLogger<TriagePipeline>.Instance);

        var suggestion = await pipeline.TriageAsync(Tickets.Make("NEW-1", "paper jam on the printer"), ct);

        suggestion.SimilarTicketKeys.Should().NotBeEmpty().And.OnlyContain(k => k.StartsWith("DB-", StringComparison.Ordinal));
        suggestion.SimilarTicketKeys[0].Should().Be("DB-2");
    }
}
