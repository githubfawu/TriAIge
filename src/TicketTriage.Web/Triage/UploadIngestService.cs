using Microsoft.EntityFrameworkCore;
using TicketTriage.Core.Domain;
using TicketTriage.Infrastructure.Persistence;

namespace TicketTriage.Web.Triage;

public sealed record UploadPreviewEntry(
    int Index,
    string IssueKey,
    string Summary,
    bool IsValid,
    string? Reason,
    IReadOnlyList<string> Hints,
    Ticket? Ticket,
    TicketEntity? Entity);

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

/// <summary>Scoped per-request service backing the Upload page. DB-hit deduplication (FR4) is out of scope for
/// Slice 1 (added in Slice 3); every valid entry is inserted as a new <see cref="TicketEntity"/>.</summary>
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

        return new UploadPreview(entries, trainingDataPresent, FileError: null);
    }

    public async Task<SaveResult> SaveAndEnqueueAsync(UploadPreview preview, bool confirmedWithoutTrainingData, CancellationToken cancellationToken)
    {
        var validEntries = preview.Entries.Where(e => e.IsValid).ToList();
        if (validEntries.Count == 0)
        {
            return new SaveResult(SaveOutcome.NoValidEntries, 0);
        }

        if (!preview.TrainingDataPresent && !confirmedWithoutTrainingData)
        {
            return new SaveResult(SaveOutcome.ConfirmationRequired, 0);
        }

        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);

        db.Tickets.AddRange(validEntries.Select(e => e.Entity!));
        await db.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);

        var uploadId = Interlocked.Increment(ref _uploadSequence);
        foreach (var entry in validEntries)
        {
            // Registered only after the commit (Leitplanke 3/FR3): the worker can never see an un-persisted ticket.
            store.Register(entry.Entity!.Id, entry.IssueKey, entry.Ticket!, uploadId);
        }

        return new SaveResult(SaveOutcome.Saved, validEntries.Count, uploadId);
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
