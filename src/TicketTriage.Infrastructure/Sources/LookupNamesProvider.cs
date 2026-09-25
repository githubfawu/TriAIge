using Microsoft.EntityFrameworkCore;
using TicketTriage.Infrastructure.Persistence;

namespace TicketTriage.Infrastructure.Sources;

/// <summary>
/// Loads the seeded, static lookup tables once and caches them. A failed or cancelled load publishes nothing,
/// so the next caller retries.
/// </summary>
internal sealed class LookupNamesProvider(IDbContextFactory<TriageDbContext> dbFactory) : IDisposable
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private volatile LookupNames? _names;

    public async ValueTask<LookupNames> GetAsync(CancellationToken cancellationToken)
    {
        if (_names is { } existing)
        {
            return existing;
        }

        await _gate.WaitAsync(cancellationToken);
        try
        {
            if (_names is { } loaded)
            {
                return loaded;
            }

            await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
            var names = await LookupNames.LoadAsync(db, cancellationToken);
            _names = names;
            return names;
        }
        finally
        {
            _gate.Release();
        }
    }

    public void Dispose() => _gate.Dispose();
}
