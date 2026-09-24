using Microsoft.EntityFrameworkCore;
using TicketTriage.Core.Domain;
using TicketTriage.Infrastructure.Persistence;

namespace TicketTriage.Web.Triage;

/// <summary>What an upload row will do to the DB (FR4) - shown as the "DB" column on the Upload page. Only set
/// (non-null) for entries that parsed as valid; a JSON-level invalid entry has no DB outcome at all.</summary>
public enum DbMatchKind
{
    New,
    Requeue,
    AlreadyInTriage,
}

public sealed record UploadPreviewEntry(
    int Index,
    string IssueKey,
    string Summary,
    bool IsValid,
    string? Reason,
    IReadOnlyList<string> Hints,
    Ticket? Ticket,
    TicketEntity? Entity,
    DbMatchKind? DbMatch = null,
    int? MatchedTicketId = null);

public sealed record UploadPreview(IReadOnlyList<UploadPreviewEntry> Entries, bool TrainingDataPresent, string? FileError)
{
    public int ValidCount => Entries.Count(e => e.IsValid);
}

public enum SaveOutcome
{
    Saved,
    ConfirmationRequired,
    NoValidEntries,
}

public sealed record SaveResult(SaveOutcome Outcome, int SavedCount, int? UploadId = null);

public interface IUploadIngestService
{
    /// <summary>Parses and maps the file; nothing is saved (FR1/FR2).</summary>
    Task<UploadPreview> PreviewAsync(byte[] content, CancellationToken cancellationToken);

    /// <summary>Saves every valid entry in one transaction and enqueues it only after the commit (FR3, Leitplanke 3).
    /// Refuses (<see cref="SaveOutcome.ConfirmationRequired"/>) if training data looks missing and the caller
    /// hasn't confirmed (FR5).</summary>
    Task<SaveResult> SaveAndEnqueueAsync(UploadPreview preview, bool confirmedWithoutTrainingData, CancellationToken cancellationToken);

    /// <summary>"Analyse now" (FR10): re-enqueues a New ticket that isn't in the RAM queue (e.g. after a restart),
    /// rebuilding its Core <see cref="Ticket"/> from the DB row. No-op (returns false) if the ticket is gone, no
    /// longer New, or already has a suggestion.</summary>
    Task<bool> EnqueueExistingAsync(int ticketId, CancellationToken cancellationToken);
}

