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
        builder.AppendLine($"Description: {Clean(ticket.Description)}");
        builder.AppendLine($"Work type (unverified hint): {Clean(ticket.WorkType)}");
        builder.AppendLine($"Affected services (unverified hint): {Clean(string.Join(", ", ticket.AffectedServices))}");
        builder.AppendLine("</ticket>");
        return builder.ToString();
    }

    public static string FormatSimilarTickets(IReadOnlyList<SimilarTicket> similarTickets)
    {
        var builder = new StringBuilder();
        builder.AppendLine("<similar_tickets>");
        foreach (var (similar, index) in similarTickets.Select((s, i) => (s, i + 1)))
        {
            builder.AppendLine($"[{index}] Key: {Clean(similar.Ticket.Key)}");
            builder.AppendLine($"Summary: {Clean(similar.Ticket.Summary)}");
            builder.AppendLine($"Work type: {Clean(similar.Ticket.WorkType)}");
            builder.AppendLine($"Affected services: {Clean(string.Join(", ", similar.Ticket.AffectedServices))}");
            builder.AppendLine($"Resolution: {Clean(similar.Ticket.Resolution)}");
        }

        builder.AppendLine("</similar_tickets>");
        return builder.ToString();
    }

    public static string Clean(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return "(empty)";
        }

        var cleaned = text
            .Replace("<ticket>", "", StringComparison.OrdinalIgnoreCase)
            .Replace("</ticket>", "", StringComparison.OrdinalIgnoreCase)
            .Replace("<similar_tickets>", "", StringComparison.OrdinalIgnoreCase)
            .Replace("</similar_tickets>", "", StringComparison.OrdinalIgnoreCase)
            .Trim();

        return cleaned.Length > MaxFieldLength ? cleaned[..MaxFieldLength] : cleaned;
    }
}
