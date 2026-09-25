using TicketTriage.Core.Abstractions;
using TicketTriage.Core.Domain;

namespace TicketTriage.Infrastructure.Stubs;

internal sealed class StubResolutionDrafter : IResolutionDrafter
{
    // TODO: implement - replace with an LLM-backed drafter in TicketTriage.Agents.
    public Task<ResolutionDraft> DraftAsync(
        Ticket ticket,
        TicketClassification classification,
        RoutingDecision routing,
        IReadOnlyList<SimilarTicket> similarTickets,
        CancellationToken cancellationToken) =>
        Task.FromResult(new ResolutionDraft(ResolutionStatus.Done, "TODO: draft resolution comment."));
}
