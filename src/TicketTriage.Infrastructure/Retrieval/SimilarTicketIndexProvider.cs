using System.Diagnostics;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using TicketTriage.Core.Domain;
using TicketTriage.Infrastructure.Persistence;

namespace TicketTriage.Infrastructure.Retrieval;

/// <summary>
/// Builds the <see cref="TfIdfIndex"/> once, lazily, on the first call. A cancelled or failed build publishes
/// nothing, so the next caller retries (a cached Task would keep the first caller's cancellation forever).
/// </summary>
internal sealed class SimilarTicketIndexProvider : IDisposable
{
    private readonly Func<CancellationToken, Task<IReadOnlyList<CorpusDocument>>> _load;
    private readonly ILogger<SimilarTicketIndexProvider> _logger;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private volatile TfIdfIndex? _index;

    public SimilarTicketIndexProvider(IDbContextFactory<TriageDbContext> dbFactory, ILogger<SimilarTicketIndexProvider> logger)
        : this(ct => LoadCorpusAsync(dbFactory, ct), logger)
    {
    }

    internal SimilarTicketIndexProvider(
        Func<CancellationToken, Task<IReadOnlyList<CorpusDocument>>> load,
        ILogger<SimilarTicketIndexProvider> logger)
    {
        _load = load;
        _logger = logger;
    }

    public async ValueTask<TfIdfIndex> GetAsync(CancellationToken cancellationToken)
    {
        if (_index is { } existing)
        {
            return existing;
        }

        await _gate.WaitAsync(cancellationToken);
        try
        {
            if (_index is { } built)
            {
                return built;
            }

            var started = Stopwatch.GetTimestamp();
            var documents = await _load(cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            var index = await Task.Run(() => TfIdfIndex.Build(documents), cancellationToken);
            _index = index;

            _logger.LogInformation(
                "Built similar-ticket index: {DocumentCount} documents, {TermCount} terms in {ElapsedMs} ms",
                index.DocumentCount,
                index.TermCount,
                (long)Stopwatch.GetElapsedTime(started).TotalMilliseconds);
            return index;
        }
        finally
        {
            _gate.Release();
        }
    }

    public void Dispose() => _gate.Dispose();

    private static async Task<IReadOnlyList<CorpusDocument>> LoadCorpusAsync(
        IDbContextFactory<TriageDbContext> dbFactory,
        CancellationToken cancellationToken)
    {
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var rows = await db.Tickets.AsNoTracking()
            .Where(t => t.Origin == TicketOrigin.Training)
            .Where(t => t.Description != null && t.Description != "")
            .OrderBy(t => t.Id)
            .Select(t => new CorpusDocument(t.Id, t.Description!))
            .ToListAsync(cancellationToken);

        // SQLite trim() only strips spaces, so whitespace-only descriptions are filtered here.
        return [.. rows.Where(r => !string.IsNullOrWhiteSpace(r.Text))];
    }
}
