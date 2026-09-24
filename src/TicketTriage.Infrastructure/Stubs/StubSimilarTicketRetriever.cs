using TicketTriage.Core.Abstractions;
using TicketTriage.Core.Domain;

namespace TicketTriage.Infrastructure.Stubs;

internal sealed class StubSimilarTicketRetriever : ISimilarTicketRetriever
{
    // TODO: implement - embed the ticket and run kNN over the training-set embedding store.
    public Task<IReadOnlyList<SimilarTicket>> FindSimilarAsync(Ticket ticket, int top, CancellationToken cancellationToken) =>
        Task.FromResult<IReadOnlyList<SimilarTicket>>([]);
}
