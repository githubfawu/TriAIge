using Microsoft.Extensions.Options;
using TicketTriage.Infrastructure.Analysis;

namespace TicketTriage.Web.Analysis;

/// <summary>Runs the analysis cycle immediately and then on a fixed interval; a failed cycle never stops the worker.</summary>
internal sealed partial class AnalysisWorker(
    IAnalysisCycle cycle,
    IOptions<AnalysisOptions> options,
    ILogger<AnalysisWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!options.Value.Enabled)
        {
            LogDisabled(logger);
            return;
        }

        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(options.Value.IntervalSeconds));
        try
        {
            do
            {
                await RunCycleAsync(stoppingToken);
            }
            while (await timer.WaitForNextTickAsync(stoppingToken));
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // Host shutdown.
        }
    }

    private async Task RunCycleAsync(CancellationToken stoppingToken)
    {
        try
        {
            await cycle.RunOnceAsync(stoppingToken);
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            LogCycleFailed(logger, ex.GetType().Name, ex.StackTrace);
        }
    }

    [LoggerMessage(Level = LogLevel.Information, Message = "Analysis worker disabled (Analysis:Enabled=false).")]
    private static partial void LogDisabled(ILogger logger);

    [LoggerMessage(Level = LogLevel.Error, Message = "Analysis cycle failed ({ExceptionType}); retrying at the next interval. {StackTrace}")]
    private static partial void LogCycleFailed(ILogger logger, string exceptionType, string? stackTrace);
}
