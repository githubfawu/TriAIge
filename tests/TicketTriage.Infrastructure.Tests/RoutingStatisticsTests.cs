using TicketTriage.Infrastructure.Routing;

namespace TicketTriage.Infrastructure.Tests;

public class RoutingStatisticsTests
{
    private static RoutingStatistics Build(params (string Service, string Team, string? Assignee, int Count)[] rows) =>
        RoutingStatistics.Build(rows);

    [Fact]
    public void Resolve_MajorityTeamAcrossAssignees_PerAC3()
    {
        var stats = Build(("Trading Platform", "Trading Support", "alice", 2), ("Trading Platform", "Trading Support", "bob", 2), ("Trading Platform", "Service Desk", "carol", 3));

        var decision = stats.Resolve(["Trading Platform"]);

        decision.ServiceTeams.Should().Equal("Trading Support");
    }

    [Fact]
    public void Resolve_MajorityAssigneeWithinServiceAndTeam_PerAC3()
    {
        var stats = Build(("Trading Platform", "Trading Support", "alice", 1), ("Trading Platform", "Trading Support", "bob", 4), ("Trading Platform", "Service Desk", "carol", 1));

        stats.Resolve(["Trading Platform"]).Assignee.Should().Be("bob");
    }

    [Fact]
    public void Resolve_TeamTie_PicksAlphabeticallyFirst_PerAC3()
    {
        var stats = Build(("Fund Pricing", "Valuation & Pricing", "a", 2), ("Fund Pricing", "Client Services", "b", 2));

        stats.Resolve(["Fund Pricing"]).ServiceTeams.Should().Equal("Client Services");
    }

    [Fact]
    public void Resolve_AssigneeTie_PicksAlphabeticallyFirstIgnoringCase_PerAC3()
    {
        var stats = Build(("Fund Pricing", "Client Services", "bob", 2), ("Fund Pricing", "Client Services", "Alice", 2));

        stats.Resolve(["Fund Pricing"]).Assignee.Should().Be("Alice");
    }

    [Fact]
    public void Resolve_UnknownServiceOrNoServices_ReturnsEmptyRouting_PerAC3()
    {
        var stats = Build(("Fund Pricing", "Client Services", "bob", 1));

        stats.Resolve(["Mainframe"]).Should().BeEquivalentTo(new { ServiceTeams = Array.Empty<string>(), Assignee = (string?)null });
        stats.Resolve([]).ServiceTeams.Should().BeEmpty();
        stats.IsKnownService("Mainframe").Should().BeFalse();
    }

    [Fact]
    public void Resolve_ServiceLookupIsCaseInsensitive_PerAC3() =>
        Build(("Fund Pricing", "Client Services", "bob", 1)).Resolve(["fund PRICING"])
            .ServiceTeams.Should().Equal("Client Services");

    [Fact]
    public void Resolve_FirstListedServiceWins_PerAC3()
    {
        var stats = Build(("Fund Pricing", "Client Services", "bob", 1), ("Tax Reporting", "Tax & Reporting", "eve", 5));

        var decision = stats.Resolve(["Fund Pricing", "Tax Reporting"]);

        decision.ServiceTeams.Should().Equal("Client Services");
        decision.Assignee.Should().Be("bob");
    }

    [Fact]
    public void Build_NullAssigneeRowsCountForTeamOnly_PerAC3()
    {
        var stats = Build(("Fund Pricing", "Client Services", null, 5), ("Fund Pricing", "Client Services", "bob", 1), ("Fund Pricing", "Client Services", "  ", 3));

        var decision = stats.Resolve(["Fund Pricing"]);

        decision.ServiceTeams.Should().Equal("Client Services");
        decision.Assignee.Should().Be("bob");
    }

    [Fact]
    public void Build_OnlyNullAssignees_YieldsTeamWithoutAssignee_PerAC3() =>
        Build(("Fund Pricing", "Client Services", null, 5)).Resolve(["Fund Pricing"]).Assignee.Should().BeNull();

    [Fact]
    public void Build_RowsWithoutServiceOrTeam_AreIgnored_PerAC3()
    {
        var stats = Build(("", "Client Services", "bob", 1), ("Fund Pricing", " ", "bob", 1));

        stats.ServiceCount.Should().Be(0);
    }
}
