using System.Text.Json;
using System.Text.Json.Nodes;
using TicketTriage.Core.Domain;

namespace TicketTriage.Infrastructure.Challenge;

/// <summary>
/// The challenge file: either a plain JSON array of records or an envelope object with a <c>records</c> array
/// (the real export). Keeps the original JSON so the output can mirror every record and the envelope metadata.
/// </summary>
/// <remarks>Error messages carry positions and type names only, never record values (personal data) or paths.</remarks>
public sealed class ChallengeDocument
{
    private const string RecordsProperty = "records";

    /// <summary>The real challenge file is tiny; the caps only bound memory for a wrong or hostile input.</summary>
    public const long MaxFileBytes = 10 * 1024 * 1024;

    public const int MaxRecords = 500;

    private static readonly JsonSerializerOptions InputOptions = new(JsonSerializerDefaults.Web);
    private static readonly JsonSerializerOptions ResultOptions = new();
    private static readonly JsonSerializerOptions OutputOptions = new() { WriteIndented = true };

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

    /// <summary>Reads at most <see cref="MaxFileBytes"/> from the stream; more is rejected.</summary>
    public static async Task<ChallengeDocument> ReadAsync(Stream stream, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(stream);

        JsonNode? root;
        try
        {
            using var buffer = new MemoryStream();
            var chunk = new byte[81920];
            int read;
            while ((read = await stream.ReadAsync(chunk, cancellationToken)) > 0)
            {
                if (buffer.Length + read > MaxFileBytes)
                {
                    throw new ChallengeFormatException($"The input is larger than the limit of {MaxFileBytes} bytes.");
                }

                buffer.Write(chunk, 0, read);
            }

            buffer.Position = 0;
            root = await JsonNode.ParseAsync(buffer, cancellationToken: cancellationToken);
        }
        catch (JsonException ex)
        {
            throw new ChallengeFormatException(
                $"The input is not valid JSON (line {ex.LineNumber + 1}, position {ex.BytePositionInLine + 1}).", ex);
        }
        catch (Exception ex) when (ex is IOException or NotSupportedException)
        {
            throw new ChallengeFormatException($"The input cannot be read ({ex.GetType().Name}).", ex);
        }

        return Parse(root);
    }

    public static ChallengeDocument Parse(JsonNode? root)
    {
        var array = root switch
        {
            JsonArray a => a,
            JsonObject o when o.TryGetPropertyValue(RecordsProperty, out var records) => records as JsonArray
                ?? throw new ChallengeFormatException($"The input has a '{RecordsProperty}' property that is not a JSON array."),
            JsonObject => throw new ChallengeFormatException($"The input is an object without a '{RecordsProperty}' array."),
            _ => throw new ChallengeFormatException(
                $"The input must be a JSON array of tickets or an object with a '{RecordsProperty}' array."),
        };

        if (array.Count == 0)
        {
            throw new ChallengeFormatException("The input contains an empty ticket array.");
        }

        if (array.Count > MaxRecords)
        {
            throw new ChallengeFormatException($"The input contains more than the limit of {MaxRecords} records.");
        }

        var recordObjects = new List<JsonObject>(array.Count);
        var tickets = new List<Ticket>(array.Count);
        for (var i = 0; i < array.Count; i++)
        {
            if (array[i] is not JsonObject record)
            {
                throw new ChallengeFormatException($"The input contains a record at index {i} that is not a JSON object.");
            }

            Ticket? ticket;
            try
            {
                ticket = record.Deserialize<Ticket>(InputOptions);
            }
            catch (JsonException ex)
            {
                throw new ChallengeFormatException($"Record at index {i} is not a valid ticket (path {ex.Path}).", ex);
            }

            if (ticket is null)
            {
                throw new ChallengeFormatException($"The input contains null instead of a ticket at index {i}.");
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

    /// <summary>Single indented serializer so Batch and Web produce byte-identical files.</summary>
    public static Task WriteAsync(JsonNode content, Stream destination, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(content);
        ArgumentNullException.ThrowIfNull(destination);
        return JsonSerializer.SerializeAsync(destination, content, OutputOptions, cancellationToken);
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
