using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using TicketTriage.Core.Domain;
using TicketTriage.Infrastructure.Persistence;
using TicketTriage.Infrastructure.Pipeline;

namespace TicketTriage.Web.Triage;

public sealed record TicketBoardRow(
    int Id,
    long Version,
    string IssueKey,
    string Summary,
    string WorkType,
    string? SuggestedWorkType,
    string Priority,
    string? SuggestedPriority,
    TicketDisplayState State,
    string? FailureReason);

public interface ITicketBoardQuery
{
    /// <summary>Rows for the <c>/tickets</c> board and the dashboard (FR13): every non-training ticket, small
    /// enough (challenge + intake, never the ~20k training rows) to load in one query and derive state in memory.</summary>
    Task<IReadOnlyList<TicketBoardRow>> GetRowsAsync(CancellationToken cancellationToken);

    /// <summary>The next ticket (by id) other than <paramref name="excludeId"/> that is still awaiting a human
    /// decision - used to jump to the next ticket after a decision (FR19).</summary>
    Task<int?> GetNextPendingIdAsync(int excludeId, CancellationToken cancellationToken);

    /// <summary>The most recent failure reason recorded for a ticket, or null if none is stored.</summary>
    Task<string?> GetLatestFailureReasonAsync(int ticketId, CancellationToken cancellationToken);
}

/// <summary>
/// Reads <see cref="TicketEntity"/>/<see cref="TriageSuggestionEntity"/>/<see cref="TriageFailureEntity"/>
/// directly (main's status/claim/retry ports are per-ticket or internal to Infrastructure; there is no bulk "list
/// every triage ticket" port) - read-only, <c>AsNoTracking</c>, never touches the ~20k training rows.
/// </summary>
public sealed class TicketBoardQuery(
    IDbContextFactory<TriageDbContext> dbFactory,
    IOptions<TriageOptions> triageOptions) : ITicketBoardQuery
{
    // Mirrors TicketTriage.Infrastructure.Persistence.TicketStatusIds (internal to Infrastructure, no
    // InternalsVisibleTo for Web) and the seed in TriageDbContext.OnModelCreating; pinned by OriginMarkerTests.
    private const int StatusNew = 0;
    private const int StatusReviewing = 1;
    private const int StatusReviewed = 2;
    private const int StatusHumanRejected = 3;
    private const int StatusHumanApproved = 4;

    private static readonly JsonSerializerOptions PayloadOptions = new(JsonSerializerDefaults.Web);

    public async Task<IReadOnlyList<TicketBoardRow>> GetRowsAsync(CancellationToken cancellationToken)
    {
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);

        var tickets = await db.Tickets.AsNoTracking()
            .Where(t => t.Origin != TicketOrigin.Training)
            .Select(t => new { t.Id, t.Version, t.StatusId, t.ClaimedAt, t.Retries, t.SourcePayload, t.Summary, t.Description })
            .ToListAsync(cancellationToken);
        if (tickets.Count == 0)
        {
            return [];
        }

        var ids = tickets.Select(t => t.Id).ToList();
        var suggestions = await db.Suggestions.AsNoTracking()
            .Where(s => ids.Contains(s.TicketId))
            .ToDictionaryAsync(s => s.TicketId, cancellationToken);

        var retryCount = triageOptions.Value.RetryCount;
        var rows = new List<TicketBoardRow>(tickets.Count);
        var failedIds = new List<int>();

        foreach (var ticket in tickets)
        {
            var state = DeriveState(ticket.StatusId, ticket.ClaimedAt, ticket.Retries, retryCount);
            if (state == TicketDisplayState.Failed)
            {
                failedIds.Add(ticket.Id);
            }

            var original = ToTicket(ticket.Id, ticket.SourcePayload, ticket.Summary, ticket.Description);
            suggestions.TryGetValue(ticket.Id, out var suggestion);

            rows.Add(new TicketBoardRow(
                ticket.Id,
                ticket.Version,
                SuggestionMapper.TicketKey(ticket.Id),
                ticket.Summary,
                original.WorkType ?? "—",
                suggestion is null ? null : TriageVocabulary.ToJsonName(suggestion.WorkType),
                original.Priority ?? "—",
                suggestion is null ? null : TriageVocabulary.ToJsonName(suggestion.Priority),
                state,
                null));
        }

        if (failedIds.Count > 0)
        {
            var reasons = await db.TriageFailures.AsNoTracking()
                .Where(f => f.TicketId != null && failedIds.Contains(f.TicketId!.Value))
                .GroupBy(f => f.TicketId!.Value)
                .Select(g => new { TicketId = g.Key, Reason = g.OrderByDescending(f => f.OccurredAtUtc).First().Reason })
                .ToDictionaryAsync(x => x.TicketId, x => x.Reason, cancellationToken);

            for (var i = 0; i < rows.Count; i++)
            {
                if (rows[i].State == TicketDisplayState.Failed && reasons.TryGetValue(rows[i].Id, out var reason))
                {
                    rows[i] = rows[i] with { FailureReason = reason };
                }
            }
        }

        return [.. rows.OrderBy(r => r.Id)];
    }

    public async Task<int?> GetNextPendingIdAsync(int excludeId, CancellationToken cancellationToken)
    {
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        return await db.Tickets.AsNoTracking()
            .Where(t => t.Id != excludeId && t.Origin != TicketOrigin.Training
                && (t.StatusId == StatusReviewing || t.StatusId == StatusReviewed))
            .OrderBy(t => t.Id)
            .Select(t => (int?)t.Id)
            .FirstOrDefaultAsync(cancellationToken);
    }

    public async Task<string?> GetLatestFailureReasonAsync(int ticketId, CancellationToken cancellationToken)
    {
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        return await db.TriageFailures.AsNoTracking()
            .Where(f => f.TicketId == ticketId)
            .OrderByDescending(f => f.OccurredAtUtc)
            .Select(f => f.Reason)
            .FirstOrDefaultAsync(cancellationToken);
    }

    internal static TicketDisplayState DeriveState(int statusId, DateTime? claimedAt, int retries, int retryCount) => statusId switch
    {
        StatusHumanApproved => TicketDisplayState.Approved,
        StatusHumanRejected => TicketDisplayState.Rejected,
        StatusReviewing or StatusReviewed => TicketDisplayState.Pending,
        StatusNew when claimedAt is not null => TicketDisplayState.Analysing,
        StatusNew when retries >= retryCount => TicketDisplayState.Failed,
        _ => TicketDisplayState.Queued,
    };

    /// <summary>Mirrors the same "SourcePayload or fall back to the entity's own columns" pattern used throughout
    /// Infrastructure (e.g. EfAnalysisMonitor) - a corrupt payload must not break the board.</summary>
    private static Ticket ToTicket(int id, string? payload, string summary, string? description)
    {
        Ticket? ticket = null;
        try
        {
            ticket = payload is null ? null : JsonSerializer.Deserialize<Ticket>(payload, PayloadOptions);
        }
        catch (JsonException)
        {
            // Falls back to the entity's own columns below.
        }

        return (ticket ?? new Ticket { Summary = summary, Description = description }) with { Id = id };
    }
}
