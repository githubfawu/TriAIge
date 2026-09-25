using TicketTriage.Core.Domain;

namespace TicketTriage.Infrastructure.Routing;

/// <summary>
/// Immutable majority-vote routing table: service -> team. The assignee is not part of it, see <see cref="IAssigneeWorkload"/>.
/// Ties are broken alphabetically (OrdinalIgnoreCase) so the result is deterministic.
/// </summary>
internal sealed class RoutingStatistics
{
    private readonly Dictionary<string, string> _teams;

    private RoutingStatistics(Dictionary<string, string> teams) => _teams = teams;

    public static RoutingStatistics Empty { get; } = new(new(StringComparer.OrdinalIgnoreCase));

    public int ServiceCount => _teams.Count;

    public static RoutingStatistics Build(IEnumerable<(string Service, string Team, int Count)> rows)
    {
        Dictionary<string, Dictionary<string, int>> tallies = new(StringComparer.OrdinalIgnoreCase);

        foreach (var (service, team, count) in rows)
        {
            if (string.IsNullOrWhiteSpace(service) || string.IsNullOrWhiteSpace(team) || count <= 0)
            {
                continue;
            }

            if (!tallies.TryGetValue(service, out var teams))
            {
                tallies[service] = teams = new(StringComparer.OrdinalIgnoreCase);
            }

            teams[team] = teams.GetValueOrDefault(team) + count;
        }

        Dictionary<string, string> routes = new(StringComparer.OrdinalIgnoreCase);
        foreach (var (service, teams) in tallies)
        {
            routes[service] = teams
                .OrderByDescending(kv => kv.Value)
                .ThenBy(kv => kv.Key, StringComparer.OrdinalIgnoreCase)
                .First().Key;
        }

        return new RoutingStatistics(routes);
    }

    public bool IsKnownService(string service) => _teams.ContainsKey(service);

    public bool TryGetTeam(string service, out string team)
    {
        if (_teams.TryGetValue(service, out var found))
        {
            team = found;
            return true;
        }

        team = string.Empty;
        return false;
    }

    /// <summary>Routes by the first listed service; an empty list or an unknown service yields no team.</summary>
    public IReadOnlyList<string> ResolveTeams(IReadOnlyList<string> services) =>
        services.Count > 0 && TryGetTeam(services[0], out var team) ? [team] : [];
}
