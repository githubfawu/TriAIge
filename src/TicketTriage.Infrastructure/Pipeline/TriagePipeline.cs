using System.Diagnostics;
using System.Runtime.CompilerServices;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using TicketTriage.Core.Abstractions;
using TicketTriage.Core.Domain;
using TicketTriage.Infrastructure.Routing;

namespace TicketTriage.Infrastructure.Pipeline;

internal sealed class TriagePipeline(
    TicketNormalizer normalizer,
    ISimilarTicketSource similarSource,
    ITicketClassifier classifier,
    IRoutingResolver routingResolver,
    IRoutingStatisticsSource statisticsSource,
    IResolutionDrafter drafter,
    IOptions<TriageOptions> options,
    ITriageFailureStore failureStore,
    ILogger<TriagePipeline> logger,
    IHostApplicationLifetime? lifetime = null) : ITriagePipeline
{
    private const int MaxStackTraceLength = 4000;

    public Task<TriageSuggestion> TriageAsync(Ticket ticket, CancellationToken cancellationToken) =>
        TriageOneAsync(ticket, cancellationToken);

    public async IAsyncEnumerable<TriageSuggestion> TriageAsync(
        IAsyncEnumerable<Ticket> tickets,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        await foreach (var ticket in tickets.WithCancellation(cancellationToken))
        {
            yield return await TriageOneAsync(ticket, cancellationToken);
        }
    }

    // Throws OperationCanceledException when the system is being stopped (StopSystemOnFailure),
    // so callers can tell "stopped" from "exhausted".
    private async Task<TriageSuggestion> TriageOneAsync(Ticket ticket, CancellationToken outerToken)
    {
        var settings = options.Value;
        Ticket? normalized = null;
        IReadOnlyList<SimilarTicket>? similar = null;
        var attempt = 0;
        var ticketStarted = Stopwatch.GetTimestamp();

        while (true)
        {
            attempt++;
            var step = "Normalize";
            var attemptStarted = Stopwatch.GetTimestamp();
            var lap = attemptStarted;
            try
            {
                using var cts = CancellationTokenSource.CreateLinkedTokenSource(outerToken);
                cts.CancelAfter(TimeSpan.FromSeconds(settings.TicketTimeoutSeconds));
                var token = cts.Token;

                normalized ??= normalizer.Normalize(ticket).Ticket;
                StepDone(ticket, attempt, step, ref lap);

                step = "Similar";
                similar ??= await similarSource.FindSimilarAsync(normalized, settings.SimilarTicketCount, token);
                var similarMs = StepDone(ticket, attempt, step, ref lap);

                step = "Classify";
                var classification = await classifier.ClassifyAsync(normalized, similar, token);
                var classifyMs = StepDone(ticket, attempt, step, ref lap);

                step = "Route";
                var routing = await routingResolver.ResolveAsync(normalized, classification, similar, token);
                var routeMs = StepDone(ticket, attempt, step, ref lap);

                step = "Draft";
                var draft = await drafter.DraftAsync(normalized, classification, routing, similar, token);
                var draftMs = StepDone(ticket, attempt, step, ref lap);

                step = "Validate";
                var suggestion = new TriageSuggestion
                {
                    TicketKey = ticket.Key,
                    WorkType = classification.WorkType,
                    AffectedServices = classification.AffectedServices,
                    ServiceTeams = routing.ServiceTeams,
                    Assignee = routing.Assignee,
                    Urgency = classification.Urgency,
                    Impact = classification.Impact,
                    ResolutionStatus = draft.Status,
                    DraftComment = draft.Comment,
                    SimilarTicketKeys = [.. similar.Select(s => s.Ticket.Key)],
                };
                SuggestionValidator.Validate(suggestion, await statisticsSource.GetAsync(token));
                var validateMs = StepDone(ticket, attempt, step, ref lap);

                await ResetRetriesAsync(ticket, outerToken);
                logger.LogInformation(
                    "Triage of {TicketKey} succeeded on attempt {Attempt} in {ElapsedMs} ms (ticket total {TotalMs} ms): "
                        + "similar {SimilarMs} ms, classify {ClassifyMs} ms, route {RouteMs} ms, draft {DraftMs} ms, validate {ValidateMs} ms",
                    ticket.Key,
                    attempt,
                    ElapsedMs(attemptStarted),
                    ElapsedMs(ticketStarted),
                    similarMs,
                    classifyMs,
                    routeMs,
                    draftMs,
                    validateMs);
                return suggestion;
            }
            catch (OperationCanceledException) when (outerToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                var reason = Describe(step, ex);
                logger.LogInformation(
                    "Triage attempt {Attempt} for {TicketKey} stopped in step {Step} after {ElapsedMs} ms (step {StepMs} ms)",
                    attempt, ticket.Key, step, ElapsedMs(attemptStarted), ElapsedMs(lap));
                var persisted = await RecordAsync(ticket, attempt, reason, ex, outerToken);
                var exhausted = Math.Max(attempt, persisted ?? 0) >= settings.RetryCount;

                // Persisted retries at the limit mean an earlier run already stopped on this ticket;
                // stopping again would re-trigger the stop after every restart.
                if (settings.StopSystemOnFailure && (persisted ?? 0) < settings.RetryCount)
                {
                    logger.LogError(
                        "Triage of {TicketKey} failed on attempt {Attempt} ({Reason}); StopSystemOnFailure is set, stopping the application",
                        ticket.Key, attempt, reason);
                    lifetime?.StopApplication();
                    throw new OperationCanceledException("Triage stopped: StopSystemOnFailure is enabled and a ticket failed.");
                }

                if (exhausted)
                {
                    logger.LogWarning(
                        "Triage of {TicketKey} failed after attempt {Attempt} ({Reason}) and {TotalMs} ms; using fallback",
                        ticket.Key, attempt, reason, ElapsedMs(ticketStarted));
                    return FallbackSuggestionFactory.Create(
                        normalized ?? ticket,
                        similar ?? [],
                        await LoadStatisticsForFallbackAsync(ticket, settings, outerToken),
                        logger);
                }

                if (settings.RetryDelayMilliseconds > 0)
                {
                    await Task.Delay(settings.RetryDelayMilliseconds, outerToken);
                }
            }
        }
    }

    private static long ElapsedMs(long started) => (long)Stopwatch.GetElapsedTime(started).TotalMilliseconds;

    /// <summary>Logs the step at Debug (the timeline) and returns its duration; <paramref name="lap"/> moves to now.</summary>
    private long StepDone(Ticket ticket, int attempt, string step, ref long lap)
    {
        var now = Stopwatch.GetTimestamp();
        var elapsedMs = (long)Stopwatch.GetElapsedTime(lap, now).TotalMilliseconds;
        lap = now;
        logger.LogDebug("Triage of {TicketKey} attempt {Attempt}: {Step} took {StepMs} ms", ticket.Key, attempt, step, elapsedMs);
        return elapsedMs;
    }

    // A statistics failure must not break the fallback: it then carries no routing, which the validator treats as consistent.
    private async Task<RoutingStatistics> LoadStatisticsForFallbackAsync(Ticket ticket, TriageOptions settings, CancellationToken outerToken)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(outerToken);
        cts.CancelAfter(TimeSpan.FromSeconds(settings.TicketTimeoutSeconds));
        try
        {
            return await statisticsSource.GetAsync(cts.Token);
        }
        catch (OperationCanceledException) when (outerToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            logger.LogWarning("Fallback routing statistics unavailable for {TicketKey}: {ExceptionType}", ticket.Key, ex.GetType().FullName);
            return RoutingStatistics.Empty;
        }
    }

    // Never includes ex.Message: it may echo ticket text.
    private static string Describe(string step, Exception ex) => ex switch
    {
        TriageValidationException v => $"{step}:{string.Join(',', v.Codes)}",
        OperationCanceledException => $"{step}:Timeout",
        _ => $"{step}:Exception",
    };

    // Frames only (no messages, also for inner exceptions): messages may echo ticket text (personal data).
    private static string? FramesOnly(Exception ex)
    {
        List<string> frames = [];
        for (var current = ex; current is not null; current = current.InnerException)
        {
            if (current.StackTrace is { } trace)
            {
                frames.Add(trace);
            }
        }

        if (frames.Count == 0)
        {
            return null;
        }

        var joined = string.Join(Environment.NewLine + "--- inner ---" + Environment.NewLine, frames);
        return joined.Length <= MaxStackTraceLength ? joined : joined[..MaxStackTraceLength];
    }

    private async Task ResetRetriesAsync(Ticket ticket, CancellationToken outerToken)
    {
        if (ticket.Id is not { } id)
        {
            return;
        }

        try
        {
            await failureStore.ResetRetriesAsync(id, outerToken);
        }
        catch (OperationCanceledException) when (outerToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            logger.LogWarning("Could not reset retries for {TicketKey}: {ExceptionType}", ticket.Key, ex.GetType().FullName);
        }
    }

    private async Task<int?> RecordAsync(Ticket ticket, int attempt, string reason, Exception ex, CancellationToken outerToken)
    {
        logger.LogWarning(
            "Triage attempt {Attempt} for {TicketKey} failed: {Reason} ({ExceptionType})",
            attempt, ticket.Key, reason, ex.GetType().FullName);

        try
        {
            return await failureStore.RecordFailureAsync(
                new TriageFailure(ticket.Id, ticket.Key, attempt, reason, ex.GetType().FullName ?? ex.GetType().Name, FramesOnly(ex)),
                outerToken);
        }
        catch (OperationCanceledException) when (outerToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception storeEx)
        {
            logger.LogWarning("Could not record triage failure for {TicketKey}: {ExceptionType}", ticket.Key, storeEx.GetType().FullName);
            return null;
        }
    }
}
