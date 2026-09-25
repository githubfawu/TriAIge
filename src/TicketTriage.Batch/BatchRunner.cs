using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using TicketTriage.Core.Abstractions;
using TicketTriage.Core.Domain;
using TicketTriage.Infrastructure.Analysis;
using TicketTriage.Infrastructure.Challenge;

namespace TicketTriage.Batch;

/// <summary>
/// Ingests the challenge tickets, waits for the Web analysis worker to store its suggestions and writes the result file
/// for scoring. Never calls the LLM itself.
/// </summary>
public sealed class BatchRunner(
    IOptions<BatchOptions> options,
    ITicketIngestor ingestor,
    IAnalysisMonitor monitor,
    IFallbackSuggestionProvider fallbackProvider,
    TimeProvider timeProvider,
    ILogger<BatchRunner> logger)
{
    public async Task<BatchSummary> RunAsync(CancellationToken cancellationToken)
    {
        var started = timeProvider.GetTimestamp();
        var (input, output) = Prepare();
        var settings = options.Value;

        logger.LogInformation("Reading challenge tickets from {Input}.", input);
        var document = await ChallengeFile.ReadAsync(input, cancellationToken);
        var tickets = document.Tickets;

        foreach (var duplicate in document.DuplicateKeys)
        {
            logger.LogWarning("Duplicate issue key {Key} occurs more than once in the input.", duplicate);
        }

        var ingested = await ingestor.IngestAsync(tickets, TicketOrigin.Challenge, cancellationToken);
        if (ingested.Count != tickets.Count)
        {
            throw new InvalidOperationException($"Ingestor returned {ingested.Count} results for {tickets.Count} tickets.");
        }

        var ids = ingested.Select(r => r.TicketId).ToList();
        var distinctIds = ids.Distinct().ToList();
        logger.LogInformation(
            "Ingested {Count} tickets ({Created} created, {Updated} updated, {Unchanged} unchanged, {Locked} locked).",
            ids.Count,
            ingested.Count(r => r.Outcome == IngestOutcome.Created),
            ingested.Count(r => r.Outcome == IngestOutcome.Updated),
            ingested.Count(r => r.Outcome == IngestOutcome.Unchanged),
            ingested.Count(r => r.Outcome == IngestOutcome.Locked));

        var states = await WaitForAnalysisAsync(distinctIds, settings, cancellationToken);
        var built = await ChallengeResults.BuildAsync(ids, states, fallbackProvider, cancellationToken);
        var results = built.Rows.Select(r => r.Result).ToList();
        var fallbacks = built.Fallbacks;
        var notAnalysed = built.NotAnalysed;

        cancellationToken.ThrowIfCancellationRequested();
        await WriteAtomicallyAsync(output, document.ToOutput(results), cancellationToken);

        var duration = timeProvider.GetElapsedTime(started);
        logger.LogInformation(
            "Wrote {Count} results ({Fallbacks} fallback, {NotAnalysed} not analysed) to {Output} in {Duration}.",
            results.Count, fallbacks, notAnalysed, output, duration);
        return new BatchSummary(results.Count, fallbacks, duration, output, notAnalysed);
    }

    // Returns when nothing is pending, or when a live worker ran out of time (pending states stay pending then).
    private async Task<IReadOnlyList<AnalysisState>> WaitForAnalysisAsync(
        IReadOnlyList<int> ids,
        BatchOptions settings,
        CancellationToken cancellationToken)
    {
        var waitStart = timeProvider.GetUtcNow();
        var poll = TimeSpan.FromSeconds(settings.PollIntervalSeconds);

        while (true)
        {
            var states = await monitor.GetStatesAsync(ids, cancellationToken);
            var pending = states.Count(s => s.IsPending);
            if (pending == 0)
            {
                return states;
            }

            var now = timeProvider.GetUtcNow();
            var elapsed = now - waitStart;
            var heartbeat = await monitor.GetWorkerHeartbeatAsync(cancellationToken);
            var alive = WorkerLiveness.IsAlive(heartbeat, now, TimeSpan.FromSeconds(settings.WorkerHeartbeatMaxAgeSeconds));

            if (!alive && elapsed >= TimeSpan.FromSeconds(settings.WorkerStartGraceSeconds))
            {
                throw new BatchWorkerUnavailableException(
                    "Analysis worker is not running - start TicketTriage.Web (aspire run) and run the batch again; no output written.");
            }

            if (alive && elapsed >= TimeSpan.FromSeconds(settings.WaitTimeoutSeconds))
            {
                logger.LogWarning(
                    "Timed out after {Elapsed} with {Pending} ticket(s) still not analysed; exporting them with the deterministic fallback.",
                    elapsed, pending);
                return states;
            }

            logger.LogInformation("Waiting for analysis: {Pending} of {Total} ticket(s) pending.", pending, states.Count);
            await Task.Delay(poll, timeProvider, cancellationToken);
        }
    }

    /// <summary>
    /// Validates the options, resolves the paths and proves the output location is writable, so a bad setup fails
    /// before any database work or LLM call. Throws <see cref="OptionsValidationException"/> or <see cref="BatchInputException"/>.
    /// </summary>
    public (string Input, string Output) Prepare()
    {
        var configured = options.Value;
        string input;
        string output;
        try
        {
            input = Path.GetFullPath(configured.Input);
            output = Path.GetFullPath(configured.Output);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            throw new BatchInputException($"Invalid input or output path ({ex.GetType().Name}).", ex);
        }

        var comparison = OperatingSystem.IsWindows() || OperatingSystem.IsMacOS()
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;
        if (string.Equals(input, output, comparison))
        {
            throw new BatchInputException("Input and output must be different files; the input would be overwritten.");
        }

        VerifyOutputWritable(output);
        return (input, output);
    }

    private static void VerifyOutputWritable(string output)
    {
        var directory = Path.GetDirectoryName(output);
        if (string.IsNullOrEmpty(directory) || Directory.Exists(output))
        {
            throw new BatchInputException($"Output path '{output}' is not a writable file location.");
        }

        var probe = Path.Combine(directory, $".write-probe-{Guid.NewGuid():N}.tmp");
        try
        {
            Directory.CreateDirectory(directory);
            File.WriteAllBytes(probe, []);
            File.Delete(probe);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException or ArgumentException)
        {
            throw new BatchInputException($"Cannot write to output directory '{directory}' ({ex.GetType().Name}).", ex);
        }
    }

    internal static bool IsFallback(TriageSuggestion suggestion) => suggestion.IsFallback;

    // The temp file lives next to the target so File.Move is a same-volume rename and never leaves a half-written result.json.
    private static async Task WriteAtomicallyAsync(
        string output,
        JsonNode content,
        CancellationToken cancellationToken)
    {
        var directory = Path.GetDirectoryName(output)
            ?? throw new BatchInputException($"Output path '{output}' is not a file location.");
        Directory.CreateDirectory(directory);
        var temp = Path.Combine(directory, $"{Path.GetFileName(output)}.{Guid.NewGuid():N}.tmp");
        try
        {
            await using (var stream = File.Create(temp))
            {
                await ChallengeDocument.WriteAsync(content, stream, cancellationToken);
            }

            File.Move(temp, output, overwrite: true);
        }
        finally
        {
            File.Delete(temp);
        }
    }
}
