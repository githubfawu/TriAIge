using Microsoft.EntityFrameworkCore;
using TicketTriage.Core.Domain;
using TicketTriage.Infrastructure.Persistence;

namespace TicketTriage.Web.Triage;

/// <summary>Everything the Review page (Slice 2) needs, resolved from DB + RAM in one place so the page stays
/// EF-free (blazor-server skill). <see cref="FormBaseline"/> is null exactly when <see cref="HasSuggestion"/> is
/// false - the page shows "Agent is analysing…"/"Analyse now" instead of a form in that case.</summary>
public sealed record TicketReviewData(
    int Id,
    string IssueKey,
    string Summary,
    string? Description,
    IReadOnlyList<string> Comments,
    TicketDisplayState? State,
    bool IsTrainingTicket,
    string? FailureReason,
    string? RejectReason,
    string OriginalWorkType,
    string? OriginalAffectedService,
    string? OriginalServiceTeam,
    string? OriginalAssignee,
    string? OriginalUrgency,
    string? OriginalImpact,
    string? OriginalPriority,
    string? OriginalResolution,
    bool HasSuggestion,
    ReviewFormSnapshot? FormBaseline,
    IReadOnlyDictionary<ReviewField, string> Hints,
    IReadOnlyList<string> AllServiceTeamNames,
    IReadOnlyList<string> AllAffectedServiceNames,
    IReadOnlyList<string> SimilarTicketKeys,
    double? Confidence);

public sealed record TicketBoardRow(
    int Id,
    string IssueKey,
    string Summary,
    string WorkType,
    string? SuggestedWorkType,
    string Priority,
    string? SuggestedPriority,
    TicketDisplayState State,
    string? FailureReason);

public interface ITriageBoardQuery
{
    /// <summary>Rows for the <c>/tickets</c> board (FR13): DB tickets combined with RAM state, restricted to
    /// tickets that are actually part of triage (uploaded this session, analysed, or decided) - training tickets
    /// never show up.</summary>
    Task<IReadOnlyList<TicketBoardRow>> GetRowsAsync(CancellationToken cancellationToken);

    /// <summary>Everything the Review page needs for one ticket (FR15/FR21), or null if it doesn't exist.</summary>
    Task<TicketReviewData?> GetReviewAsync(int id, CancellationToken cancellationToken);

    /// <summary>The next Pending ticket (by id) other than <paramref name="excludeId"/>, or null if there is none -
    /// used to jump to the next ticket after a decision (FR19).</summary>
    Task<int?> GetNextPendingIdAsync(int excludeId, CancellationToken cancellationToken);
}

