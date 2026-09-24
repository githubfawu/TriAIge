using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using TicketTriage.Infrastructure.Persistence;
using TicketTriage.Web.Tests.Support;
using TicketTriage.Web.Triage;

namespace TicketTriage.Web.Tests;

public sealed class UploadIngestServiceTests : IAsyncLifetime
{
    private static readonly string FixturePath = Path.Combine(AppContext.BaseDirectory, "Fixtures", "challenge-sample.json");

    private TestDatabase _database = null!;
    private LookupCatalog _catalog = null!;
    private TriageSessionStore _store = null!;
    private UploadIngestService _service = null!;

    public async ValueTask InitializeAsync()
    {
        var cancellationToken = Xunit.TestContext.Current.CancellationToken;
        _database = await TestDatabase.CreateAsync(cancellationToken);
        _catalog = new LookupCatalog(_database.CreateFactory());
        await _catalog.EnsureLoadedAsync(cancellationToken);
        _store = new TriageSessionStore(NullLogger<TriageSessionStore>.Instance);
        _service = new UploadIngestService(_database.CreateFactory(), _catalog, _store, TimeProvider.System);
    }

    public async ValueTask DisposeAsync() => await _database.DisposeAsync();

    [Fact]
    public async Task PreviewAsync_FixtureFile_FiveValidRows_NothingSaved_PerAC1()
    {
        var cancellationToken = Xunit.TestContext.Current.CancellationToken;
        var bytes = await File.ReadAllBytesAsync(FixturePath, cancellationToken);

        var preview = await _service.PreviewAsync(bytes, cancellationToken);

        preview.FileError.Should().BeNull();
        preview.ValidCount.Should().Be(5);

        await using var db = _database.CreateContext();
        (await db.Tickets.CountAsync(cancellationToken)).Should().Be(0);
    }

    [Fact]
    public async Task SaveAndEnqueueAsync_WithoutTrainingData_ReturnsConfirmationRequired_NothingWritten_PerAC5()
    {
        var cancellationToken = Xunit.TestContext.Current.CancellationToken;
        var preview = await _service.PreviewAsync(await File.ReadAllBytesAsync(FixturePath, cancellationToken), cancellationToken);
        preview.TrainingDataPresent.Should().BeFalse();

        var result = await _service.SaveAndEnqueueAsync(preview, confirmedWithoutTrainingData: false, cancellationToken);

        result.Outcome.Should().Be(SaveOutcome.ConfirmationRequired);
        await using var db = _database.CreateContext();
        (await db.Tickets.CountAsync(cancellationToken)).Should().Be(0);
    }

    [Fact]
    public async Task SaveAndEnqueueAsync_ConfirmedWithoutTrainingData_SavesAndRegisters_PerAC5()
    {
        var cancellationToken = Xunit.TestContext.Current.CancellationToken;
        var preview = await _service.PreviewAsync(await File.ReadAllBytesAsync(FixturePath, cancellationToken), cancellationToken);

        var result = await _service.SaveAndEnqueueAsync(preview, confirmedWithoutTrainingData: true, cancellationToken);

        result.Outcome.Should().Be(SaveOutcome.Saved);
        result.SavedCount.Should().Be(5);

        await using var db = _database.CreateContext();
        (await db.Tickets.CountAsync(cancellationToken)).Should().Be(5);
        (await db.Tickets.CountAsync(t => t.StatusId == _catalog.NewStatusId, cancellationToken)).Should().Be(5);

        _store.GetUploadProgress(result.UploadId!.Value).Total.Should().Be(5);
    }

