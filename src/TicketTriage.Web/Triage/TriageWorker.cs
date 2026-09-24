using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using TicketTriage.Core.Abstractions;

namespace TicketTriage.Web.Triage;

public sealed class TriageWorkerOptions
{
    public const string SectionName = "TriageWorker";

    public int MaxAttempts { get; set; } = 3;

    public TimeSpan Timeout { get; set; } = TimeSpan.FromSeconds(60);
}

/// <summary>
/// Background worker (ADR-0002): serially drains the RAM queue, one ticket at a time, and is the only place in
/// Web that resolves <see cref="ITriagePipeline"/> (NFR2/NFR10 - never from a page). Every ticket gets its own
/// DI scope and a 60s-bounded cancellation token (Leitplanke 3: never a transaction spanning the pipeline call).
/// </summary>
public sealed class TriageWorker(
    TriageSessionStore store,
    IServiceScopeFactory scopeFactory,
    IOptions<TriageWorkerOptions> options,
    ILogger<TriageWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            TriageSessionEntry entry;
            try
            {
                entry = await store.DequeueAsync(stoppingToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }

            try
            {
                await ProcessAsync(entry, stoppingToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }
    }

    private async Task ProcessAsync(TriageSessionEntry entry, CancellationToken stoppingToken)
    {
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
        timeoutCts.CancelAfter(options.Value.Timeout);

        try
        {
            await using var scope = scopeFactory.CreateAsyncScope();
            var pipeline = scope.ServiceProvider.GetRequiredService<ITriagePipeline>();
            var suggestion = await pipeline.TriageAsync(entry.Ticket, timeoutCts.Token);

            var writer = scope.ServiceProvider.GetRequiredService<ISuggestionWriter>();
            var notInCatalog = await writer.WriteAsync(entry.TicketId, suggestion, timeoutCts.Token);

            store.CompleteAnalysis(entry.TicketId, suggestion, notInCatalog);
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // App shutdown, not a per-ticket timeout: propagate so ExecuteAsync stops the loop without counting an attempt.
            throw;
        }
        catch (Exception ex)
        {
            // Never log ticket content (NFR6): id, attempt count and exception type only.
            logger.LogWarning(ex, "Triage attempt {Attempt} failed for ticket {TicketId}.", entry.Attempts, entry.TicketId);
            store.FailAttempt(entry.TicketId, ex.GetType().Name, options.Value.MaxAttempts);
        }
    }
}
