using Microsoft.EntityFrameworkCore;
using TicketTriage.Core.Domain;
using TicketTriage.Infrastructure.Persistence;

namespace TicketTriage.Infrastructure.Routing;

/// <summary>
/// Assigns tickets to the person with the fewest tickets so far. The assignee is not predictable from the ticket
/// (docs/features/score-completeness), so it is spread evenly instead of voted.
/// </summary>
internal interface IAssigneeWorkload
{
    /// <summary>The assignee with the fewest tickets (ties alphabetical), without changing the counts; null when nobody is known yet.</summary>
    ValueTask<string?> PeekLeastLoadedAsync(CancellationToken cancellationToken);

    /// <summary>Counts one more ticket for <paramref name="assignee"/>; a null or unknown name is ignored.</summary>
    ValueTask ReserveAsync(string? assignee, CancellationToken cancellationToken);
}

/// <summary>
/// Counts are loaded once per process (training tickets plus stored suggestions) and then kept in memory: every
/// suggestion the pipeline issues counts immediately, so consecutive tickets go to different people. After a restart the
/// stored suggestions are counted again; reservations of an unfinished run are lost.
/// </summary>
internal sealed class AssigneeWorkloadProvider : IAssigneeWorkload, IDisposable
{
    private readonly Func<CancellationToken, Task<IReadOnlyDictionary<string, int>>> _load;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly Lock _sync = new();
    private Dictionary<string, int>? _tickets;

    public AssigneeWorkloadProvider(IDbContextFactory<TriageDbContext> dbFactory)
        : this(ct => LoadAsync(dbFactory, ct))
    {
    }

    internal AssigneeWorkloadProvider(Func<CancellationToken, Task<IReadOnlyDictionary<string, int>>> load) => _load = load;

    public async ValueTask<string?> PeekLeastLoadedAsync(CancellationToken cancellationToken)
    {
        var tickets = await EnsureLoadedAsync(cancellationToken);
        lock (_sync)
        {
            return tickets.Count == 0
                ? null
                : tickets.OrderBy(kv => kv.Value).ThenBy(kv => kv.Key, StringComparer.OrdinalIgnoreCase).First().Key;
        }
    }

    public async ValueTask ReserveAsync(string? assignee, CancellationToken cancellationToken)
    {
        var tickets = await EnsureLoadedAsync(cancellationToken);
        lock (_sync)
        {
            if (assignee is not null && tickets.TryGetValue(assignee, out var count))
            {
                tickets[assignee] = count + 1;
            }
        }
    }

    public void Dispose() => _gate.Dispose();

    // A cancelled or failed load publishes nothing, so the next caller retries; an empty result is not cached either.
    private async ValueTask<Dictionary<string, int>> EnsureLoadedAsync(CancellationToken cancellationToken)
    {
        if (_tickets is { } existing)
        {
            return existing;
        }

        await _gate.WaitAsync(cancellationToken);
        try
        {
            if (_tickets is { } built)
            {
                return built;
            }

            var loaded = new Dictionary<string, int>(await _load(cancellationToken), StringComparer.OrdinalIgnoreCase);
            if (loaded.Count > 0)
            {
                _tickets = loaded;
            }

            return loaded;
        }
        finally
        {
            _gate.Release();
        }
    }

    // The candidates are the assignees of the training tickets; suggestions only add to their counts.
    private static async Task<IReadOnlyDictionary<string, int>> LoadAsync(
        IDbContextFactory<TriageDbContext> dbFactory,
        CancellationToken cancellationToken)
    {
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var training = await db.Tickets.AsNoTracking()
            .Where(t => t.Origin == TicketOrigin.Training && t.Assignee != null)
            .GroupBy(t => t.Assignee!)
            .Select(g => new { Assignee = g.Key, Count = g.Count() })
            .ToListAsync(cancellationToken);
        var suggested = await db.Suggestions.AsNoTracking()
            .Where(s => s.Assignee != null)
            .GroupBy(s => s.Assignee!)
            .Select(g => new { Assignee = g.Key, Count = g.Count() })
            .ToListAsync(cancellationToken);

        Dictionary<string, int> counts = new(StringComparer.OrdinalIgnoreCase);
        foreach (var row in training.Where(r => !string.IsNullOrWhiteSpace(r.Assignee)))
        {
            counts[row.Assignee] = row.Count;
        }

        foreach (var row in suggested.Where(r => counts.ContainsKey(r.Assignee)))
        {
            counts[row.Assignee] += row.Count;
        }

        return counts;
    }
}
