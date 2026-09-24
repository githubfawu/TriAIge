using TicketTriage.Core.Abstractions;
using TicketTriage.Core.Domain;

namespace TicketTriage.Agents.Drafting;

/// <summary>Resolution status and comment. Richer than Core's <see cref="IResolutionDrafter"/>, which returns only the comment.</summary>
public sealed record ResolutionDraft(ResolutionStatus Status, string Comment, string Language);

/// <summary>Agents-side drafter that also exposes the resolution status until the Core port can carry it.</summary>
public interface IResolutionDraftAgent
{
    Task<ResolutionDraft> DraftWithStatusAsync(
        Ticket ticket,
        TicketClassification classification,
        IReadOnlyList<SimilarTicket> similarTickets,
        CancellationToken cancellationToken);
}
