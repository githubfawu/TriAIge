using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using TicketTriage.Core.Abstractions;
using TicketTriage.Core.Domain;

namespace TicketTriage.Batch;

/// <summary>Reads the challenge tickets, triages them and writes the result file for scoring.</summary>
public sealed class BatchRunner(
    IOptions<BatchOptions> options,
    ITriagePipeline pipeline,
    TimeProvider timeProvider,
    ILogger<BatchRunner> logger)
{
    private static readonly JsonSerializerOptions OutputOptions = new() { WriteIndented = true };

    public async Task<BatchSummary> RunAsync(CancellationToken cancellationToken)
    {
        var started = timeProvider.GetTimestamp();
        var (input, output) = Prepare();

        logger.LogInformation("Reading challenge tickets from {Input}.", input);
        var document = await ChallengeDocument.ReadAsync(input, cancellationToken);
        var tickets = document.Tickets;

        foreach (var duplicate in document.DuplicateKeys)
        {
            logger.LogWarning("Duplicate issue key {Key} occurs more than once in the input.", duplicate);
        }

        var results = new List<TriageResult>(tickets.Count);
        var fallbacks = 0;
        await foreach (var suggestion in pipeline.TriageAsync(ToAsyncEnumerable(tickets), cancellationToken))
        {
            if (IsFallback(suggestion))
            {
                fallbacks++;
            }

            results.Add(TriageResult.From(suggestion));
        }

        if (results.Count != tickets.Count)
        {
            throw new InvalidOperationException(
                $"Pipeline returned {results.Count} results for {tickets.Count} tickets.");
        }

        cancellationToken.ThrowIfCancellationRequested();
        await WriteAtomicallyAsync(output, document.ToOutput(results), cancellationToken);

        var duration = timeProvider.GetElapsedTime(started);
        logger.LogInformation(
            "Wrote {Count} results ({Fallbacks} fallback) to {Output} in {Duration}.",
            results.Count, fallbacks, output, duration);
        return new BatchSummary(results.Count, fallbacks, duration, output);
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

    // The pipeline has no fallback flag, but its validator rejects a blank DraftComment on every successful
    // path while the fallback factory always leaves it null. TriageResult.From applies the same blank test.
    internal static bool IsFallback(TriageSuggestion suggestion) =>
        string.IsNullOrWhiteSpace(suggestion.DraftComment);

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
                await JsonSerializer.SerializeAsync(stream, content, OutputOptions, cancellationToken);
            }

            File.Move(temp, output, overwrite: true);
        }
        finally
        {
            File.Delete(temp);
        }
    }

    private static async IAsyncEnumerable<Ticket> ToAsyncEnumerable(IEnumerable<Ticket> tickets)
    {
        foreach (var ticket in tickets)
        {
            yield return ticket;
        }

        await Task.CompletedTask;
    }
}
