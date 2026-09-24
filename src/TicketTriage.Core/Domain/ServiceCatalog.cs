namespace TicketTriage.Core.Domain;

public sealed record ServiceDefinition(string Name, ServiceRating Rating);

/// <summary>
/// The fixed catalog of "Affected Business or IT Services" (20 services, 14 of them Critical).
/// </summary>
public static class ServiceCatalog
{
    // TODO: replace the placeholder names with the exact service names from the challenge
    // specification / training data. The counts (20 total, 14 Critical) are asserted by tests.
    public static IReadOnlyList<ServiceDefinition> All { get; } =
    [
        new("TODO Critical Service 01", ServiceRating.Critical),
        new("TODO Critical Service 02", ServiceRating.Critical),
        new("TODO Critical Service 03", ServiceRating.Critical),
        new("TODO Critical Service 04", ServiceRating.Critical),
        new("TODO Critical Service 05", ServiceRating.Critical),
        new("TODO Critical Service 06", ServiceRating.Critical),
        new("TODO Critical Service 07", ServiceRating.Critical),
        new("TODO Critical Service 08", ServiceRating.Critical),
        new("TODO Critical Service 09", ServiceRating.Critical),
        new("TODO Critical Service 10", ServiceRating.Critical),
        new("TODO Critical Service 11", ServiceRating.Critical),
        new("TODO Critical Service 12", ServiceRating.Critical),
        new("TODO Critical Service 13", ServiceRating.Critical),
        new("TODO Critical Service 14", ServiceRating.Critical),
        new("TODO Non-Critical Service 01", ServiceRating.NonCritical),
        new("TODO Non-Critical Service 02", ServiceRating.NonCritical),
        new("TODO Non-Critical Service 03", ServiceRating.NonCritical),
        new("TODO Non-Critical Service 04", ServiceRating.NonCritical),
        new("TODO Non-Critical Service 05", ServiceRating.NonCritical),
        new("TODO Non-Critical Service 06", ServiceRating.NonCritical),
    ];

    private static readonly Dictionary<string, ServiceDefinition> ByName =
        All.ToDictionary(s => s.Name, StringComparer.OrdinalIgnoreCase);

    public static ServiceDefinition? Find(string name) => ByName.GetValueOrDefault(name);

    public static bool IsCritical(string name) => Find(name)?.Rating == ServiceRating.Critical;
}
