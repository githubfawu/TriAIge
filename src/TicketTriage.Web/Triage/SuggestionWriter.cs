using Microsoft.EntityFrameworkCore;
using TicketTriage.Core.Domain;
using TicketTriage.Infrastructure.Persistence;

namespace TicketTriage.Web.Triage;

public interface ISuggestionWriter
{
    /// <summary>Writes a pipeline suggestion into the "*Changed" columns; returns the field names that couldn't
    /// be resolved against the lookup catalog (FR7).</summary>
    Task<IReadOnlyList<string>> WriteAsync(int ticketId, TriageSuggestion suggestion, CancellationToken cancellationToken);
}

/// <summary>Writes a suggestion with a single conditional <c>UPDATE</c> (Leitplanke 3/4): only applies if the
/// ticket is still New and has no suggestion yet, so a suggestion is never overwritten and no transaction ever
/// spans the pipeline call.</summary>
public sealed class SuggestionWriter(IDbContextFactory<TriageDbContext> dbFactory, LookupCatalog catalog) : ISuggestionWriter
{
    public async Task<IReadOnlyList<string>> WriteAsync(int ticketId, TriageSuggestion suggestion, CancellationToken cancellationToken)
    {
        await catalog.EnsureLoadedAsync(cancellationToken);
        var (values, notInCatalog) = TicketMapper.ToChangedValues(suggestion, catalog);

        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);

        // Writable statuses are New and Reviewing (if the current seed has it) - never Reviewed, since a
        // suggestion is only ever written once (HasNoSuggestion below enforces that too). The worker keeps
        // "Analysing" in RAM only and never writes Reviewing; this just tolerates a ticket that's already there.
        var writableStatusIds = catalog.ReviewingStatusId is { } reviewingId
            ? new[] { catalog.NewStatusId, reviewingId }
            : new[] { catalog.NewStatusId };
        var reviewedStatusId = catalog.ReviewedStatusId;

        await db.Tickets
            .Where(t => t.Id == ticketId && writableStatusIds.Contains(t.StatusId))
            .Where(TicketPredicates.HasNoSuggestion)
            .ExecuteUpdateAsync(setters => setters
                .SetProperty(t => t.WorkTypeChangedId, values.WorkTypeChangedId)
                .SetProperty(t => t.AffectedBusinessOrITServiceChangedId, values.AffectedServiceChangedId)
                .SetProperty(t => t.ServiceTeamChangedId, values.ServiceTeamChangedId)
                .SetProperty(t => t.AssigneeChanged, values.AssigneeChanged)
                .SetProperty(t => t.UrgencyChangedId, values.UrgencyChangedId)
                .SetProperty(t => t.ImpactChangedId, values.ImpactChangedId)
                .SetProperty(t => t.PriorityChangedId, values.PriorityChangedId)
                .SetProperty(t => t.ResolutionChanged, values.ResolutionChanged)
                // New -> Reviewing -> Reviewed -> HumanApproved/HumanRejected (team's lifecycle); only advance to
                // Reviewed when that status exists in the current seed data.
                .SetProperty(t => t.StatusId, t => reviewedStatusId ?? t.StatusId),
                cancellationToken);

        return notInCatalog;
    }
}
