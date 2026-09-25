using Microsoft.Extensions.Logging.Abstractions;
using TicketTriage.Core.Domain;
using TicketTriage.Infrastructure.Routing;
using TicketTriage.Infrastructure.Sources;

namespace TicketTriage.Infrastructure.Tests;

public class StatisticsRoutingResolverTests
{
    // Seed ids: services 3 = Trading Platform, 4 = Fund Pricing; teams 9 = Trading Support, 6 = Valuation & Pricing, 7 = Client Services.
    private const int TradingPlatform = 3;
    private const int FundPricing = 4;

    private static StatisticsRoutingResolver Resolver(SqliteTestDatabase db, out RoutingStatisticsProvider provider)
    {
        provider = new RoutingStatisticsProvider(db.Factory, new LookupNamesProvider(db.Factory), NullLogger<RoutingStatisticsProvider>.Instance);
        return new StatisticsRoutingResolver(provider, new FakeWorkload());
    }

    private static Task<RoutingDecision> Route(StatisticsRoutingResolver sut, string service, CancellationToken ct) =>
        sut.ResolveAsync(
            Tickets.Make("T-1"),
            new TicketClassification(WorkType.Incident, [service], Urgency.High, Impact.Significant),
            [],
            ct);

    [Fact]
    public async Task Resolve_CraftedDatabase_ReturnsMajorityTeam_PerAC3()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var db = await SqliteTestDatabase.CreateAsync(ct);
        await db.AddTicketAsync(1, "d", serviceId: TradingPlatform, teamId: 9, assignee: "alice", cancellationToken: ct);
        await db.AddTicketAsync(2, "d", serviceId: TradingPlatform, teamId: 9, assignee: "bob", cancellationToken: ct);
        await db.AddTicketAsync(3, "d", serviceId: TradingPlatform, teamId: 9, assignee: "bob", cancellationToken: ct);
        await db.AddTicketAsync(4, "d", serviceId: TradingPlatform, teamId: 7, assignee: "zed", cancellationToken: ct);
        await db.AddTicketAsync(5, "d", serviceId: FundPricing, teamId: 6, assignee: null, cancellationToken: ct);
        var sut = Resolver(db, out _);

        var trading = await Route(sut, "Trading Platform", ct);
        var pricing = await Route(sut, "fund pricing", ct);