    [Fact]
    public async Task SaveAndEnqueueAsync_TrainingDataPresent_MapsImpactAndTruncatesText_PerImporterRules()
    {
        var cancellationToken = Xunit.TestContext.Current.CancellationToken;
        await SeedFinishedTicketAsync(cancellationToken);

        var preview = await _service.PreviewAsync(await File.ReadAllBytesAsync(FixturePath, cancellationToken), cancellationToken);
        preview.TrainingDataPresent.Should().BeTrue();

        var result = await _service.SaveAndEnqueueAsync(preview, confirmedWithoutTrainingData: false, cancellationToken);
        result.Outcome.Should().Be(SaveOutcome.Saved);

        await using var db = _database.CreateContext();
        var truncated = await db.Tickets.AsNoTracking().SingleAsync(t => t.Summary.Length == TicketMapper.SummaryMaxLength, cancellationToken);
        truncated.StatusId.Should().Be(_catalog.NewStatusId); // Slice 1 always ingests as New, even though it has a length that would be "Finished" data-shaped.

        var significant = await db.Tickets.AsNoTracking().SingleAsync(t => t.ImpactId == _catalog.FindImpactId("High"), cancellationToken);
        significant.Should().NotBeNull(); // "Significant" (TT-1001) translated to DB "High".
    }

    [Fact]
    public async Task EnqueueExistingAsync_NewTicketNotInQueue_RegistersItInStore_PerFR10()
    {
        var cancellationToken = Xunit.TestContext.Current.CancellationToken;
        int ticketId;
        await using (var db = _database.CreateContext())
        {
            var ticket = new TicketEntity
            {
                WorkTypeId = _catalog.DefaultWorkTypeId,
                Summary = "Ticket that fell out of the RAM queue",
                StatusId = _catalog.NewStatusId,
                CreatedDate = DateTime.UtcNow,
            };
            db.Tickets.Add(ticket);
            await db.SaveChangesAsync(cancellationToken);
            ticketId = ticket.Id;
        }

        var enqueued = await _service.EnqueueExistingAsync(ticketId, cancellationToken);

        enqueued.Should().BeTrue();
        _store.Get(ticketId).Should().NotBeNull();
        _store.Get(ticketId)!.Phase.Should().Be(QueuePhase.Queued);
    }

    [Fact]
    public async Task EnqueueExistingAsync_TicketAlreadyHasSuggestion_ReturnsFalse_DoesNotRegister()
    {
        var cancellationToken = Xunit.TestContext.Current.CancellationToken;
        int ticketId;
        await using (var db = _database.CreateContext())
        {
            var ticket = new TicketEntity
            {
                WorkTypeId = _catalog.DefaultWorkTypeId,
                Summary = "Already analysed",
                StatusId = _catalog.NewStatusId,
                CreatedDate = DateTime.UtcNow,
                WorkTypeChangedId = _catalog.DefaultWorkTypeId,
            };
            db.Tickets.Add(ticket);
            await db.SaveChangesAsync(cancellationToken);
            ticketId = ticket.Id;
        }

        var enqueued = await _service.EnqueueExistingAsync(ticketId, cancellationToken);

        enqueued.Should().BeFalse();
        _store.Get(ticketId).Should().BeNull();
    }

