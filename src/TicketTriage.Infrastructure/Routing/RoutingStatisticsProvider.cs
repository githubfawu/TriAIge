using System.Diagnostics;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using TicketTriage.Core.Domain;
using TicketTriage.Infrastructure.Persistence;
using TicketTriage.Infrastructure.Sources;

namespace TicketTriage.Infrastructure.Routing;

internal interface IRoutingStatisticsSource
{
    ValueTask<RoutingStatistics> GetAsync(CancellationToken cancellationToken);
}

/// <summary>
/// Builds <see cref="RoutingStatistics"/> once per process from one grouped query. A cancelled or failed load
/// publishes nothing, so the next caller retries.
/// </summary>
internal sealed class RoutingStatisticsProvider : IRoutingStatisticsSource, IDisposable
{
    private readonly Func<CancellationToken, Task<IReadOnlyList<(string Service, string Team, int Count)>>> _load;
    private readonly ILogger<RoutingStatisticsProvider> _logger;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private volatile RoutingStatistics? _statistics;

    public RoutingStatisticsProvider(
        IDbContextFactory<TriageDbContext> dbFactory,
        LookupNamesProvider lookupNames,
        ILogger<RoutingStatisticsProvider> logger)
        : this(ct => LoadRowsAsync(dbFactory, lookupNames, ct), logger)
    {
    }

    internal RoutingStatisticsProvider(
        Func<CancellationToken, Task<IReadOnlyList<(string Service, string Team, int Count)>>> load,
        ILogger<RoutingStatisticsProvider> logger)
    {
        _load = load;
        _logger = logger;
    }

    public async ValueTask<RoutingStatistics> GetAsync(CancellationToken cancellationToken)
    {
        if (_statistics is { } existing)
        {
            return existing;
        }

        await _gate.WaitAsync(cancellationToken);
        try
        {
            if (_statistics is { } built)
            {
                return built;
            }

            var started = Stopwatch.GetTimestamp();
            var rows = await _load(cancellationToken);
            var statistics = RoutingStatistics.Build(rows);
            if (rows.Count == 0)
            {
                // Not cached: an empty result usually means the training data is not imported yet.
                _logger.LogWarning("Routing statistics empty: training data not imported?");
                return statistics;
            }

            _statistics = statistics;

            _logger.LogInformation(
                "Built routing statistics: {GroupCount} groups, {ServiceCount} services in {ElapsedMs} ms",
                rows.Count,
                statistics.ServiceCount,
                (long)Stopwatch.GetElapsedTime(started).TotalMilliseconds);
            return statistics;
        }
        finally
        {
            _gate.Release();
        }
    }

    public void Dispose() => _gate.Dispose();

    private static async Task<IReadOnlyList<(string Service, string Team, int Count)>> LoadRowsAsync(
        IDbContextFactory<TriageDbContext> dbFactory,
        LookupNamesProvider lookupNames,
        CancellationToken cancellationToken)
    {
        var names = await lookupNames.GetAsync(cancellationToken);
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var groups = await db.Tickets.AsNoTracking()
            .Where(t => t.Origin == TicketOrigin.Training)
            .Where(t => t.AffectedBusinessOrITServiceId != null && t.ServiceTeamId != null)
            .GroupBy(t => new { t.AffectedBusinessOrITServiceId, t.ServiceTeamId })
            .Select(g => new { g.Key.AffectedBusinessOrITServiceId, g.Key.ServiceTeamId, Count = g.Count() })
            .OrderBy(g => g.AffectedBusinessOrITServiceId)
            .ThenBy(g => g.ServiceTeamId)
            .ToListAsync(cancellationToken);

        List<(string Service, string Team, int Count)> rows = [];
        foreach (var g in groups)
        {
            if (names.AffectedBusinessOrITServices.TryGetValue(g.AffectedBusinessOrITServiceId!.Value, out var service)
                && names.ServiceTeams.TryGetValue(g.ServiceTeamId!.Value, out var team))
            {
                rows.Add((service, team, g.Count));
            }
        }

        // Ids order the SQL result; names decide the final order so it is independent of lookup id assignment.
        return rows
            .OrderBy(r => r.Service, StringComparer.Ordinal)
            .ThenBy(r => r.Team, StringComparer.Ordinal)
            .ToList();
    }
}
