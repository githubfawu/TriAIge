using TicketTriage.Core.Abstractions;
using TicketTriage.Core.Domain;

namespace TicketTriage.Infrastructure.Stubs;

internal sealed class StubRoutingResolver : IRoutingResolver
{
    // TODO: implement - derive team/assignee from routing statistics (service -> team -> assignee frequencies).
    public Task<RoutingDecision> ResolveAsync(
        Ticket ticket,
        TicketClassification classification,
        IReadOnlyList<SimilarTicket> similarTickets,
        CancellationToken cancellationToken) =>
        Task.FromResult(new RoutingDecision(ServiceTeams: [], Assignee: null));
}
