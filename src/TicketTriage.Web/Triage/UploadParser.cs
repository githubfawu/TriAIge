using System.Text.Json;
using TicketTriage.Core.Domain;

namespace TicketTriage.Web.Triage;

public sealed record ParsedTicketEntry(int Index, Ticket? Ticket, bool IsValid, string? Reason);

public sealed record UploadParseResult(IReadOnlyList<ParsedTicketEntry> Entries, string? FileError)
{
    public static UploadParseResult Failed(string reason) => new([], reason);
}

/// <summary>
/// Pure, static JSON parsing of an uploaded challenge/training-shaped file (FR1, Leitplanke 9): bounds are
/// checked before any per-entry parsing, and each array element is deserialized individually so one malformed
/// ticket doesn't invalidate the whole file (<see cref="Ticket.Key"/>/<see cref="Ticket.Summary"/> are required).
/// </summary>
public static class UploadParser
{
    public const int MaxFileSizeBytes = 1_000_000;
    public const int MaxEntries = 200;

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public static UploadParseResult Parse(byte[] content)
    {
        if (content.Length > MaxFileSizeBytes)
        {
            return UploadParseResult.Failed($"File is larger than {MaxFileSizeBytes / 1_000_000} MB.");
        }

        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(content);
        }
        catch (JsonException)
        {
            return UploadParseResult.Failed("The file is not valid JSON.");
        }

        using (document)
        {
            if (document.RootElement.ValueKind != JsonValueKind.Array)
            {
                return UploadParseResult.Failed("The JSON root must be an array of tickets.");
            }

            var elements = document.RootElement.EnumerateArray().ToList();
            if (elements.Count == 0)
            {
                return UploadParseResult.Failed("The file contains no tickets.");
            }

            if (elements.Count > MaxEntries)
            {
                return UploadParseResult.Failed($"The file has more than {MaxEntries} tickets.");
            }

            var seenKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var entries = new List<ParsedTicketEntry>(elements.Count);
            for (var index = 0; index < elements.Count; index++)
            {
                entries.Add(ParseEntry(index, elements[index], seenKeys));
            }

            return new UploadParseResult(entries, FileError: null);
        }
    }

    private static ParsedTicketEntry ParseEntry(int index, JsonElement element, HashSet<string> seenKeys)
    {
        Ticket? ticket;
        try
        {
            ticket = element.Deserialize<Ticket>(JsonOptions);
        }
        catch (JsonException ex)
        {
            return new ParsedTicketEntry(index, null, false, $"Entry {index + 1} could not be read: {ex.Message}");
        }

        if (ticket is null)
        {
            return new ParsedTicketEntry(index, null, false, $"Entry {index + 1} is empty.");
        }

        if (string.IsNullOrWhiteSpace(ticket.Summary))
        {
            return new ParsedTicketEntry(index, ticket, false, "Summary is required.");
        }

        if (string.IsNullOrWhiteSpace(ticket.Key))
        {
            return new ParsedTicketEntry(index, ticket, false, "Issue key is required.");
        }

        if (!seenKeys.Add(ticket.Key))
        {
            return new ParsedTicketEntry(index, ticket, false, $"Duplicate issue key '{ticket.Key}'.");
        }

        return new ParsedTicketEntry(index, ticket, true, Reason: null);
    }
}
