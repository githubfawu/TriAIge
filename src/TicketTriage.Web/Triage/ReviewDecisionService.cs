using Microsoft.EntityFrameworkCore;
using TicketTriage.Core.Domain;
using TicketTriage.Infrastructure.Persistence;

namespace TicketTriage.Web.Triage;

public enum DecisionOutcome
{
    Saved,
    AlreadyDecided,
}

public sealed record DecisionResult(DecisionOutcome Outcome);

public interface IReviewDecisionService
{
    /// <summary>Approve (Accept or Save, FR18/FR19): writes the form's final values onto the original columns,
    /// derives Priority via the matrix, clears every "*Changed" column and stores a non-empty comment as a new
    /// <see cref="CommentEntity"/> - all in one transaction, only while the ticket is still Pending (AC7).</summary>
    Task<DecisionResult> ApproveAsync(int ticketId, ReviewFormModel form, CancellationToken cancellationToken);

    /// <summary>Reject (FR18/FR19): clears every "*Changed" column, leaves the originals untouched (AC9). The
    /// reason is required and stays in RAM only (Technical Constraints).</summary>
    Task<DecisionResult> RejectAsync(int ticketId, string reason, CancellationToken cancellationToken);
}

/// <summary>Persists a human review decision via a single conditional <c>UPDATE</c> (Leitplanke 3/4): it only
/// applies while the ticket is still New with a suggestion, so two analysts deciding the same ticket never both
/// win (AC10) and no transaction ever spans a pipeline call.</summary>
public sealed class ReviewDecisionService(
    IDbContextFactory<TriageDbContext> dbFactory,
    LookupCatalog catalog,
    TriageSessionStore store) : IReviewDecisionService
{
    public async Task<DecisionResult> ApproveAsync(int ticketId, ReviewFormModel form, CancellationToken cancellationToken)
    {
        await catalog.EnsureLoadedAsync(cancellationToken);

        var workTypeId = catalog.FindWorkTypeId(TriageVocabulary.ToJsonName(form.WorkType))
            ?? throw MissingSeed("WorkType", TriageVocabulary.ToJsonName(form.WorkType));
        var affectedServiceId = form.AffectedService is null ? null : catalog.FindAffectedServiceId(form.AffectedService);
        var serviceTeamId = form.ServiceTeam is null ? null : catalog.FindServiceTeamId(form.ServiceTeam);
        var urgencyId = catalog.FindUrgencyId(TriageVocabulary.ToJsonName(form.Urgency))
            ?? throw MissingSeed("Urgency", TriageVocabulary.ToJsonName(form.Urgency));
        var impactId = catalog.FindImpactId(TriageVocabulary.DbImpactNameFor(form.Impact))
            ?? throw MissingSeed("Impact", TriageVocabulary.DbImpactNameFor(form.Impact));
        var priorityId = catalog.FindPriorityId(TriageVocabulary.ToJsonName(form.Priority))
            ?? throw MissingSeed("Priority", TriageVocabulary.ToJsonName(form.Priority));
        var assignee = TriageVocabulary.Truncate(form.Assignee, TicketMapper.AssigneeMaxLength);
        // Resolution is only written when a status was actually chosen (docs/features/web-triage-ui/plan.md §0.1);
        // otherwise the original column keeps whatever it already had (see the coalescing SetProperty below).
        var resolutionText = form.Resolution is { } status ? TriageVocabulary.ToJsonName(status) : null;
        var commentText = string.IsNullOrWhiteSpace(form.Comment) ? null : TriageVocabulary.Truncate(form.Comment, TicketMapper.CommentMaxLength);

        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);

        var rows = await db.Tickets
            .Where(t => t.Id == ticketId && t.StatusId == catalog.NewStatusId)
            .Where(TicketPredicates.HasSuggestion)
            .ExecuteUpdateAsync(s => s
                .SetProperty(t => t.WorkTypeId, workTypeId)
                .SetProperty(t => t.AffectedBusinessOrITServiceId, affectedServiceId)
                .SetProperty(t => t.ServiceTeamId, serviceTeamId)
                .SetProperty(t => t.Assignee, assignee)
                .SetProperty(t => t.UrgencyId, urgencyId)
                .SetProperty(t => t.ImpactId, impactId)
                .SetProperty(t => t.PriorityId, priorityId)
                .SetProperty(t => t.Resolution, t => resolutionText ?? t.Resolution)
                .SetProperty(t => t.WorkTypeChangedId, (int?)null)
                .SetProperty(t => t.AffectedBusinessOrITServiceChangedId, (int?)null)
                .SetProperty(t => t.ServiceTeamChangedId, (int?)null)
                .SetProperty(t => t.AssigneeChanged, (string?)null)
                .SetProperty(t => t.UrgencyChangedId, (int?)null)
                .SetProperty(t => t.ImpactChangedId, (int?)null)
                .SetProperty(t => t.PriorityChangedId, (int?)null)
                .SetProperty(t => t.ResolutionChanged, (string?)null)
                .SetProperty(t => t.StatusId, catalog.HumanApprovedStatusId),
                cancellationToken);

        if (rows == 0)
        {
            return new DecisionResult(DecisionOutcome.AlreadyDecided);
        }

        if (commentText is not null)
        {
            db.Comments.Add(new CommentEntity { TicketId = ticketId, CommentText = commentText });
            await db.SaveChangesAsync(cancellationToken);
        }

        await transaction.CommitAsync(cancellationToken);

        store.RecordDecision(ticketId, ReviewDecision.Approved, form.EditedFields(), rejectReason: null);
        return new DecisionResult(DecisionOutcome.Saved);
    }

    public async Task<DecisionResult> RejectAsync(int ticketId, string reason, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(reason);
        await catalog.EnsureLoadedAsync(cancellationToken);

        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);

        var rows = await db.Tickets
            .Where(t => t.Id == ticketId && t.StatusId == catalog.NewStatusId)
            .Where(TicketPredicates.HasSuggestion)
            .ExecuteUpdateAsync(s => s
                .SetProperty(t => t.WorkTypeChangedId, (int?)null)
                .SetProperty(t => t.AffectedBusinessOrITServiceChangedId, (int?)null)
                .SetProperty(t => t.ServiceTeamChangedId, (int?)null)
                .SetProperty(t => t.AssigneeChanged, (string?)null)
                .SetProperty(t => t.UrgencyChangedId, (int?)null)
                .SetProperty(t => t.ImpactChangedId, (int?)null)
                .SetProperty(t => t.PriorityChangedId, (int?)null)
                .SetProperty(t => t.ResolutionChanged, (string?)null)
                .SetProperty(t => t.StatusId, catalog.HumanRejectedStatusId),
                cancellationToken);

        if (rows == 0)
        {
            return new DecisionResult(DecisionOutcome.AlreadyDecided);
        }

        store.RecordDecision(ticketId, ReviewDecision.Rejected, editedFields: [], reason);
        return new DecisionResult(DecisionOutcome.Saved);
    }

    private static InvalidOperationException MissingSeed(string lookup, string name) =>
        new($"Seed data is missing the '{name}' {lookup} value; check TriageDbContext.OnModelCreating.");
}
