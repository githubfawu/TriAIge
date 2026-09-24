using TicketTriage.Core.Domain;
using TicketTriage.Infrastructure.Persistence;

namespace TicketTriage.Web.Triage;

/// <summary>
/// Pure mapping between the JSON <see cref="Ticket"/>/<see cref="TriageSuggestion"/> shapes and the DB's
/// FK-id columns (Leitplanke 2: only ever by name, through <see cref="LookupCatalog"/>). No EF/DB access here -
/// keeps the mapping rules unit-testable without a database.
/// </summary>
public static class TicketMapper
{
    public const int SummaryMaxLength = 250;
    public const int DescriptionMaxLength = 1000;
    public const int ResolutionMaxLength = 500;
    public const int AssigneeMaxLength = 50;
    public const int CommentMaxLength = 500;

    public sealed record MappedTicket(TicketEntity Entity, IReadOnlyList<string> Hints);

    public sealed record SuggestedChangedValues(
        int WorkTypeChangedId,
        int? AffectedServiceChangedId,
        int? ServiceTeamChangedId,
        string? AssigneeChanged,
        int UrgencyChangedId,
        int ImpactChangedId,
        int PriorityChangedId,
        string? ResolutionChanged);

    /// <summary>Maps an uploaded ticket onto a new, untracked <see cref="TicketEntity"/> (mirrors
    /// <c>TrainingDataImporter</c>'s rules), always as <c>StatusId = New</c> regardless of any Resolution in the
    /// source file (deviation from the importer, Slice 1 design).</summary>
    public static MappedTicket ToEntity(Ticket ticket, LookupCatalog catalog, TimeProvider timeProvider)
    {
        var hints = new List<string>();

        var workTypeId = catalog.FindWorkTypeId(ticket.WorkType);
        if (workTypeId is null)
        {
            if (!string.IsNullOrWhiteSpace(ticket.WorkType))
            {
                hints.Add($"Unknown work type '{ticket.WorkType}', defaulted to Incident.");
            }

            workTypeId = catalog.DefaultWorkTypeId;
        }

        var affectedServiceName = ticket.AffectedServices.FirstOrDefault();
        var affectedServiceId = catalog.FindAffectedServiceId(affectedServiceName);
        if (affectedServiceName is not null && affectedServiceId is null)
        {
            hints.Add($"Unknown service '{affectedServiceName}', left empty.");
        }

        if (ticket.AffectedServices.Count > 1)
        {
            hints.Add("Only first service stored.");
        }

        var serviceTeamName = ticket.ServiceTeams.FirstOrDefault();
        var serviceTeamId = catalog.FindServiceTeamId(serviceTeamName);
        if (serviceTeamName is not null && serviceTeamId is null)
        {
            hints.Add($"Unknown team '{serviceTeamName}', left empty.");
        }

        if (ticket.ServiceTeams.Count > 1)
        {
            hints.Add("Only first team stored.");
        }

        var urgencyId = catalog.FindUrgencyId(ticket.Urgency);
        if (!string.IsNullOrWhiteSpace(ticket.Urgency) && urgencyId is null)
        {
            hints.Add($"Unknown urgency '{ticket.Urgency}', left empty.");
        }

        var dbImpactName = TriageVocabulary.TranslateImpactNameToDb(ticket.Impact);
        var impactId = catalog.FindImpactId(dbImpactName);
        if (!string.IsNullOrWhiteSpace(ticket.Impact) && impactId is null)
        {
            hints.Add($"Unknown impact '{ticket.Impact}', left empty.");
        }

        var priorityId = catalog.FindPriorityId(ticket.Priority);
        if (!string.IsNullOrWhiteSpace(ticket.Priority) && priorityId is null)
        {
            hints.Add($"Unknown priority '{ticket.Priority}', left empty.");
        }

        if (ticket.Summary.Length > SummaryMaxLength)
        {
            hints.Add($"Summary truncated to {SummaryMaxLength} characters.");
        }

        if (ticket.Description is { Length: > DescriptionMaxLength })
        {
            hints.Add($"Description truncated to {DescriptionMaxLength} characters.");
        }

        if (ticket.Resolution is { Length: > ResolutionMaxLength })
        {
            hints.Add($"Resolution truncated to {ResolutionMaxLength} characters.");
        }

        if (ticket.Assignee is { Length: > AssigneeMaxLength })
        {
            hints.Add($"Assignee truncated to {AssigneeMaxLength} characters.");
        }

        var entity = new TicketEntity
        {
            WorkTypeId = workTypeId.Value,
            Summary = TriageVocabulary.Truncate(ticket.Summary, SummaryMaxLength),
            Description = TriageVocabulary.Truncate(ticket.Description, DescriptionMaxLength),
            AffectedBusinessOrITServiceId = affectedServiceId,
            ServiceTeamId = serviceTeamId,
            Assignee = TriageVocabulary.Truncate(ticket.Assignee, AssigneeMaxLength),
            UrgencyId = urgencyId,
            ImpactId = impactId,
            PriorityId = priorityId,
            CreatedDate = ticket.Created?.UtcDateTime ?? timeProvider.GetUtcNow().UtcDateTime,
            StatusId = catalog.NewStatusId,
            Resolution = TriageVocabulary.Truncate(ticket.Resolution, ResolutionMaxLength),
            Comments = [.. ticket.Comments
                .Where(text => !string.IsNullOrWhiteSpace(text))
                .Select(text => new CommentEntity { CommentText = TriageVocabulary.Truncate(text, CommentMaxLength)! })],
        };

        return new MappedTicket(entity, hints);
    }

