using Microsoft.Extensions.Options;

namespace TicketTriage.Batch;

/// <summary>Maps the outcome of a batch run to an exit code and console output. Never prints ticket text.</summary>
public static class BatchCommand
{
    public const string Usage = "Usage: TicketTriage.Batch --input <challenge file (envelope or array)> --output <result.json>";

    public static async Task<int> ExecuteAsync(
        BatchRunner runner,
        TextWriter stdout,
        TextWriter stderr,
        CancellationToken cancellationToken,
        Func<CancellationToken, Task>? beforeRunAsync = null)
    {
        try
        {
            // Options and output location are checked first so a bad invocation never triggers the database initialisation.
            runner.Prepare();
            if (beforeRunAsync is not null)
            {
                await beforeRunAsync(cancellationToken);
            }

            var summary = await runner.RunAsync(cancellationToken);
            await stdout.WriteLineAsync(
                $"Tickets: {summary.Total} · fallback/failed: {summary.Fallbacks} · duration: {summary.Duration:hh\\:mm\\:ss} · output: {summary.OutputPath}");
            if (summary.Total > 0 && summary.Fallbacks == summary.Total)
            {
                await stdout.WriteLineAsync(
                    "Warning: every ticket used the fallback - the LLM provider is probably not configured (Llm section) or unreachable.");
            }

            return 0;
        }
        catch (OptionsValidationException ex)
        {
            // Failures are the static messages of our own validators; they never contain configuration values.
            await stderr.WriteLineAsync(string.Join(Environment.NewLine, ex.Failures));
            await stderr.WriteLineAsync(Usage);
            return 2;
        }
        catch (BatchInputException ex)
        {
            await stderr.WriteLineAsync(ex.Message);
            return 1;
        }
        catch (OperationCanceledException)
        {
            await stderr.WriteLineAsync(
                "The run was cancelled or aborted (see Triage:StopSystemOnFailure); no output written.");
            return 1;
        }
        catch (Exception ex)
        {
            await stderr.WriteLineAsync($"Unexpected error ({ex.GetType().Name}); no output written.");
            return 1;
        }
    }
}
