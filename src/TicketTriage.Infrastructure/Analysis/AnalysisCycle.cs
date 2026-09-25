using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using TicketTriage.Core.Abstractions;

namespace TicketTriage.Infrastructure.Analysis;

/// <summary>One pass of the background analysis: heartbeat, gate, sweep, claim, triage and store.</summary>
public interface IAnalysisCycle
{
    /// <exception cref="OperationCanceledException">The host is shutting down; unfinished claims were released first.</exception>
    Task RunOnceAsync(CancellationToken cancellationToken);
}

internal sealed partial class AnalysisCycle(
    IServiceScopeFactory scopeFactory,
    IOptions<AnalysisOptions> options,
    ILogger<AnalysisCycle> logger) : IAnalysisCycle
{
    private static readonly TimeSpan ReleaseGrace = TimeSpan.FromSeconds(5);

    private bool _notReadyLogged;
    private bool _ownClaimsReleased;

    public async Task RunOnceAsync(CancellationToken cancellationToken)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        var store = scope.ServiceProvider.GetRequiredService<TicketClaimStore>();

        await store.BeatAsync(cancellationToken);
        if (!await store.IsDataReadyAsync(cancellationToken))
        {
            if (!_notReadyLogged)
            {
                _notReadyLogged = true;
                LogDataNotReady(logger);
            }

            return;
        }

        _notReadyLogged = false;
        if (!_ownClaimsReleased)
        {
            // Single worker process: any lease still present belongs to a previous run of this worker.
            await store.ReleaseAllAsync(cancellationToken);
            _ownClaimsReleased = true;
        }

        await store.ReleaseStaleAsync(cancellationToken);

        // Each ticket is attempted at most once per cycle: a failed one is released back to New and must not be re-claimed here.
        HashSet<int> attempted = [];
        while (true)
        {
            var claimed = await store.ClaimAsync(options.Value.BatchSize, attempted, cancellationToken);
            if (claimed.Count == 0)
            {
                return;
            }

            foreach (var item in claimed)
            {
                attempted.Add(item.Ticket.Id!.Value);
                await ProcessAsync(store, item, cancellationToken);
                await store.BeatAsync(cancellationToken);
            }
        }
    }

    private async Task ProcessAsync(TicketClaimStore store, ClaimedTicket item, CancellationToken cancellationToken)
    {
        var id = item.Ticket.Id!.Value;
        try
        {
            await using var ticketScope = scopeFactory.CreateAsyncScope();
            var pipeline = ticketScope.ServiceProvider.GetRequiredService<ITriagePipeline>();
            var suggestion = await pipeline.TriageAsync(item.Ticket, cancellationToken);
            if (!await store.SaveAsync(item, suggestion, cancellationToken))
            {
                LogLeaseLost(logger, id);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // Saved siblings are Reviewing and unaffected; everything still New under this claim goes back.
            using var grace = new CancellationTokenSource(ReleaseGrace);
            await store.ReleaseAsync(item.Claim, grace.Token);
            throw;
        }
        catch (Exception ex)
        {
            // Includes the pipeline's stop-system cancellation while the host is still running.
            LogTicketFailed(logger, id, ex.GetType().Name);
            using var grace = new CancellationTokenSource(ReleaseGrace);
            await store.ReleaseAsync(id, item.Claim, grace.Token);
        }
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Analysis paused: training data is not imported yet.")]
    private static partial void LogDataNotReady(ILogger logger);

    [LoggerMessage(Level = LogLevel.Information, Message = "Lease lost for ticket {TicketId}; suggestion discarded.")]
    private static partial void LogLeaseLost(ILogger logger, int ticketId);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Analysis of ticket {TicketId} failed ({ExceptionType}); released for retry.")]
    private static partial void LogTicketFailed(ILogger logger, int ticketId, string exceptionType);
}
