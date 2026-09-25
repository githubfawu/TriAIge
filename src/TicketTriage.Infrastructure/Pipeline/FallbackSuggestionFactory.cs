using System.Text.Json;
using Microsoft.Extensions.Logging;
using TicketTriage.Core.Domain;
using TicketTriage.Infrastructure.Routing;

namespace TicketTriage.Infrastructure.Pipeline;

/// <summary>Builds the deterministic suggestion used when every attempt for a ticket failed.</summary>
internal static class FallbackSuggestionFactory
{
    public static TicketClassification Classify(IReadOnlyList<SimilarTicket> similar)
    {
        var workType = Ranked(
            similar
                .Select(s => (Value: ParseWorkType(s.Ticket.WorkType), s.Score))
                .Where(x => x.Value is not null)
                .Select(x => (x.Value!.Value, x.Score)),
            Comparer<WorkType>.Default).Cast<WorkType?>().FirstOrDefault() ?? WorkType.Incident;

        var service = Ranked(
            similar.SelectMany(s => s.Ticket.AffectedServices
                .Where(n => !string.IsNullOrWhiteSpace(n))
                .Select(n => ServiceCatalog.Find(n.Trim()))
                .OfType<ServiceDefinition>()
                .Select(d => (d.Name, s.Score))),
            StringComparer.Ordinal).FirstOrDefault();

        return new TicketClassification(workType, service is null ? [] : [service], Urgency.Medium, Impact.Moderate);
    }

    // The team comes straight from the statistics (not the resolver) so the fallback always agrees with the validator's routing rules.
    public static TriageSuggestion Create(
        Ticket ticket,
        IReadOnlyList<SimilarTicket> similar,
        RoutingStatistics statistics,
        string? assignee,
        ILogger logger)
    {
        var classification = Classify(similar);
        var teams = statistics.ResolveTeams(classification.AffectedServices);
        logger.LogDebug("Fallback for {TicketKey} routed to {TeamCount} team(s)", ticket.Key, teams.Count);

        return new TriageSuggestion
        {
            TicketKey = ticket.Key,
            WorkType = classification.WorkType,
            AffectedServices = classification.AffectedServices,
            ServiceTeams = teams,
            Assignee = assignee,
            Urgency = classification.Urgency,
            Impact = classification.Impact,
            DraftComment = null,
            ResolutionStatus = ResolveStatus(similar),
            SimilarTicketKeys = [.. similar.Select(s => s.Ticket.Key)],
        };
    }

    // Training statuses carry no signal (docs/features/score-completeness); this only keeps the field in the vocabulary.
    private static ResolutionStatus ResolveStatus(IReadOnlyList<SimilarTicket> similar) =>
        Ranked(
            similar
                .Select(s => (Value: ParseEnum<ResolutionStatus>(s.Ticket.Resolution), s.Score))
                .Where(x => x.Value is not null)
                .Select(x => (x.Value!.Value, x.Score)),
            Comparer<ResolutionStatus>.Default).Cast<ResolutionStatus?>().FirstOrDefault() ?? ResolutionStatus.Done;

    // Most frequent value first; ties -> higher summed score, then the comparer's order.
    private static IEnumerable<T> Ranked<T>(IEnumerable<(T Value, double Score)> items, IComparer<T> comparer) =>
        items
            .GroupBy(i => i.Value)
            .Select(g => (Value: g.Key, Count: g.Count(), Score: g.Sum(i => i.Score)))
            .OrderByDescending(g => g.Count).ThenByDescending(g => g.Score)
            .ThenBy(g => g.Value, comparer)
            .Select(g => g.Value);

    private static WorkType? ParseWorkType(string? raw) => ParseEnum<WorkType>(raw);

    private static T? ParseEnum<T>(string? raw) where T : struct, Enum
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            return null;
        }

        try
        {
            return JsonSerializer.Deserialize<T>(JsonSerializer.Serialize(raw.Trim()));
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
