using TicketTriage.Core.Domain;

namespace TicketTriage.Core.Tests;

public sealed class ServiceCatalogTests
{
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
}
