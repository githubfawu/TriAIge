using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace TicketTriage.Batch;

/// <summary>Reads the challenge tickets and writes the result file for scoring.</summary>
public sealed class BatchRunner(IOptions<BatchOptions> options, ILogger<BatchRunner> logger)
{
    private static readonly JsonSerializerOptions OutputOptions = new() { WriteIndented = true };

    public async Task RunAsync(CancellationToken cancellationToken)
    {
        var input = Path.GetFullPath(options.Value.Input);
        var output = Path.GetFullPath(options.Value.Output);

        logger.LogInformation("Reading challenge tickets from {Input}.", input);
        JsonNode? tickets;
        await using (var stream = File.OpenRead(input))
        {
            tickets = await JsonNode.ParseAsync(stream, cancellationToken: cancellationToken);
        }

        // TODO: implement - deserialize into Ticket, run ITriagePipeline per ticket and write TriageResult[]
        // in the official scoring format. For now the input is echoed back unchanged, wrapped with a marker.
        var result = new JsonObject
        {
            ["TODO"] = "Triage not implemented yet; tickets holds the unchanged input.",
            ["tickets"] = tickets,
        };

        Directory.CreateDirectory(Path.GetDirectoryName(output)!);
        await using (var stream = File.Create(output))
        {
            await JsonSerializer.SerializeAsync(stream, result, OutputOptions, cancellationToken);
        }

        logger.LogInformation("Wrote {Output}.", output);
    }
}
