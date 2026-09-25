using TicketTriage.Core.Abstractions;
using TicketTriage.Core.Domain;

namespace TicketTriage.Infrastructure.Routing;

/// <summary>Team from the historical majority vote per service (FR-13), assignee = least-loaded person; ticket text is deliberately not used.</summary>
internal sealed class StatisticsRoutingResolver(IRoutingStatisticsSource source, IAssigneeWorkload workload) : IRoutingResolver
{
    public async Task<RoutingDecision> ResolveAsync(
        Ticket ticket,
        TicketClassification classification,
        IReadOnlyList<SimilarTicket> similarTickets,
        CancellationToken cancellationToken)
    {
        var statistics = await source.GetAsync(cancellationToken);
        // Only a peek: the pipeline reserves the assignee once the suggestion is final, so retries do not count twice.
        return new RoutingDecision(
            statistics.ResolveTeams(classification.AffectedServices),
            await workload.PeekLeastLoadedAsync(cancellationToken));
    }
}
