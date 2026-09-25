using TicketTriage.Core.Domain;

namespace TicketTriage.Core.Tests;

public sealed class ServiceCatalogTests
{
    private static readonly string[] Critical =
    [
        "Trading Platform", "Order Management", "Trade Matching", "Securities Settlement", "Corporate Actions",
        "Fund Pricing", "NAV Calculation", "Portfolio Accounting", "Cash Management", "Risk & Compliance Monitoring",
        "Regulatory Reporting", "SimCorp Dimension", "Rimes Data Feed", "Client Reporting",
    ];

    private static readonly string[] NonCritical =
    [
        "Tax Reporting", "CRM & Client Portal", "Identity & Access Management", "SharePoint & File Storage",
        "Outlook & Email", "Emailed Support Tickets",
    ];

    [Fact]
    public void Catalog_contains_20_services_of_which_14_are_critical()
    {
        ServiceCatalog.All.Should().HaveCount(20);
        ServiceCatalog.All.Count(s => s.Rating == ServiceRating.Critical).Should().Be(14);
    }

    [Fact]
    public void Service_names_are_unique()
    {
        ServiceCatalog.All.Select(s => s.Name).Should().OnlyHaveUniqueItems();
    }

    [Fact]
    public void Catalog_is_exactly_the_20_names_of_requirements_section_6_PerAC5()
    {
        ServiceCatalog.All.Where(s => s.Rating == ServiceRating.Critical).Select(s => s.Name)
            .Should().BeEquivalentTo(Critical);
        ServiceCatalog.All.Where(s => s.Rating == ServiceRating.NonCritical).Select(s => s.Name)
            .Should().BeEquivalentTo(NonCritical);
    }

    [Theory]
    [InlineData("trading platform", "Trading Platform")]
    [InlineData("OUTLOOK & EMAIL", "Outlook & Email")]
    public void Find_is_case_insensitive_and_returns_the_canonical_name_PerAC5(string input, string canonical)
    {
        ServiceCatalog.Find(input)!.Name.Should().Be(canonical);
    }

    [Fact]
    public void Find_unknown_service_returns_null_PerAC5()
    {
        ServiceCatalog.Find("Mainframe").Should().BeNull();
        ServiceCatalog.IsCritical("Mainframe").Should().BeFalse();
    }
}
