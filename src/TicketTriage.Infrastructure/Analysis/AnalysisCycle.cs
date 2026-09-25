using System.Diagnostics;
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
    TimeProvider timeProvider,
    ILogger<AnalysisCycle> logger) : IAnalysisCycle
{
    private static readonly TimeSpan ReleaseGrace = TimeSpan.FromSeconds(5);

    private bool _notReadyLogged;
    private bool _ownClaimsReleased;

    private enum Outcome
    {
        Saved,
        LeaseLost,
        Failed,
    }

    public async Task RunOnceAsync(CancellationToken cancellationToken)
    {
        var cycleStarted = Stopwatch.GetTimestamp();
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

        var swept = await store.ReleaseStaleAsync(cancellationToken);
        var housekeepingMs = ElapsedMs(cycleStarted);
        LogHousekeeping(logger, housekeepingMs, swept);

        // Each ticket is attempted at most once per cycle: a failed one is released back to New and must not be re-claimed here.
        HashSet<int> attempted = [];
        int saved = 0, leaseLost = 0, failed = 0;
        while (true)
        {
            var claimStarted = Stopwatch.GetTimestamp();
            var claimed = await store.ClaimAsync(options.Value.BatchSize, attempted, cancellationToken);
            LogClaimed(logger, claimed.Count, ElapsedMs(claimStarted));
            if (claimed.Count == 0)
            {
                break;
            }

            foreach (var item in claimed)
            {
                attempted.Add(item.Ticket.Id!.Value);
                switch (await ProcessAsync(store, item, cancellationToken))
                {
                    case Outcome.Saved:
                        saved++;
                        break;
                    case Outcome.LeaseLost:
                        leaseLost++;
                        break;
                    default:
                        failed++;
                        break;
                }

                await store.BeatAsync(cancellationToken);
            }
        }

        // Idle cycles run every IntervalSeconds, so they stay at Debug to keep the log readable.
        if (attempted.Count == 0)
        {
            LogIdleCycle(logger, ElapsedMs(cycleStarted));
        }
        else
        {
            LogCycleFinished(logger, attempted.Count, saved, failed, leaseLost, ElapsedMs(cycleStarted), housekeepingMs);
        }
    }

    private async Task<Outcome> ProcessAsync(TicketClaimStore store, ClaimedTicket item, CancellationToken cancellationToken)
    {
        var id = item.Ticket.Id!.Value;
        var queueMs = item.IngestedAt is { } ingestedAt
            ? (long)(timeProvider.GetUtcNow().UtcDateTime - ingestedAt).TotalMilliseconds
            : (long?)null;
        LogTicketStarted(logger, id, queueMs);
        var started = Stopwatch.GetTimestamp();
        try
        {
            await using var ticketScope = scopeFactory.CreateAsyncScope();
            var pipeline = ticketScope.ServiceProvider.GetRequiredService<ITriagePipeline>();
            var suggestion = await pipeline.TriageAsync(item.Ticket, cancellationToken);
            var pipelineMs = ElapsedMs(started);

            var saveStarted = Stopwatch.GetTimestamp();
            var stored = await store.SaveAsync(item, suggestion, cancellationToken);
            var saveMs = ElapsedMs(saveStarted);
            if (!stored)
            {
                LogLeaseLost(logger, id);
                return Outcome.LeaseLost;
            }

            LogTicketFinished(logger, id, ElapsedMs(started), queueMs, pipelineMs, saveMs, suggestion.IsFallback);
            return Outcome.Saved;
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
            LogTicketFailed(logger, id, ex.GetType().Name, ElapsedMs(started));
            using var grace = new CancellationTokenSource(ReleaseGrace);
            await store.ReleaseAsync(id, item.Claim, grace.Token);
            return Outcome.Failed;
        }
    }

    private static long ElapsedMs(long started) => (long)Stopwatch.GetElapsedTime(started).TotalMilliseconds;

    [LoggerMessage(Level = LogLevel.Information, Message = "Analysis paused: training data is not imported yet.")]
    private static partial void LogDataNotReady(ILogger logger);

    [LoggerMessage(Level = LogLevel.Debug, Message = "Analysis housekeeping (heartbeat, readiness, lease sweep) took {ElapsedMs} ms; {Swept} stale lease(s) released.")]
    private static partial void LogHousekeeping(ILogger logger, long elapsedMs, int swept);

    [LoggerMessage(Level = LogLevel.Debug, Message = "Claimed {Count} ticket(s) in {ElapsedMs} ms.")]
    private static partial void LogClaimed(ILogger logger, int count, long elapsedMs);

    [LoggerMessage(Level = LogLevel.Information, Message = "Analysis of ticket {TicketId} started; queued {QueueMs} ms since ingest.")]
    private static partial void LogTicketStarted(ILogger logger, int ticketId, long? queueMs);

    [LoggerMessage(
        Level = LogLevel.Information,
        Message = "Analysis of ticket {TicketId} finished in {ElapsedMs} ms (queued {QueueMs} ms before, pipeline {PipelineMs} ms, save {SaveMs} ms, fallback {IsFallback}).")]
    private static partial void LogTicketFinished(
        ILogger logger, int ticketId, long elapsedMs, long? queueMs, long pipelineMs, long saveMs, bool isFallback);

    [LoggerMessage(Level = LogLevel.Debug, Message = "Analysis cycle idle: nothing to analyse ({ElapsedMs} ms).")]
    private static partial void LogIdleCycle(ILogger logger, long elapsedMs);

    [LoggerMessage(
        Level = LogLevel.Information,
        Message = "Analysis cycle finished: {Attempted} ticket(s) in {ElapsedMs} ms ({Saved} saved, {Failed} failed, {LeaseLost} lease lost; housekeeping {HousekeepingMs} ms).")]
    private static partial void LogCycleFinished(
        ILogger logger, int attempted, int saved, int failed, int leaseLost, long elapsedMs, long housekeepingMs);

    [LoggerMessage(Level = LogLevel.Information, Message = "Lease lost for ticket {TicketId}; suggestion discarded.")]
    private static partial void LogLeaseLost(ILogger logger, int ticketId);

    [LoggerMessage(Level = LogLevel.Warning, Message = "Analysis of ticket {TicketId} failed ({ExceptionType}) after {ElapsedMs} ms; released for retry.")]
    private static partial void LogTicketFailed(ILogger logger, int ticketId, string exceptionType, long elapsedMs);
}
