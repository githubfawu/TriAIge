using System.Text.Json;
using Microsoft.Extensions.Logging;
using TicketTriage.Core.Domain;

namespace TicketTriage.Infrastructure.Pipeline;

/// <summary>A ticket with unusable classification hints removed, plus the flag codes raised for it.</summary>
internal sealed record NormalizedTicket(Ticket Ticket, IReadOnlyList<string> Flags);

/// <summary>
/// Flags empty or suspicious fields and nulls all supplied classification hints (work type, urgency, impact, priority),
/// flagging the ones that were supplied but invalid; the model classifies from the (unchanged) text, never from supplied hints.
/// </summary>
internal sealed class TicketNormalizer(ILogger<TicketNormalizer> logger)
{
    public const string EmptySummary = "EmptySummary";
    public const string EmptyDescription = "EmptyDescription";
    public const string NoServices = "NoServices";
    public const string UnknownWorkType = "UnknownWorkType";
    public const string UnknownUrgency = "UnknownUrgency";
    public const string UnknownImpact = "UnknownImpact";

    public NormalizedTicket Normalize(Ticket ticket)
    {
        List<string> flags = [];

        if (string.IsNullOrWhiteSpace(ticket.Summary))
        {
            flags.Add(EmptySummary);
        }

        if (string.IsNullOrWhiteSpace(ticket.Description))
        {
            flags.Add(EmptyDescription);
        }

        if (ticket.AffectedServices is not { Count: > 0 })
        {
            flags.Add(NoServices);
        }

        FlagIfInvalid<WorkType>(ticket.WorkType, UnknownWorkType, flags);
        FlagIfInvalid<Urgency>(ticket.Urgency, UnknownUrgency, flags);
        FlagIfInvalid<Impact>(ticket.Impact, UnknownImpact, flags);

        var normalized = ticket with
        {
            WorkType = null,
            Urgency = null,
            Impact = null,
            Priority = null,
        };

        if (flags.Count > 0)
        {
            logger.LogInformation("Ticket {TicketKey} normalized with flags {Flags}", ticket.Key, string.Join(',', flags));
        }

        return new NormalizedTicket(normalized, flags);
    }

    private static void FlagIfInvalid<TEnum>(string? raw, string flag, List<string> flags)
        where TEnum : struct, Enum
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            return;
        }

        try
        {
            // Goes through the enum's JsonStringEnumConverter so the Jira names ("No Impact") stay defined in one place.
            JsonSerializer.Deserialize<TEnum>(JsonSerializer.Serialize(raw.Trim()));
        }
        catch (JsonException)
        {
            flags.Add(flag);
        }
    }
}