        trading.ServiceTeams.Should().Equal("Trading Support");
        pricing.ServiceTeams.Should().Equal("Valuation & Pricing");
        trading.Assignee.Should().Be(FakeRouting.Assignee, "the assignee comes from the workload, not from the majority vote");
    }

    [Fact]
    public async Task Resolve_Ties_UseAlphabeticalOrder_PerAC3()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var db = await SqliteTestDatabase.CreateAsync(ct);
        await db.AddTicketAsync(1, "d", serviceId: FundPricing, teamId: 6, assignee: "bob", cancellationToken: ct);
        await db.AddTicketAsync(2, "d", serviceId: FundPricing, teamId: 7, assignee: "alice", cancellationToken: ct);
        var sut = Resolver(db, out _);

        var decision = await Route(sut, "Fund Pricing", ct);

        decision.ServiceTeams.Should().Equal("Client Services");
    }

    [Fact]
    public async Task Resolve_UnknownService_ReturnsEmptyRouting_PerAC3()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var db = await SqliteTestDatabase.CreateAsync(ct);
        await db.AddTicketAsync(1, "d", serviceId: FundPricing, teamId: 6, assignee: "bob", cancellationToken: ct);
        var sut = Resolver(db, out _);

        var decision = await Route(sut, "Trading Platform", ct);

        decision.ServiceTeams.Should().BeEmpty();
        decision.Assignee.Should().Be(FakeRouting.Assignee, "the assignee does not depend on the service");
    }

    [Fact]
    public async Task Provider_LoadsOnceForTwoResolves_PerNFR3()
    {
        var ct = TestContext.Current.CancellationToken;
        var loads = 0;
        var provider = new RoutingStatisticsProvider(
            _ =>
            {
                loads++;
                return Task.FromResult<IReadOnlyList<(string, string, int)>>([("Fund Pricing", "Client Services", 1)]);
            },
            NullLogger<RoutingStatisticsProvider>.Instance);
        var sut = new StatisticsRoutingResolver(provider, new FakeWorkload());

        await Route(sut, "Fund Pricing", ct);
        await Route(sut, "Fund Pricing", ct);

        loads.Should().Be(1);
    }

    [Fact]
    public async Task Provider_EmptyLoad_IsNotCachedAndRetried()
    {
        var ct = TestContext.Current.CancellationToken;
        var loads = 0;
        var provider = new RoutingStatisticsProvider(
            _ => Task.FromResult<IReadOnlyList<(string, string, int)>>(
                ++loads == 1 ? [] : [("Fund Pricing", "Client Services", 1)]),
            NullLogger<RoutingStatisticsProvider>.Instance);
        var sut = new StatisticsRoutingResolver(provider, new FakeWorkload());

        var first = await Route(sut, "Fund Pricing", ct);
        var second = await Route(sut, "Fund Pricing", ct);
        await Route(sut, "Fund Pricing", ct);

        first.ServiceTeams.Should().BeEmpty();
        second.ServiceTeams.Should().Equal("Client Services");
        loads.Should().Be(2);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Provider_DatabaseTies_ResolveIndependentOfInsertionOrder(bool reversed)
    {
        var ct = TestContext.Current.CancellationToken;
        await using var db = await SqliteTestDatabase.CreateAsync(ct);
        (int Id, int Team, string Assignee)[] rows = [(1, 6, "bob"), (2, 7, "alice"), (3, 6, "carol"), (4, 7, "Alice")];
        foreach (var r in reversed ? rows.Reverse() : rows)
        {
            await db.AddTicketAsync(r.Id, "d", serviceId: FundPricing, teamId: r.Team, assignee: r.Assignee, cancellationToken: ct);
        }

        var decision = await Route(Resolver(db, out _), "Fund Pricing", ct);

        decision.ServiceTeams.Should().Equal("Client Services");
    }

    [Fact]
    public async Task Provider_CancelledFirstLoad_IsRetried_PerNFR3()
    {
        var ct = TestContext.Current.CancellationToken;
        var loads = 0;
        using var cancelled = new CancellationTokenSource();
        var provider = new RoutingStatisticsProvider(
            async token =>
            {
                // Cancel while the load is in flight, like a caller giving up mid-query.
                if (++loads == 1)
                {
                    await cancelled.CancelAsync();
                }

                token.ThrowIfCancellationRequested();
                return [("Fund Pricing", "Client Services", 1)];
            },
            NullLogger<RoutingStatisticsProvider>.Instance);
        var sut = new StatisticsRoutingResolver(provider, new FakeWorkload());

        var act = async () => await Route(sut, "Fund Pricing", cancelled.Token);
        await act.Should().ThrowAsync<OperationCanceledException>();
        var decision = await Route(sut, "Fund Pricing", ct);

        decision.ServiceTeams.Should().Equal("Client Services");
        loads.Should().Be(2);
    }

    [Fact]
    [Trait("Category", "Smoke")]
    public async Task Smoke_ResolveOverSeededDatabase_ReturnsRouteForCatalogService()
    {
        var ct = TestContext.Current.CancellationToken;
        await using var db = await SqliteTestDatabase.CreateAsync(ct);
        for (var i = 1; i <= 6; i++)
        {
            await db.AddTicketAsync(i, "printer broken", serviceId: TradingPlatform, teamId: 9, assignee: i % 2 == 0 ? "alice" : "bob", cancellationToken: ct);
        }

        var sut = Resolver(db, out _);

        var decision = await Route(sut, ServiceCatalog.All[0].Name, ct);

        decision.ServiceTeams.Should().ContainSingle().Which.Should().Be("Trading Support");
        decision.Assignee.Should().Be("alice");
    }
}
