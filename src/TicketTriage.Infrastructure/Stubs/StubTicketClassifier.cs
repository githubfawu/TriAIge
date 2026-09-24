using TicketTriage.Core.Abstractions;
using TicketTriage.Core.Domain;

namespace TicketTriage.Infrastructure.Stubs;

internal sealed class StubTicketClassifier : ITicketClassifier
{
    // TODO: implement - replace with an LLM-backed classifier in TicketTriage.Agents.
    public Task<TicketClassification> ClassifyAsync(
        Ticket ticket,
        IReadOnlyList<SimilarTicket> similarTickets,
        CancellationToken cancellationToken) =>
        Task.FromResult(new TicketClassification(
            WorkType.Incident,
            AffectedServices: [],
            Urgency.Medium,
            Impact.Moderate));
}
