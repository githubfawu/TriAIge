using System.Text.Json;
using Microsoft.Extensions.Logging;
using TicketTriage.Core.Abstractions;
using TicketTriage.Core.Domain;

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

    public static async Task<TriageSuggestion> CreateAsync(
        Ticket ticket,
        IReadOnlyList<SimilarTicket> similar,
        IRoutingResolver router,
        TimeSpan routingTimeout,
        ILogger logger,
        CancellationToken cancellationToken)
    {
        var classification = Classify(similar);
        var routing = new RoutingDecision([], null);

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        cts.CancelAfter(routingTimeout);
        try
        {
            routing = await router.ResolveAsync(ticket, classification, similar, cts.Token);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            logger.LogWarning("Fallback routing failed for {TicketKey}: {ExceptionType}", ticket.Key, ex.GetType().FullName);
        }

        return new TriageSuggestion
        {
            TicketKey = ticket.Key,
            WorkType = classification.WorkType,
            AffectedServices = classification.AffectedServices,
            ServiceTeams = routing.ServiceTeams,
            Assignee = routing.Assignee,
            Urgency = classification.Urgency,
            Impact = classification.Impact,
            DraftComment = null,
            ResolutionStatus = null,
            SimilarTicketKeys = [.. similar.Select(s => s.Ticket.Key)],
        };
    }

    // Most frequent value first; ties -> higher summed score, then the comparer's order.
    private static IEnumerable<T> Ranked<T>(IEnumerable<(T Value, double Score)> items, IComparer<T> comparer) =>
        items
            .GroupBy(i => i.Value)
            .Select(g => (Value: g.Key, Count: g.Count(), Score: g.Sum(i => i.Score)))
            .OrderByDescending(g => g.Count).ThenByDescending(g => g.Score)
            .ThenBy(g => g.Value, comparer)
            .Select(g => g.Value);

    private static WorkType? ParseWorkType(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            return null;
        }

        try
        {
            return JsonSerializer.Deserialize<WorkType>(JsonSerializer.Serialize(raw.Trim()));
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