public sealed class TriageBoardQuery(
    IDbContextFactory<TriageDbContext> dbFactory,
    LookupCatalog catalog,
    TriageSessionStore store) : ITriageBoardQuery
{
    public async Task<IReadOnlyList<TicketBoardRow>> GetRowsAsync(CancellationToken cancellationToken)
    {
        await catalog.EnsureLoadedAsync(cancellationToken);

        var ramByTicketId = store.Snapshot().ToDictionary(e => e.TicketId);

        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);

        // Cheap SQL pre-filter (Finished == training data, ~20k rows); the exact "is this shown" rule is
        // TicketDisplayStateMapper below, applied in memory since the candidate set here is small.
        var candidates = await db.Tickets
            .AsNoTracking()
            .Where(t => t.StatusId != catalog.FinishedStatusId)
            .ToListAsync(cancellationToken);

        var rows = new List<TicketBoardRow>(candidates.Count);
        foreach (var ticket in candidates)
        {
            var ramEntry = ramByTicketId.GetValueOrDefault(ticket.Id);
            var facts = new TicketDbFacts(ticket.Id, ticket.StatusId, TicketPredicates.HasSuggestionCompiled(ticket));
            var state = TicketDisplayStateMapper.Derive(facts, ramEntry, catalog);
            if (state is null)
            {
                continue;
            }

            rows.Add(new TicketBoardRow(
                ticket.Id,
                ramEntry?.IssueKey ?? "—",
                ticket.Summary,
                catalog.WorkTypeNames.GetValueOrDefault(ticket.WorkTypeId, "—"),
                ticket.WorkTypeChangedId is { } changedWorkTypeId ? catalog.WorkTypeNames.GetValueOrDefault(changedWorkTypeId) : null,
                ticket.PriorityId is { } priorityId ? catalog.PriorityNames.GetValueOrDefault(priorityId, "—") : "—",
                ticket.PriorityChangedId is { } changedPriorityId ? catalog.PriorityNames.GetValueOrDefault(changedPriorityId) : null,
                state.Value,
                ramEntry?.FailureReason));
        }

        return [.. rows.OrderBy(r => r.Id)];
    }

    public async Task<TicketReviewData?> GetReviewAsync(int id, CancellationToken cancellationToken)
    {
        await catalog.EnsureLoadedAsync(cancellationToken);

        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var ticket = await db.Tickets.AsNoTracking()
            .Include(t => t.Comments)
            .SingleOrDefaultAsync(t => t.Id == id, cancellationToken);
        if (ticket is null)
        {
            return null;
        }

        var ramEntry = store.Get(id);
        var hasSuggestion = TicketPredicates.HasSuggestionCompiled(ticket);
        var facts = new TicketDbFacts(ticket.Id, ticket.StatusId, hasSuggestion);
        var state = TicketDisplayStateMapper.Derive(facts, ramEntry, catalog);

        var originalAffectedService = ticket.AffectedBusinessOrITServiceId is { } oaid ? catalog.AffectedServiceNames.GetValueOrDefault(oaid) : null;
        var originalServiceTeam = ticket.ServiceTeamId is { } otid ? catalog.ServiceTeamNames.GetValueOrDefault(otid) : null;
        var originalUrgency = ticket.UrgencyId is { } ouid ? catalog.UrgencyNames.GetValueOrDefault(ouid) : null;
        var originalImpact = ticket.ImpactId is { } oiid ? catalog.ImpactNames.GetValueOrDefault(oiid) : null;
        var originalPriority = ticket.PriorityId is { } opid ? catalog.PriorityNames.GetValueOrDefault(opid) : null;

        ReviewFormSnapshot? baseline = null;
        var hints = new Dictionary<ReviewField, string>();

        if (hasSuggestion)
        {
            var workType = ticket.WorkTypeChangedId is { } wtc && catalog.WorkTypeNames.TryGetValue(wtc, out var wtName)
                ? TriageVocabulary.ParseEnumOrNull<WorkType>(wtName) ?? WorkType.Incident
                : WorkType.Incident;
            var urgency = ticket.UrgencyChangedId is { } uc && catalog.UrgencyNames.TryGetValue(uc, out var uName)
                ? TriageVocabulary.ParseEnumOrNull<Urgency>(uName) ?? Urgency.Medium
                : Urgency.Medium;
            var impact = ticket.ImpactChangedId is { } ic && catalog.ImpactNames.TryGetValue(ic, out var iName)
                ? TriageVocabulary.ParseImpactFromDbName(iName) ?? Impact.Moderate
                : Impact.Moderate;

            var changedAffectedService = ticket.AffectedBusinessOrITServiceChangedId is { } caid ? catalog.AffectedServiceNames.GetValueOrDefault(caid) : null;
            var changedServiceTeam = ticket.ServiceTeamChangedId is { } ctid ? catalog.ServiceTeamNames.GetValueOrDefault(ctid) : null;
            var changedResolution = TriageVocabulary.ParseEnumOrNull<ResolutionStatus>(ticket.ResolutionChanged);

            var inputs = new ReviewFieldInputs(
                workType, urgency, impact,
                changedAffectedService, originalAffectedService,
                changedServiceTeam, originalServiceTeam,
                ticket.AssigneeChanged, ticket.Assignee,
                changedResolution, TriageVocabulary.ParseEnumOrNull<ResolutionStatus>(ticket.Resolution));

            baseline = ReviewFormModel.BuildBaseline(inputs, ramEntry?.Suggestion?.DraftComment);

            if (changedAffectedService is null)
            {
                hints[ReviewField.AffectedService] = NotInCatalogHint(ramEntry, "AffectedService", ramEntry?.Suggestion?.AffectedServices.FirstOrDefault());
            }

            if (changedServiceTeam is null)
            {
                hints[ReviewField.ServiceTeam] = NotInCatalogHint(ramEntry, "ServiceTeam", ramEntry?.Suggestion?.ServiceTeams.FirstOrDefault());
            }

            if (ticket.AssigneeChanged is null)
            {
                hints[ReviewField.Assignee] = "no suggestion – original kept";
            }

            if (changedResolution is null)
            {
                hints[ReviewField.Resolution] = "no suggestion – original kept";
            }
        }

        return new TicketReviewData(
            ticket.Id,
            ramEntry?.IssueKey ?? "—",
            ticket.Summary,
            ticket.Description,
            [.. ticket.Comments.Select(c => c.CommentText)],
            state,
            ticket.StatusId == catalog.FinishedStatusId,
            ramEntry?.FailureReason,
            ramEntry?.RejectReason,
            catalog.WorkTypeNames.GetValueOrDefault(ticket.WorkTypeId, "—"),
            originalAffectedService,
            originalServiceTeam,
            ticket.Assignee,
            originalUrgency,
            originalImpact,
            originalPriority,
            ticket.Resolution,
            hasSuggestion,
            baseline,
            hints,
            catalog.AllServiceTeamNames,
            catalog.AllAffectedServiceNames,
            ramEntry?.Suggestion?.SimilarTicketKeys ?? [],
            ramEntry?.Suggestion?.Confidence);
    }

    public async Task<int?> GetNextPendingIdAsync(int excludeId, CancellationToken cancellationToken)
    {
        await catalog.EnsureLoadedAsync(cancellationToken);

        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        return await db.Tickets.AsNoTracking()
            .Where(t => t.Id != excludeId && t.StatusId == catalog.NewStatusId)
            .Where(TicketPredicates.HasSuggestion)
            .OrderBy(t => t.Id)
            .Select(t => (int?)t.Id)
            .FirstOrDefaultAsync(cancellationToken);
    }

    /// <summary>Distinguishes the two reasons a suggested value can be missing (Leitplanke 6): the pipeline simply
    /// gave none, or it gave one that isn't in the lookup catalog. Both fall back to the original value; only the
    /// hint text differs, and only while the RAM entry (with the raw suggested name) is still around.</summary>
    private static string NotInCatalogHint(TriageSessionEntry? ramEntry, string fieldTag, string? rawSuggestedName) =>
        ramEntry?.NotInCatalog.Contains(fieldTag) == true && rawSuggestedName is not null
            ? $"'{rawSuggestedName}' not in catalog"
            : "no suggestion – original kept";
}