    /// <summary>Maps a pipeline suggestion onto the "*Changed" column values (FR7): all values are always written
    /// (even when equal to the original) so "has a suggestion" reliably means "was analysed" (Leitplanke 5/6).
    /// A service/team name that isn't in the lookup becomes <c>null</c> plus an entry in <c>NotInCatalog</c>,
    /// never a thrown exception - the analyst resolves it in review.</summary>
    public static (SuggestedChangedValues Values, IReadOnlyList<string> NotInCatalog) ToChangedValues(
        TriageSuggestion suggestion, LookupCatalog catalog)
    {
        var notInCatalog = new List<string>();

        var workTypeId = catalog.FindWorkTypeId(TriageVocabulary.ToJsonName(suggestion.WorkType))
            ?? throw MissingSeed("WorkType", TriageVocabulary.ToJsonName(suggestion.WorkType));

        var affectedServiceName = suggestion.AffectedServices.FirstOrDefault();
        var affectedServiceId = catalog.FindAffectedServiceId(affectedServiceName);
        if (affectedServiceName is not null && affectedServiceId is null)
        {
            notInCatalog.Add("AffectedService");
        }

        var serviceTeamName = suggestion.ServiceTeams.FirstOrDefault();
        var serviceTeamId = catalog.FindServiceTeamId(serviceTeamName);
        if (serviceTeamName is not null && serviceTeamId is null)
        {
            notInCatalog.Add("ServiceTeam");
        }

        var urgencyId = catalog.FindUrgencyId(TriageVocabulary.ToJsonName(suggestion.Urgency))
            ?? throw MissingSeed("Urgency", TriageVocabulary.ToJsonName(suggestion.Urgency));

        var impactId = catalog.FindImpactId(TriageVocabulary.DbImpactNameFor(suggestion.Impact))
            ?? throw MissingSeed("Impact", TriageVocabulary.DbImpactNameFor(suggestion.Impact));

        var priorityId = catalog.FindPriorityId(TriageVocabulary.ToJsonName(suggestion.Priority))
            ?? throw MissingSeed("Priority", TriageVocabulary.ToJsonName(suggestion.Priority));

        var resolutionChanged = suggestion.ResolutionStatus is { } status
            ? TriageVocabulary.ToJsonName(status)
            : null;

        var values = new SuggestedChangedValues(
            WorkTypeChangedId: workTypeId,
            AffectedServiceChangedId: affectedServiceId,
            ServiceTeamChangedId: serviceTeamId,
            AssigneeChanged: TriageVocabulary.Truncate(suggestion.Assignee, AssigneeMaxLength),
            UrgencyChangedId: urgencyId,
            ImpactChangedId: impactId,
            PriorityChangedId: priorityId,
            ResolutionChanged: TriageVocabulary.Truncate(resolutionChanged, ResolutionMaxLength));

        return (values, notInCatalog);
    }

    /// <summary>Rebuilds a Core <see cref="Ticket"/> from a DB row (FR10 "Analyse now": a New ticket that fell out
    /// of the RAM queue, e.g. after a restart, needs a <see cref="Ticket"/> to re-enqueue). The issue key is
    /// synthesized as <c>#{id}</c> since the original upload's key lived only in RAM and may be gone.</summary>
    public static Ticket ToTicket(TicketEntity entity, LookupCatalog catalog) => new()
    {
        Key = $"#{entity.Id}",
        Summary = entity.Summary,
        Description = entity.Description,
        WorkType = catalog.WorkTypeNames.GetValueOrDefault(entity.WorkTypeId),
        AffectedServices = entity.AffectedBusinessOrITServiceId is { } serviceId
            && catalog.AffectedServiceNames.TryGetValue(serviceId, out var serviceName)
                ? [serviceName]
                : [],
        ServiceTeams = entity.ServiceTeamId is { } teamId && catalog.ServiceTeamNames.TryGetValue(teamId, out var teamName)
            ? [teamName]
            : [],
        Assignee = entity.Assignee,
        Urgency = entity.UrgencyId is { } urgencyId ? catalog.UrgencyNames.GetValueOrDefault(urgencyId) : null,
        Impact = entity.ImpactId is { } impactId
            ? TriageVocabulary.RawImpactNameFromDb(catalog.ImpactNames.GetValueOrDefault(impactId))
            : null,
        Priority = entity.PriorityId is { } priorityId ? catalog.PriorityNames.GetValueOrDefault(priorityId) : null,
        Resolution = entity.Resolution,
        Created = new DateTimeOffset(entity.CreatedDate, TimeSpan.Zero),
        Comments = [.. entity.Comments.Select(c => c.CommentText)],
    };

    private static InvalidOperationException MissingSeed(string lookup, string name) =>
        new($"Seed data is missing the '{name}' {lookup} value; check TriageDbContext.OnModelCreating.");
}
