using TicketTriage.Infrastructure.Routing;

namespace TicketTriage.Infrastructure.Tests;

public class RoutingStatisticsTests
{
    private static RoutingStatistics Build(params (string Service, string Team, int Count)[] rows) =>
        RoutingStatistics.Build(rows);

    [Fact]
    public void ResolveTeams_MajorityTeamPerService_PerAC3()
    {
        var stats = Build(("Trading Platform", "Trading Support", 2), ("Trading Platform", "Trading Support", 2), ("Trading Platform", "Service Desk", 3));

        stats.ResolveTeams(["Trading Platform"]).Should().Equal("Trading Support");
    }

    [Fact]
    public void ResolveTeams_TeamTie_PicksAlphabeticallyFirst_PerAC3()
    {
        var stats = Build(("Fund Pricing", "Valuation & Pricing", 2), ("Fund Pricing", "Client Services", 2));

        stats.ResolveTeams(["Fund Pricing"]).Should().Equal("Client Services");
    }

    [Fact]
    public void ResolveTeams_UnknownServiceOrNoServices_ReturnsNoTeam_PerAC3()
    {
        var stats = Build(("Fund Pricing", "Client Services", 1));

        stats.ResolveTeams(["Mainframe"]).Should().BeEmpty();
        stats.ResolveTeams([]).Should().BeEmpty();
        stats.IsKnownService("Mainframe").Should().BeFalse();
    }

    [Fact]
    public void ResolveTeams_ServiceLookupIsCaseInsensitive_PerAC3() =>
        Build(("Fund Pricing", "Client Services", 1)).ResolveTeams(["fund PRICING"]).Should().Equal("Client Services");

    [Fact]
    public void ResolveTeams_FirstListedServiceWins_PerAC3()
    {
        var stats = Build(("Fund Pricing", "Client Services", 1), ("Tax Reporting", "Tax & Reporting", 5));

        stats.ResolveTeams(["Fund Pricing", "Tax Reporting"]).Should().Equal("Client Services");
    }

    [Fact]
    public void Build_RowsWithoutServiceOrTeam_AreIgnored_PerAC3()
    {
        var stats = Build(("", "Client Services", 1), ("Fund Pricing", " ", 1));

        stats.ServiceCount.Should().Be(0);
    }
}
