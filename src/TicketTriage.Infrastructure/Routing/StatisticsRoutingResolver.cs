using TicketTriage.Core.Abstractions;
using TicketTriage.Core.Domain;

namespace TicketTriage.Infrastructure.Routing;

/// <summary>Team and assignee from historical majority vote (FR-13); ticket text is deliberately not used.</summary>
internal sealed class StatisticsRoutingResolver(IRoutingStatisticsSource source) : IRoutingResolver
{
    public async Task<RoutingDecision> ResolveAsync(
        Ticket ticket,
        TicketClassification classification,
        IReadOnlyList<SimilarTicket> similarTickets,
        CancellationToken cancellationToken)
    {
        var statistics = await source.GetAsync(cancellationToken);
        return statistics.Resolve(classification.AffectedServices);
    }
}
