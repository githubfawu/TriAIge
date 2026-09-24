using TicketTriage.Core.Abstractions;
using TicketTriage.Core.Domain;

namespace TicketTriage.Infrastructure.Stubs;

/// <summary>Wires the (stub) steps together in the intended order; priority comes from <see cref="PriorityMatrix"/>.</summary>
internal sealed class StubTriagePipeline(
    ISimilarTicketRetriever retriever,
    ITicketClassifier classifier,
    IRoutingResolver routingResolver,
    IResolutionDrafter drafter) : ITriagePipeline
{
    // TODO: implement - replace with the Agent Framework workflow; keep this ordering as the reference.
    public async Task<TriageSuggestion> TriageAsync(Ticket ticket, CancellationToken cancellationToken)
    {
        var similar = await retriever.FindSimilarAsync(ticket, top: 10, cancellationToken);
        var classification = await classifier.ClassifyAsync(ticket, similar, cancellationToken);
        var routing = await routingResolver.ResolveAsync(ticket, classification, similar, cancellationToken);
        var draft = await drafter.DraftAsync(ticket, classification, similar, cancellationToken);

        return new TriageSuggestion
        {
            TicketKey = ticket.Key,
            WorkType = classification.WorkType,
            AffectedServices = classification.AffectedServices,
            ServiceTeams = routing.ServiceTeams,
            Assignee = routing.Assignee,
            Urgency = classification.Urgency,
            Impact = classification.Impact,
            DraftComment = draft,
            SimilarTicketKeys = [.. similar.Select(s => s.Ticket.Key)],
            Confidence = classification.Confidence,
        };
    }
}
