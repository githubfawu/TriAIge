using System.ComponentModel;
using TicketTriage.Agents.Prompting;
using TicketTriage.Core.Domain;

namespace TicketTriage.Agents.Classification;

/// <summary>Structured model output. Strings on purpose: they are validated strictly before mapping onto Core types.</summary>
internal sealed record ClassificationDto(
    [property: Description("Incident or Service Request")] string? WorkType,
    [property: Description("Affected services, exact names from the service list")] string[]? AffectedServices,
    [property: Description("Critical, High, Medium, Low or Lowest")] string? Urgency,
    [property: Description("Major, Significant, Moderate, Minor or No Impact")] string? Impact)
{
    public TicketClassification ToDomain(IReadOnlyList<ServiceDefinition> catalog)
    {
        var services = new List<string>();
        foreach (var name in AffectedServices ?? [])
        {
            var match = catalog.FirstOrDefault(s => string.Equals(s.Name, name?.Trim(), StringComparison.OrdinalIgnoreCase))
                ?? throw new InvalidOperationException("The model returned a service that is not in the service catalog.");

            if (!services.Contains(match.Name))
            {
                services.Add(match.Name);
            }
        }

        if (services.Count == 0)
        {
            throw new InvalidOperationException("The model returned no affected service.");
        }

        return new TicketClassification(
            EnumNames.Parse<WorkType>(WorkType, "work type"),
            services,
            EnumNames.Parse<Urgency>(Urgency, "urgency"),
            EnumNames.Parse<Impact>(Impact, "impact"));
    }
}
