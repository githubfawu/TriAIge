using System.Text.Json;
using System.Text.Json.Nodes;
using TicketTriage.Core.Domain;

namespace TicketTriage.Batch;

/// <summary>
/// The challenge file: either a plain JSON array of records or an envelope object with a <c>records</c> array
/// (the real export). Keeps the original JSON so the output can mirror every record and the envelope metadata.
/// </summary>
/// <remarks>Error messages carry positions and type names only, never record values (personal data).</remarks>
public sealed class ChallengeDocument
{
    private const string RecordsProperty = "records";

    /// <summary>The real challenge file is tiny; the caps only bound memory for a wrong or hostile input.</summary>
    internal const long MaxFileBytes = 10 * 1024 * 1024;

    internal const int MaxRecords = 10_000;

    private static readonly JsonSerializerOptions InputOptions = new(JsonSerializerDefaults.Web);
    private static readonly JsonSerializerOptions ResultOptions = new();

    private readonly JsonNode _root;
    private readonly List<JsonObject> _records;

    private ChallengeDocument(JsonNode root, List<JsonObject> records, List<Ticket> tickets, List<string> duplicateKeys)
    {
        _root = root;
        _records = records;
        Tickets = tickets;
        DuplicateKeys = duplicateKeys;
    }

    /// <summary>One ticket per record, in file order. Records without <c>Issue key</c> get the key <c>#n</c> (1-based).</summary>
    public IReadOnlyList<Ticket> Tickets { get; }

    /// <summary>Real (non-positional) keys that occur more than once.</summary>
    public IReadOnlyList<string> DuplicateKeys { get; }

    public static string PositionalKey(int zeroBasedIndex) => $"#{zeroBasedIndex + 1}";

    public static async Task<ChallengeDocument> ReadAsync(string path, CancellationToken cancellationToken)
    {
        JsonNode? root;
        try
        {
            if (new FileInfo(path) is { Exists: true, Length: > MaxFileBytes })
            {
                throw new BatchInputException($"Input file '{path}' is larger than the limit of {MaxFileBytes} bytes.");
            }

            await using var stream = File.OpenRead(path);
            root = await JsonNode.ParseAsync(stream, cancellationToken: cancellationToken);
        }
        catch (JsonException ex)
        {
            throw new BatchInputException(
                $"Input file '{path}' is not valid JSON (line {ex.LineNumber + 1}, position {ex.BytePositionInLine + 1}).", ex);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException)
        {
            throw new BatchInputException($"Cannot read input file '{path}' ({ex.GetType().Name}).", ex);
        }

        var array = root switch
        {
            JsonArray a => a,
            JsonObject o when o.TryGetPropertyValue(RecordsProperty, out var records) => records as JsonArray
                ?? throw new BatchInputException(
                    $"Input file '{path}' has a '{RecordsProperty}' property that is not a JSON array."),
            JsonObject => throw new BatchInputException(
                $"Input file '{path}' is an object without a '{RecordsProperty}' array."),
            _ => throw new BatchInputException(
                $"Input file '{path}' must be a JSON array of tickets or an object with a '{RecordsProperty}' array."),
        };

        if (array.Count == 0)
        {
            throw new BatchInputException($"Input file '{path}' contains an empty ticket array.");
        }

        if (array.Count > MaxRecords)
        {
            throw new BatchInputException($"Input file '{path}' contains more than the limit of {MaxRecords} records.");
        }

        var recordObjects = new List<JsonObject>(array.Count);
        var tickets = new List<Ticket>(array.Count);
        for (var i = 0; i < array.Count; i++)
        {
            if (array[i] is not JsonObject record)
            {
                throw new BatchInputException($"Input file '{path}' contains a record at index {i} that is not a JSON object.");
            }

            Ticket? ticket;
            try
            {
                ticket = record.Deserialize<Ticket>(InputOptions);
            }
            catch (JsonException ex)
            {
                throw new BatchInputException(
                    $"Record at index {i} in '{path}' is not a valid ticket (path {ex.Path}).", ex);
            }

            if (ticket is null)
            {
                throw new BatchInputException($"Input file '{path}' contains null instead of a ticket at index {i}.");
            }

            recordObjects.Add(record);
            tickets.Add(string.IsNullOrWhiteSpace(ticket.Key) ? ticket with { Key = PositionalKey(i) } : ticket);
        }

        var duplicates = tickets
            .Where((t, i) => !string.IsNullOrWhiteSpace(t.Key) && t.Key != PositionalKey(i))
            .GroupBy(t => t.Key)
            .Where(g => g.Count() > 1)
            .Select(g => g.Key)
            .ToList();

        return new ChallengeDocument(root!, recordObjects, tickets, duplicates);
    }

    /// <summary>
    /// Mirrors the input: every record is cloned, its predicted fields are overwritten and all other fields keep
    /// their place. An envelope stays an envelope with its metadata; an array stays an array. No key is added.
    /// </summary>
    public JsonNode ToOutput(IReadOnlyList<TriageResult> results)
    {
        if (results.Count != _records.Count)
        {
            throw new InvalidOperationException($"Expected {_records.Count} results but got {results.Count}.");
        }

        var output = new JsonArray();
        for (var i = 0; i < _records.Count; i++)
        {
            var clone = (JsonObject)_records[i].DeepClone();
            var predicted = (JsonObject)JsonSerializer.SerializeToNode(results[i], ResultOptions)!;
            foreach (var (name, value) in predicted)
            {
                clone[name] = value?.DeepClone();
            }

            output.Add(clone);
        }

        if (_root is not JsonObject envelope)
        {
            return output;
        }

        var copy = (JsonObject)envelope.DeepClone();
        copy[RecordsProperty] = output;
        return copy;
    }
}