    [Fact]
    public async Task SaveAndEnqueueAsync_SameFileAfterRestart_ZeroNewRows_RequeuesUnanalysed_SkipsDecided_PerAC4()
    {
        var cancellationToken = Xunit.TestContext.Current.CancellationToken;
        await SeedFinishedTicketAsync(cancellationToken);
        var bytes = await File.ReadAllBytesAsync(FixturePath, cancellationToken);

        var firstPreview = await _service.PreviewAsync(bytes, cancellationToken);
        var firstResult = await _service.SaveAndEnqueueAsync(firstPreview, confirmedWithoutTrainingData: false, cancellationToken);
        firstResult.Outcome.Should().Be(SaveOutcome.Saved);
        firstResult.SavedCount.Should().Be(5);

        // Simulate what a running app would have done to 3 of the 5 tickets before "restarting" (clearing RAM):
        // one decided each way, one analysed-but-still-Pending, two left untouched.
        int approvedId, rejectedId, pendingId, unanalysedId1, unanalysedId2;
        await using (var db = _database.CreateContext())
        {
            var tickets = await db.Tickets
                .Where(t => t.StatusId == _catalog.NewStatusId)
                .OrderBy(t => t.Id)
                .ToListAsync(cancellationToken);
            tickets.Should().HaveCount(5);

            approvedId = tickets[0].Id;
            rejectedId = tickets[1].Id;
            pendingId = tickets[2].Id;
            unanalysedId1 = tickets[3].Id;
            unanalysedId2 = tickets[4].Id;

            tickets[0].StatusId = _catalog.HumanApprovedStatusId;
            tickets[1].StatusId = _catalog.HumanRejectedStatusId;
            tickets[2].WorkTypeChangedId = _catalog.DefaultWorkTypeId;
            await db.SaveChangesAsync(cancellationToken);
        }

        // "Restart": a brand-new RAM store and service, same DB (Technical Constraints: RAM-Store is lost, DB isn't).
        var restartedStore = new TriageSessionStore(NullLogger<TriageSessionStore>.Instance);
        var restartedService = new UploadIngestService(_database.CreateFactory(), _catalog, restartedStore, TimeProvider.System);

        var secondPreview = await restartedService.PreviewAsync(bytes, cancellationToken);
        secondPreview.Entries.Should().HaveCount(5);
        secondPreview.Entries[0].DbMatch.Should().Be(DbMatchKind.AlreadyInTriage);
        secondPreview.Entries[0].Reason.Should().Contain("Approved");
        secondPreview.Entries[1].DbMatch.Should().Be(DbMatchKind.AlreadyInTriage);
        secondPreview.Entries[1].Reason.Should().Contain("Rejected");
        secondPreview.Entries[2].DbMatch.Should().Be(DbMatchKind.AlreadyInTriage);
        secondPreview.Entries[2].Reason.Should().Contain("Pending");
        secondPreview.Entries[3].DbMatch.Should().Be(DbMatchKind.Requeue);
        secondPreview.Entries[4].DbMatch.Should().Be(DbMatchKind.Requeue);

        var secondResult = await restartedService.SaveAndEnqueueAsync(secondPreview, confirmedWithoutTrainingData: false, cancellationToken);
        secondResult.Outcome.Should().Be(SaveOutcome.Saved);
        secondResult.SavedCount.Should().Be(2); // only the two un-analysed tickets are (re-)registered, nothing new

        await using (var db = _database.CreateContext())
        {
            (await db.Tickets.CountAsync(cancellationToken)).Should().Be(6); // 5 from the first save + 1 Finished, no new rows
        }

        restartedStore.Get(unanalysedId1)!.Phase.Should().Be(QueuePhase.Queued);
        restartedStore.Get(unanalysedId2)!.Phase.Should().Be(QueuePhase.Queued);
        restartedStore.Get(approvedId).Should().BeNull();
        restartedStore.Get(rejectedId).Should().BeNull();
        restartedStore.Get(pendingId).Should().BeNull();
    }

    [Fact]
    public async Task PreviewAsync_MatchOnlyAgainstFinishedTraining_IsNeverADuplicate_PerFR4()
    {
        var cancellationToken = Xunit.TestContext.Current.CancellationToken;

        // A Finished (training) ticket with the exact same summary/description as fixture entry #0.
        await using (var db = _database.CreateContext())
        {
            db.Tickets.Add(new TicketEntity
            {
                WorkTypeId = _catalog.DefaultWorkTypeId,
                Summary = "Outlook keeps freezing when opening shared calendars",
                Description = "Several analysts report that Outlook hangs for 30+ seconds whenever a shared calendar is opened.",
                StatusId = _catalog.FinishedStatusId,
                CreatedDate = DateTime.UtcNow,
            });
            await db.SaveChangesAsync(cancellationToken);
        }

        var preview = await _service.PreviewAsync(await File.ReadAllBytesAsync(FixturePath, cancellationToken), cancellationToken);

        preview.Entries[0].DbMatch.Should().Be(DbMatchKind.New);
    }

    private async Task SeedFinishedTicketAsync(CancellationToken cancellationToken)
    {
        await using var db = _database.CreateContext();
        db.Tickets.Add(new TicketEntity
        {
            WorkTypeId = _catalog.DefaultWorkTypeId,
            Summary = "Historical training ticket",
            StatusId = _catalog.FinishedStatusId,
            CreatedDate = DateTime.UtcNow,
        });
        await db.SaveChangesAsync(cancellationToken);
    }
}
