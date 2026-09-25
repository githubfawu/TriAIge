using System.Diagnostics;
using Microsoft.Extensions.Options;
using TicketTriage.Core.Abstractions;
using TicketTriage.Core.Domain;
using TicketTriage.Infrastructure.Analysis;
using TicketTriage.Infrastructure.Challenge;

namespace TicketTriage.Web.Upload;

/// <summary>
/// Parses an uploaded challenge file, ingests it and reports the analysis progress; never calls the LLM. Ticket text is
/// never logged (personal data).
/// </summary>
public sealed class ChallengeUploadService(
    IServiceScopeFactory scopeFactory,
    IAnalysisMonitor monitor,
    IFallbackSuggestionProvider fallbackProvider,
    IOptions<UploadOptions> options,
    TimeProvider timeProvider,
    ILogger<ChallengeUploadService> logger)
{
    public async Task<UploadSession> UploadAsync(Stream json, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(json);

        var started = Stopwatch.GetTimestamp();
        var document = await ChallengeDocument.ReadAsync(json, cancellationToken);
        var parseMs = ElapsedMs(started);

        var tickets = document.Tickets;
        IReadOnlyList<IngestResult> ingested;
        var ingestStarted = Stopwatch.GetTimestamp();
        await using (var scope = scopeFactory.CreateAsyncScope())
        {
            var ingestor = scope.ServiceProvider.GetRequiredService<ITicketIngestor>();
            ingested = await ingestor.IngestAsync(tickets, TicketOrigin.Challenge, cancellationToken);
        }

        var ingestMs = ElapsedMs(ingestStarted);

        if (ingested.Count != tickets.Count)
        {
            throw new InvalidOperationException($"Ingestor returned {ingested.Count} results for {tickets.Count} tickets.");
        }

        var counts = new IngestCounts(
            ingested.Count(r => r.Outcome == IngestOutcome.Created),
            ingested.Count(r => r.Outcome == IngestOutcome.Updated),
            ingested.Count(r => r.Outcome == IngestOutcome.Unchanged),
            ingested.Count(r => r.Outcome == IngestOutcome.Locked));
        logger.LogInformation(
            "Ingested {Count} uploaded tickets ({Created} created, {Updated} updated, {Unchanged} unchanged, {Locked} locked); "
                + "parse {ParseMs} ms, ingest {IngestMs} ms.",
            ingested.Count, counts.Created, counts.Updated, counts.Unchanged, counts.Locked, parseMs, ingestMs);

        return new UploadSession(
            document,
            [.. ingested.Select(r => r.TicketId)],
            counts,
            document.DuplicateKeys,
            timeProvider.GetUtcNow());
    }

    public async Task<UploadProgress> GetProgressAsync(UploadSession session, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(session);
        var settings = options.Value;

        var distinctIds = session.TicketIds.Distinct().ToList();
        var states = await monitor.GetStatesAsync(distinctIds, cancellationToken);
        var pending = states.Count(s => s.IsPending);
        var fallbacks = states.Count(s => !s.IsPending && s.Suggestion is { IsFallback: true });

        var now = timeProvider.GetUtcNow();
        var elapsed = now - session.StartedAt;
        var heartbeat = await monitor.GetWorkerHeartbeatAsync(cancellationToken);
        var alive = WorkerLiveness.IsAlive(heartbeat, now, TimeSpan.FromSeconds(settings.WorkerHeartbeatMaxAgeSeconds));

        var worker = alive
            ? WorkerStatus.Alive
            : elapsed >= TimeSpan.FromSeconds(settings.WorkerStartGraceSeconds) ? WorkerStatus.NotRunning : WorkerStatus.Starting;
        var timedOut = pending > 0 && alive && elapsed >= TimeSpan.FromSeconds(settings.WaitTimeoutSeconds);

        return new UploadProgress(distinctIds.Count, distinctIds.Count - pending, pending, fallbacks, worker, timedOut);
    }

    /// <summary>Builds result.json from the stored suggestions; tickets still pending get the deterministic fallback.</summary>
    public async Task<UploadExport> ExportAsync(UploadSession session, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(session);

        var started = Stopwatch.GetTimestamp();
        var states = await monitor.GetStatesAsync([.. session.TicketIds.Distinct()], cancellationToken);
        var built = await ChallengeResults.BuildAsync(session.TicketIds, states, fallbackProvider, cancellationToken);
        var output = session.Document.ToOutput([.. built.Rows.Select(r => r.Result)]);

        await using var buffer = new MemoryStream();
        await ChallengeDocument.WriteAsync(output, buffer, cancellationToken);
        logger.LogInformation(
            "Exported {Count} uploaded tickets in {ElapsedMs} ms ({NotAnalysed} not analysed, {Fallbacks} fallbacks); "
                + "{SinceUploadMs} ms since upload.",
            built.Rows.Count,
            ElapsedMs(started),
            built.NotAnalysed,
            built.Fallbacks,
            (long)(timeProvider.GetUtcNow() - session.StartedAt).TotalMilliseconds);
        return new UploadExport(built.Rows, buffer.ToArray(), built.Fallbacks, built.NotAnalysed);
    }

    private static long ElapsedMs(long started) => (long)Stopwatch.GetElapsedTime(started).TotalMilliseconds;
}
