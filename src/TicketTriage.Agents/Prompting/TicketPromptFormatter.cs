using System.Text;
using TicketTriage.Core.Domain;

namespace TicketTriage.Agents.Prompting;

/// <summary>
/// Renders tickets into the user message. Ticket text is untrusted, so it is delimited and stripped of the
/// delimiter tags; instructions never contain ticket content. Priority/urgency/impact of historical tickets are
/// random in the training data and are deliberately never rendered.
/// </summary>
internal static class TicketPromptFormatter
{
    private const int MaxFieldLength = 1500;

    public static string FormatTicket(Ticket ticket)
    {
        var builder = new StringBuilder();
        builder.AppendLine("<ticket>");
        builder.AppendLine($"Summary: {Clean(ticket.Summary)}");
        builder.AppendLine($"Description: {Clean(ticket.Description, keepLineBreaks: true)}");
        builder.AppendLine($"Work type (unverified hint): {Clean(ticket.WorkType)}");
        builder.AppendLine($"Affected services (unverified hint): {Clean(string.Join(", ", ticket.AffectedServices))}");
        builder.AppendLine("</ticket>");
        return builder.ToString();
    }

    private const string ResolutionNotePrefix = "Resolution:";

    /// <param name="includeResolutionNote">Drafter only: adds the cleaned resolution note found in the comments.</param>
    /// <param name="includeResolutionStatus">Resolution status is ~random in the training data, so the drafter omits it.</param>
    public static string FormatSimilarTickets(
        IReadOnlyList<SimilarTicket> similarTickets,
        bool includeResolutionNote = false,
        bool includeResolutionStatus = true)
    {
        var builder = new StringBuilder();
        builder.AppendLine("<similar_tickets>");
        foreach (var (similar, index) in similarTickets.Select((s, i) => (s, i + 1)))
        {
            builder.AppendLine($"[{index}] Key: {Clean(similar.Ticket.Key)}");
            builder.AppendLine($"Summary: {Clean(similar.Ticket.Summary)}");
            builder.AppendLine($"Work type: {Clean(similar.Ticket.WorkType)}");
            builder.AppendLine($"Affected services: {Clean(string.Join(", ", similar.Ticket.AffectedServices))}");
            if (includeResolutionStatus)
            {
                builder.AppendLine($"Resolution status: {Clean(similar.Ticket.Resolution)}");
            }

            if (includeResolutionNote && ExtractResolutionNote(similar.Ticket) is { } note)
            {
                builder.AppendLine($"Resolution note: {Clean(note)}");
            }
        }

        builder.AppendLine("</similar_tickets>");
        return builder.ToString();
    }

    /// <summary>
    /// Comments look like "&lt;email&gt;: Resolution: text" or just "Resolution: text". The last one whose body starts
    /// with "Resolution:" wins; templates such as "Resolution recorded: ..." and "Problem fixed." do not match and
    /// are skipped.
    /// </summary>
    internal static string? ExtractResolutionNote(Ticket ticket)
    {
        foreach (var comment in ticket.Comments.Reverse())
        {
            var text = comment.TrimStart();
            var note = TryNote(text);
            if (note is null)
            {
                var separator = text.IndexOf(": ", StringComparison.Ordinal);
                if (separator >= 0)
                {
                    note = TryNote(text[(separator + 2)..].TrimStart());
                }
            }

            if (note is { Length: > 0 })
            {
                return note;
            }
        }

        return null;

        static string? TryNote(string body) =>
            body.StartsWith(ResolutionNotePrefix, StringComparison.OrdinalIgnoreCase)
                ? body[ResolutionNotePrefix.Length..].Trim()
                : null;
    }

    /// <summary>
    /// Neutralises everything that could forge the prompt structure: every angle bracket becomes a space (so no tag
    /// variant or nested reconstruction survives) and control characters are collapsed. Single-line fields (keys,
    /// notes, assignee, similar-ticket fields) collapse all whitespace to one space so a value cannot start a fake
    /// "[n] Key:" line; only the ticket description keeps its line breaks for readability, which is safe because
    /// it sits inside the ticket block and cannot close it.
    /// </summary>
    public static string Clean(string? text, bool keepLineBreaks = false)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return "(empty)";
        }

        var builder = new StringBuilder(Math.Min(text.Length, MaxFieldLength + 1));
        var pendingSpace = false;
        var pendingBreak = false;
        foreach (var c in text)
        {
            if (builder.Length > MaxFieldLength)
            {
                break;
            }

            if (keepLineBreaks && c is '\n' or '\r')
            {
                pendingBreak = true;
            }
            else if (char.IsWhiteSpace(c) || char.IsControl(c) || c is '<' or '>')
            {
                pendingSpace = true;
            }
            else
            {
                if (builder.Length > 0 && pendingBreak)
                {
                    builder.Append('\n');
                }
                else if (builder.Length > 0 && pendingSpace)
                {
                    builder.Append(' ');
                }

                pendingSpace = pendingBreak = false;
                builder.Append(c);
            }
        }

        if (builder.Length == 0)
        {
            return "(empty)";
        }

        return builder.Length > MaxFieldLength ? builder.ToString(0, MaxFieldLength) : builder.ToString();
    }
}
