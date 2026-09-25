namespace TicketTriage.Core.Domain;

public sealed record ServiceDefinition(string Name, ServiceRating Rating);

/// <summary>
/// The fixed catalog of "Affected Business or IT Services" (20 services, 14 of them Critical).
/// </summary>
public static class ServiceCatalog
{
    // Order follows docs/requirements.md section 6; names match the AffectedBusinessOrITServices lookup seed.
    public static IReadOnlyList<ServiceDefinition> All { get; } =
    [
        new("Trading Platform", ServiceRating.Critical),
        new("Order Management", ServiceRating.Critical),
        new("Trade Matching", ServiceRating.Critical),
        new("Securities Settlement", ServiceRating.Critical),
        new("Corporate Actions", ServiceRating.Critical),
        new("Fund Pricing", ServiceRating.Critical),
        new("NAV Calculation", ServiceRating.Critical),
        new("Portfolio Accounting", ServiceRating.Critical),
        new("Cash Management", ServiceRating.Critical),
        new("Risk & Compliance Monitoring", ServiceRating.Critical),
        new("Regulatory Reporting", ServiceRating.Critical),
        new("SimCorp Dimension", ServiceRating.Critical),
        new("Rimes Data Feed", ServiceRating.Critical),
        new("Client Reporting", ServiceRating.Critical),
        new("Tax Reporting", ServiceRating.NonCritical),
        new("CRM & Client Portal", ServiceRating.NonCritical),
        new("Identity & Access Management", ServiceRating.NonCritical),
        new("SharePoint & File Storage", ServiceRating.NonCritical),
        new("Outlook & Email", ServiceRating.NonCritical),
        new("Emailed Support Tickets", ServiceRating.NonCritical),
    ];

    private static readonly Dictionary<string, ServiceDefinition> ByName =
        All.ToDictionary(s => s.Name, StringComparer.OrdinalIgnoreCase);

    public static ServiceDefinition? Find(string name) => ByName.GetValueOrDefault(name);

    public static bool IsCritical(string name) => Find(name)?.Rating == ServiceRating.Critical;
}