/// <summary>Scoped per-request service backing the Upload page. Duplicates against the DB (FR4, Slice 3) are
/// resolved by <see cref="ApplyDedupeAsync"/>, run once for the preview and again inside the save transaction
/// (Leitplanke: the preview and the save round-trip are separate requests, so the DB can have changed between
/// them).</summary>
public sealed class UploadIngestService(
    IDbContextFactory<TriageDbContext> dbFactory,
    LookupCatalog catalog,
    TriageSessionStore store,
    TimeProvider timeProvider) : IUploadIngestService
{
    // RAM-only correlation id for the upload progress bar (Technical Constraints: no new DB column for it).
    private static int _uploadSequence;

    public async Task<UploadPreview> PreviewAsync(byte[] content, CancellationToken cancellationToken)
    {
        await catalog.EnsureLoadedAsync(cancellationToken);

        var parsed = UploadParser.Parse(content);
        if (parsed.FileError is not null)
        {
            return new UploadPreview([], TrainingDataPresent: true, parsed.FileError);
        }

        var entries = parsed.Entries.Select(entry => ToPreviewEntry(entry, catalog, timeProvider)).ToList();

        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var trainingDataPresent = await db.Tickets.AnyAsync(t => t.StatusId == catalog.FinishedStatusId, cancellationToken);

        var deduped = await ApplyDedupeAsync(entries, db, cancellationToken);

        return new UploadPreview(deduped, trainingDataPresent, FileError: null);
    }

    public async Task<SaveResult> SaveAndEnqueueAsync(UploadPreview preview, bool confirmedWithoutTrainingData, CancellationToken cancellationToken)
    {
        var candidateEntries = preview.Entries.Where(e => e.IsValid).ToList();
        if (candidateEntries.Count == 0)
        {
            return new SaveResult(SaveOutcome.NoValidEntries, 0);
        }

        if (!preview.TrainingDataPresent && !confirmedWithoutTrainingData)
        {
            return new SaveResult(SaveOutcome.ConfirmationRequired, 0);
        }

        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);

        // Re-run the dedupe check inside the transaction (FR4/Leitplanke): the preview happened on an earlier
        // request, so another upload (or a restart) may have changed the DB in the meantime.
        var deduped = await ApplyDedupeAsync(candidateEntries, db, cancellationToken);
        var toInsert = deduped.Where(e => e.DbMatch == DbMatchKind.New).ToList();
        var toRequeue = deduped.Where(e => e.DbMatch == DbMatchKind.Requeue).ToList();

        db.Tickets.AddRange(toInsert.Select(e => e.Entity!));
        await db.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);

        var uploadId = Interlocked.Increment(ref _uploadSequence);
        foreach (var entry in toInsert)
        {
            // Registered only after the commit (Leitplanke 3/FR3): the worker can never see an un-persisted ticket.
            store.Register(entry.Entity!.Id, entry.IssueKey, entry.Ticket!, uploadId);
        }

        foreach (var entry in toRequeue)
        {
            // No insert (FR4): a match without a suggestion or decision is re-enqueued under its existing id,
            // restoring the issue key in RAM (e.g. after a restart cleared the queue).
            store.Register(entry.MatchedTicketId!.Value, entry.IssueKey, entry.Ticket!, uploadId);
        }

        return new SaveResult(SaveOutcome.Saved, toInsert.Count + toRequeue.Count, uploadId);
    }

    /// <summary>Resolves each valid entry against existing DB rows (FR4): one query for candidates sharing a
    /// summary, then an in-memory comparison of the (already truncated) description and - only if the source
    /// ticket had one - the created date. Finished (training) tickets are excluded up front and are therefore
    /// never treated as duplicates.</summary>
    private async Task<List<UploadPreviewEntry>> ApplyDedupeAsync(
        IReadOnlyList<UploadPreviewEntry> entries, TriageDbContext db, CancellationToken cancellationToken)
    {
        var validEntries = entries.Where(e => e.IsValid).ToList();
        if (validEntries.Count == 0)
        {
            return [.. entries];
        }

        var summaries = validEntries.Select(e => e.Entity!.Summary).Distinct().ToList();
        var candidates = await db.Tickets
            .AsNoTracking()
            .Where(t => summaries.Contains(t.Summary) && t.StatusId != catalog.FinishedStatusId)
            .ToListAsync(cancellationToken);

        if (candidates.Count == 0)
        {
            return [.. entries.Select(e => e.IsValid ? e with { DbMatch = DbMatchKind.New } : e)];
        }

        var result = new List<UploadPreviewEntry>(entries.Count);
        foreach (var entry in entries)
        {
            result.Add(entry.IsValid ? ResolveMatch(entry, FindMatch(entry, candidates)) : entry);
        }

        return result;
    }

    private static TicketEntity? FindMatch(UploadPreviewEntry entry, IReadOnlyList<TicketEntity> candidates)
    {
        var mapped = entry.Entity!;
        var hasCreated = entry.Ticket!.Created.HasValue;
        return candidates.FirstOrDefault(candidate =>
            string.Equals(candidate.Summary, mapped.Summary, StringComparison.Ordinal)
            && string.Equals(candidate.Description, mapped.Description, StringComparison.Ordinal)
            && (!hasCreated || candidate.CreatedDate == mapped.CreatedDate));
    }

    private UploadPreviewEntry ResolveMatch(UploadPreviewEntry entry, TicketEntity? match)
    {
        if (match is null)
        {
            return entry with { DbMatch = DbMatchKind.New };
        }

        string? decidedState =
            match.StatusId == catalog.HumanApprovedStatusId ? "Approved" :
            match.StatusId == catalog.HumanRejectedStatusId ? "Rejected" :
            match.StatusId == catalog.NewStatusId && TicketPredicates.HasSuggestionCompiled(match) ? "Pending" :
            null;

        if (decidedState is not null)
        {
            return entry with
            {
                IsValid = false,
                Reason = $"already in triage ({decidedState})",
                DbMatch = DbMatchKind.AlreadyInTriage,
                MatchedTicketId = match.Id,
            };
        }

        return entry with { DbMatch = DbMatchKind.Requeue, MatchedTicketId = match.Id };
    }

    public async Task<bool> EnqueueExistingAsync(int ticketId, CancellationToken cancellationToken)
    {
        await catalog.EnsureLoadedAsync(cancellationToken);

        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        var entity = await db.Tickets.AsNoTracking().SingleOrDefaultAsync(t => t.Id == ticketId, cancellationToken);
        if (entity is null || entity.StatusId != catalog.NewStatusId || TicketPredicates.HasSuggestionCompiled(entity))
        {
            return false;
        }

        var ticket = TicketMapper.ToTicket(entity, catalog);
        store.Register(ticketId, $"#{ticketId}", ticket, uploadId: 0);
        return true;
    }

    private static UploadPreviewEntry ToPreviewEntry(ParsedTicketEntry parsed, LookupCatalog catalog, TimeProvider timeProvider)
    {
        if (!parsed.IsValid || parsed.Ticket is null)
        {
            return new UploadPreviewEntry(
                parsed.Index,
                parsed.Ticket?.Key ?? "—",
                parsed.Ticket?.Summary ?? "—",
                IsValid: false,
                parsed.Reason,
                [],
                parsed.Ticket,
                Entity: null);
        }

        var mapped = TicketMapper.ToEntity(parsed.Ticket, catalog, timeProvider);
        return new UploadPreviewEntry(
            parsed.Index,
            parsed.Ticket.Key,
            mapped.Entity.Summary,
            IsValid: true,
            Reason: null,
            mapped.Hints,
            parsed.Ticket,
            mapped.Entity);
    }
}
