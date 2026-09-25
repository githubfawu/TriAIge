using System.Globalization;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using TicketTriage.Core.Abstractions;
using TicketTriage.Core.Domain;
using TicketTriage.Infrastructure.Persistence;
using TicketTriage.Infrastructure.Pipeline;

namespace TicketTriage.Infrastructure.Review;

/// <summary>Stores review decisions and edits with optimistic concurrency on <see cref="TicketEntity.Version"/>.</summary>
public sealed class EfReviewService(
    IDbContextFactory<TriageDbContext> dbFactory,
    IOptions<TriageOptions> triageOptions,
    TimeProvider timeProvider) : IReviewService
{
    internal const int MaxCommentLength = 2000;
    internal const int MaxReasonLength = 500;

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public async Task<TicketReview?> OpenAsync(int ticketId, CancellationToken cancellationToken)
    {
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var ticket = await db.Tickets.AsNoTracking()
            .SingleOrDefaultAsync(t => t.Id == ticketId && t.Origin != TicketOrigin.Training, cancellationToken);
        if (ticket is null)
        {
            return null;
        }

        var suggestion = await db.Suggestions.AsNoTracking().SingleOrDefaultAsync(s => s.TicketId == ticketId, cancellationToken);
        var edits = await db.SuggestionEdits.AsNoTracking().Where(e => e.TicketId == ticketId).ToListAsync(cancellationToken);

        var firstOpened = suggestion?.FirstOpenedAtUtc;
        if (suggestion is not null && firstOpened is null)
        {
            // Conditional update keeps the first opening; it deliberately leaves Version alone (D7).
            var now = Now();
            var updated = await db.Suggestions
                .Where(s => s.TicketId == ticketId && s.FirstOpenedAtUtc == null)
                .ExecuteUpdateAsync(s => s.SetProperty(x => x.FirstOpenedAtUtc, (DateTime?)now), cancellationToken);
            firstOpened = updated > 0
                ? now
                : await db.Suggestions.AsNoTracking().Where(s => s.TicketId == ticketId).Select(s => s.FirstOpenedAtUtc).SingleAsync(cancellationToken);
        }

        var domain = suggestion is null ? null : SuggestionMapper.ToDomain(suggestion);
        var infos = edits
            .Select(e => new SuggestionEditInfo(Enum.Parse<SuggestionField>(e.Field), e.AiValue, e.FinalValue, e.EditedAtUtc))
            .OrderBy(e => e.Field)
            .ToList();

        return new TicketReview(
            ToTicket(ticket),
            domain,
            domain is null ? null : Apply(domain, infos),
            StatusName(ticket.StatusId),
            ticket.Version,
            IsAnalysing: ticket.StatusId == TicketStatusIds.New,
            IsFailed: ticket.Retries >= triageOptions.Value.RetryCount,
            suggestion is null ? null : new ReviewDecisionInfo(suggestion.Decision, suggestion.RejectReason, firstOpened, suggestion.DecidedAtUtc),
            infos);
    }

    public Task<ReviewResult> SaveEditsAsync(int ticketId, long expectedVersion, ReviewEdits edits, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(edits);
        return DecideAsync(ticketId, expectedVersion, edits, TicketStatusIds.Reviewed, requireEdits: true, reason: null, cancellationToken);
    }

    public Task<ReviewResult> ApproveAsync(int ticketId, long expectedVersion, ReviewEdits? edits, CancellationToken cancellationToken) =>
        DecideAsync(ticketId, expectedVersion, edits, TicketStatusIds.HumanApproved, requireEdits: false, reason: null, cancellationToken);

    public Task<ReviewResult> RejectAsync(int ticketId, long expectedVersion, string reason, CancellationToken cancellationToken)
    {
        var trimmed = reason?.Trim();
        if (string.IsNullOrEmpty(trimmed))
        {
            return Task.FromResult(Result(ReviewOutcome.Invalid, "A reject reason is required."));
        }

        return trimmed.Length > MaxReasonLength
            ? Task.FromResult(Result(ReviewOutcome.Invalid, $"The reject reason is limited to {MaxReasonLength} characters."))
            : DecideAsync(ticketId, expectedVersion, null, TicketStatusIds.HumanRejected, requireEdits: false, trimmed, cancellationToken);
    }

    public async Task<ReviewResult> RequeueFailedAsync(int ticketId, long expectedVersion, CancellationToken cancellationToken)
    {
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var ticket = await db.Tickets.SingleOrDefaultAsync(t => t.Id == ticketId && t.Origin != TicketOrigin.Training, cancellationToken);
        if (ticket is null)
        {
            return Result(ReviewOutcome.NotFound, "Ticket not found.");
        }

        if (ticket.StatusId != TicketStatusIds.Reviewing || ticket.Retries < triageOptions.Value.RetryCount)
        {
            return Result(ReviewOutcome.InvalidState, "Only a failed ticket can be requeued.");
        }

        if (ticket.Version != expectedVersion)
        {
            return Result(ReviewOutcome.Conflict, "The ticket was changed in the meantime.");
        }

        db.Suggestions.RemoveRange(await db.Suggestions.Where(s => s.TicketId == ticketId).ToListAsync(cancellationToken));
        db.SuggestionEdits.RemoveRange(await db.SuggestionEdits.Where(e => e.TicketId == ticketId).ToListAsync(cancellationToken));
        ticket.Retries = 0;
        ticket.StatusId = TicketStatusIds.New;
        ticket.ClaimedAt = null;
        db.Entry(ticket).Property(t => t.Version).OriginalValue = expectedVersion;
        ticket.Version = expectedVersion + 1;

        try
        {
            await db.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException)
        {
            return Result(ReviewOutcome.Conflict, "The ticket was changed in the meantime.");
        }

        return new ReviewResult(ReviewOutcome.Success, ticket.Version, null);
    }

    private async Task<ReviewResult> DecideAsync(
        int ticketId,
        long expectedVersion,
        ReviewEdits? edits,
        int targetStatus,
        bool requireEdits,
        string? reason,
        CancellationToken cancellationToken)
    {
        var hasEdits = edits is { HasAny: true };
        if (requireEdits && !hasEdits)
        {
            return Result(ReviewOutcome.Invalid, "No edits given.");
        }

        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var ticket = await db.Tickets.SingleOrDefaultAsync(t => t.Id == ticketId && t.Origin != TicketOrigin.Training, cancellationToken);
        if (ticket is null)
        {
            return Result(ReviewOutcome.NotFound, "Ticket not found.");
        }

        if (ticket.StatusId is not (TicketStatusIds.Reviewing or TicketStatusIds.Reviewed))
        {
            return Result(ReviewOutcome.InvalidState, $"A ticket in status {StatusName(ticket.StatusId)} cannot be reviewed.");
        }

        if (ticket.Version != expectedVersion)
        {
            return Result(ReviewOutcome.Conflict, "The ticket was changed in the meantime.");
        }

        var suggestion = await db.Suggestions.SingleOrDefaultAsync(s => s.TicketId == ticketId, cancellationToken);
        if (suggestion is null)
        {
            return Result(ReviewOutcome.InvalidState, "The ticket has no suggestion.");
        }

        var now = Now();
        if (hasEdits)
        {
            var existing = await db.SuggestionEdits.Where(e => e.TicketId == ticketId).ToListAsync(cancellationToken);
            var error = ApplyEdits(db, suggestion, existing, edits!, now);
            if (error is not null)
            {
                return Result(ReviewOutcome.Invalid, error);
            }
        }

        ticket.StatusId = targetStatus;
        if (targetStatus == TicketStatusIds.HumanApproved)
        {
            suggestion.Decision = ReviewDecision.Approved;
            suggestion.DecidedAtUtc = now;
        }
        else if (targetStatus == TicketStatusIds.HumanRejected)
        {
            suggestion.Decision = ReviewDecision.Rejected;
            suggestion.RejectReason = reason;
            suggestion.DecidedAtUtc = now;
        }

        db.Entry(ticket).Property(t => t.Version).OriginalValue = expectedVersion;
        ticket.Version = expectedVersion + 1;

        try
        {
            await db.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException)
        {
            // Also covers a unique (TicketId, Field) violation from a concurrent first edit; DbUpdateConcurrencyException derives from it.
            return Result(ReviewOutcome.Conflict, "The ticket was changed in the meantime.");
        }

        return new ReviewResult(ReviewOutcome.Success, ticket.Version, null);
    }

    // The suggestion entity keeps the AI values; edits live only as rows, so the AI value stays recoverable.
    private static string? ApplyEdits(
        TriageDbContext db,
        TriageSuggestionEntity suggestion,
        List<SuggestionEditEntity> existing,
        ReviewEdits edits,
        DateTime now)
    {
        var changes = new List<(SuggestionField Field, string? Ai, string? Final)>();

        if (edits.WorkType is { } workType)
        {
            if (!Enum.IsDefined(workType))
            {
                return "Unknown work type.";
            }

            changes.Add((SuggestionField.WorkType, suggestion.WorkType.ToString(), workType.ToString()));
        }

        if (edits.AffectedServices is { } services)
        {
            var canonical = new List<string>();
            foreach (var name in services)
            {
                var definition = name is null ? null : ServiceCatalog.Find(name.Trim());
                if (definition is null)
                {
                    return $"Unknown service '{name}'.";
                }

                if (!canonical.Contains(definition.Name, StringComparer.Ordinal))
                {
                    canonical.Add(definition.Name);
                }
            }

            if (canonical.Count == 0)
            {
                return "At least one affected service is required.";
            }

            changes.Add((SuggestionField.AffectedServices, JsonSerializer.Serialize(suggestion.AffectedServices, Json), JsonSerializer.Serialize(canonical, Json)));
        }

        if (edits.ServiceTeams is { } team)
        {
            var canonicalTeam = ServiceTeamCatalog.Find(team);
            if (canonicalTeam is null)
            {
                return "Unknown service team.";
            }

            changes.Add((SuggestionField.ServiceTeams, JsonSerializer.Serialize(suggestion.ServiceTeams, Json), JsonSerializer.Serialize(new[] { canonicalTeam }, Json)));
        }

        if (edits.Assignee is { } assignee)
        {
            var cleaned = ServiceTeamCatalog.NormalizeAssignee(assignee);
            if (cleaned is null)
            {
                return "The assignee must be 1-100 characters.";
            }

            changes.Add((SuggestionField.Assignee, suggestion.Assignee, cleaned));
        }

        if (edits.Urgency is { } urgency)
        {
            if (!Enum.IsDefined(urgency))
            {
                return "Unknown urgency.";
            }

            changes.Add((SuggestionField.Urgency, suggestion.Urgency.ToString(), urgency.ToString()));
        }

        if (edits.Impact is { } impact)
        {
            if (!Enum.IsDefined(impact))
            {
                return "Unknown impact.";
            }

            changes.Add((SuggestionField.Impact, suggestion.Impact.ToString(), impact.ToString()));
        }

        if (edits.ResolutionStatus is { } status)
        {
            if (!Enum.IsDefined(status))
            {
                return "Unknown resolution status.";
            }

            changes.Add((SuggestionField.ResolutionStatus, suggestion.ResolutionStatus?.ToString(), status.ToString()));
        }

        if (edits.DraftComment is { } comment)
        {
            var trimmed = comment.Trim();
            if (trimmed.Length is 0 or > MaxCommentLength)
            {
                return $"The draft comment must be 1-{MaxCommentLength} characters.";
            }

            changes.Add((SuggestionField.DraftComment, suggestion.DraftComment, trimmed));
        }

        foreach (var (field, ai, final) in changes)
        {
            var name = field.ToString();
            var row = existing.SingleOrDefault(e => e.Field == name);
            if (string.Equals(ai, final, StringComparison.Ordinal))
            {
                if (row is not null)
                {
                    db.SuggestionEdits.Remove(row);
                }
            }
            else if (row is null)
            {
                db.SuggestionEdits.Add(new SuggestionEditEntity
                {
                    TicketId = suggestion.TicketId,
                    Field = name,
                    AiValue = ai,
                    FinalValue = final,
                    EditedAtUtc = now,
                });
            }
            else
            {
                row.AiValue = ai;
                row.FinalValue = final;
                row.EditedAtUtc = now;
            }
        }

        return null;
    }

    private static TriageSuggestion Apply(TriageSuggestion ai, IReadOnlyList<SuggestionEditInfo> edits)
    {
        var result = ai;
        foreach (var edit in edits)
        {
            var value = edit.FinalValue;
            result = edit.Field switch
            {
                SuggestionField.WorkType => result with { WorkType = Enum.Parse<WorkType>(value!) },
                SuggestionField.AffectedServices => result with { AffectedServices = JsonSerializer.Deserialize<List<string>>(value!, Json) ?? [] },
                SuggestionField.ServiceTeams => result with { ServiceTeams = JsonSerializer.Deserialize<List<string>>(value!, Json) ?? [] },
                SuggestionField.Assignee => result with { Assignee = value },
                SuggestionField.Urgency => result with { Urgency = Enum.Parse<Urgency>(value!) },
                SuggestionField.Impact => result with { Impact = Enum.Parse<Impact>(value!) },
                SuggestionField.ResolutionStatus => result with { ResolutionStatus = Enum.Parse<ResolutionStatus>(value!) },
                SuggestionField.DraftComment => result with { DraftComment = value },
                _ => result,
            };
        }

        return result;
    }

    private static Ticket ToTicket(TicketEntity entity)
    {
        Ticket? ticket = null;
        try
        {
            ticket = entity.SourcePayload is null ? null : JsonSerializer.Deserialize<Ticket>(entity.SourcePayload, Json);
        }
        catch (JsonException)
        {
            // A corrupt payload must not make the ticket unopenable; the entity's own columns are the fallback.
        }

        return (ticket ?? new Ticket { Summary = entity.Summary, Description = entity.Description }) with
        {
            Id = entity.Id,
            Key = SuggestionMapper.TicketKey(entity.Id),
        };
    }

    private static string StatusName(int statusId) => statusId switch
    {
        TicketStatusIds.New => "New",
        TicketStatusIds.Reviewing => "Reviewing",
        TicketStatusIds.Reviewed => "Reviewed",
        TicketStatusIds.HumanRejected => "HumanRejected",
        TicketStatusIds.HumanApproved => "HumanApproved",
        _ => statusId.ToString(CultureInfo.InvariantCulture),
    };

    private static ReviewResult Result(ReviewOutcome outcome, string message) => new(outcome, null, message);

    private DateTime Now() => timeProvider.GetUtcNow().UtcDateTime;
}
