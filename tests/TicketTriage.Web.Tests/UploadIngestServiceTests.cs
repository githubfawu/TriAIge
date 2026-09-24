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
