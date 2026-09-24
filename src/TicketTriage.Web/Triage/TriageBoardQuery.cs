using Microsoft.EntityFrameworkCore;
using TicketTriage.Infrastructure.Persistence;

namespace TicketTriage.Web.Triage;

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
}
