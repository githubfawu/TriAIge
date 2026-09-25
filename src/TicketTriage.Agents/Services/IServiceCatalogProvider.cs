using TicketTriage.Core.Domain;

namespace TicketTriage.Agents.Services;

/// <summary>Source of the valid "Affected Business or IT Services" and their criticality.</summary>
public interface IServiceCatalogProvider
{
    IReadOnlyList<ServiceDefinition> GetServices();
}

/// <summary>Default provider backed by Core's <see cref="ServiceCatalog"/>.</summary>
internal sealed class CoreServiceCatalogProvider : IServiceCatalogProvider
{
    public IReadOnlyList<ServiceDefinition> GetServices() => ServiceCatalog.All;
}
