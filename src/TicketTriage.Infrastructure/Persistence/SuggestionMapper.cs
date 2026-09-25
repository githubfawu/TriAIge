using TicketTriage.Core.Domain;

namespace TicketTriage.Infrastructure.Persistence;

public static class SuggestionMapper
{
    public static string TicketKey(int ticketId) => $"DB-{ticketId}";

    public static TriageSuggestionEntity ToEntity(int ticketId, TriageSuggestion suggestion, DateTime createdAtUtc)
    {
        ArgumentNullException.ThrowIfNull(suggestion);

        return new TriageSuggestionEntity
        {
            TicketId = ticketId,
            WorkType = suggestion.WorkType,
            AffectedServices = [.. suggestion.AffectedServices],
            ServiceTeams = [.. suggestion.ServiceTeams],
            Assignee = suggestion.Assignee,
            Urgency = suggestion.Urgency,
            Impact = suggestion.Impact,
            Priority = suggestion.Priority,
            ResolutionStatus = suggestion.ResolutionStatus,
            DraftComment = suggestion.DraftComment,
            SimilarTicketKeys = [.. suggestion.SimilarTicketKeys],
            IsFallback = suggestion.IsFallback,
            CreatedAtUtc = createdAtUtc,
        };
    }

    public static TriageSuggestion ToDomain(TriageSuggestionEntity entity)
    {
        ArgumentNullException.ThrowIfNull(entity);

        return new TriageSuggestion
        {
            TicketKey = TicketKey(entity.TicketId),
            WorkType = entity.WorkType,
            AffectedServices = [.. entity.AffectedServices],
            ServiceTeams = [.. entity.ServiceTeams],
            Assignee = entity.Assignee,
            Urgency = entity.Urgency,
            Impact = entity.Impact,
            ResolutionStatus = entity.ResolutionStatus,
            DraftComment = entity.DraftComment,
            SimilarTicketKeys = [.. entity.SimilarTicketKeys],
        };
    }
}
