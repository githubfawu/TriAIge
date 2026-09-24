using TicketTriage.Core.Abstractions;
using TicketTriage.Core.Domain;

namespace TicketTriage.Infrastructure.Stubs;

internal sealed class StubResolutionDrafter : IResolutionDrafter
{
    // TODO: implement - replace with an LLM-backed drafter in TicketTriage.Agents.
    public Task<string> DraftAsync(
        Ticket ticket,
        TicketClassification classification,
        IReadOnlyList<SimilarTicket> similarTickets,
        CancellationToken cancellationToken) =>
        Task.FromResult("TODO: draft resolution comment.");
}
