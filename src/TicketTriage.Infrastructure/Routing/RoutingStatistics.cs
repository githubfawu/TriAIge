using TicketTriage.Core.Domain;

namespace TicketTriage.Infrastructure.Routing;

/// <summary>
/// Immutable majority-vote routing table: service -> team, and (service, team) -> assignee.
/// Ties are broken alphabetically (OrdinalIgnoreCase) so the result is deterministic.
/// </summary>
internal sealed class RoutingStatistics
{
    private readonly Dictionary<string, (string Team, string? Assignee)> _routes;

    private RoutingStatistics(Dictionary<string, (string Team, string? Assignee)> routes) => _routes = routes;

    public static RoutingStatistics Empty { get; } = new(new(StringComparer.OrdinalIgnoreCase));

    public int ServiceCount => _routes.Count;

    public static RoutingStatistics Build(IEnumerable<(string Service, string Team, string? Assignee, int Count)> rows)
    {
        Dictionary<string, Dictionary<string, TeamTally>> tallies = new(StringComparer.OrdinalIgnoreCase);

        foreach (var (service, team, assignee, count) in rows)
        {
            if (string.IsNullOrWhiteSpace(service) || string.IsNullOrWhiteSpace(team) || count <= 0)
            {
                continue;
            }

            if (!tallies.TryGetValue(service, out var teams))
            {
                tallies[service] = teams = new(StringComparer.OrdinalIgnoreCase);
            }

            if (!teams.TryGetValue(team, out var tally))
            {
                teams[team] = tally = new TeamTally();
            }

            tally.Total += count;
            if (!string.IsNullOrWhiteSpace(assignee))
            {
                tally.Assignees[assignee] = tally.Assignees.GetValueOrDefault(assignee) + count;
            }
        }

        Dictionary<string, (string Team, string? Assignee)> routes = new(StringComparer.OrdinalIgnoreCase);
        foreach (var (service, teams) in tallies)
        {
            var topTeam = TopKey(teams, t => t.Total);
            var assignees = teams[topTeam].Assignees;
            routes[service] = (topTeam, assignees.Count == 0 ? null : TopKey(assignees, c => c));
        }

        return new RoutingStatistics(routes);
    }

    public bool IsKnownService(string service) => _routes.ContainsKey(service);

    public bool TryGetRoute(string service, out string team, out string? assignee)
    {
        if (_routes.TryGetValue(service, out var route))
        {
            (team, assignee) = route;
            return true;
        }

        team = string.Empty;
        assignee = null;
        return false;
    }

    /// <summary>Routes by the first listed service; an empty list or an unknown service yields no team and no assignee.</summary>
    public RoutingDecision Resolve(IReadOnlyList<string> services) =>
        services.Count > 0 && TryGetRoute(services[0], out var team, out var assignee)
            ? new RoutingDecision([team], assignee)
            : new RoutingDecision([], null);

    private static string TopKey<T>(Dictionary<string, T> items, Func<T, int> count) =>
        items.OrderByDescending(kv => count(kv.Value))
            .ThenBy(kv => kv.Key, StringComparer.OrdinalIgnoreCase)
            .First().Key;

    private sealed class TeamTally
    {
        public int Total { get; set; }

        public Dictionary<string, int> Assignees { get; } = new(StringComparer.OrdinalIgnoreCase);
    }
}
