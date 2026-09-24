using Microsoft.Extensions.Logging.Abstractions;
using TicketTriage.Core.Domain;
using TicketTriage.Infrastructure.Persistence;
using TicketTriage.Web.Tests.Support;
using TicketTriage.Web.Triage;

namespace TicketTriage.Web.Tests;

public sealed class TriageBoardQueryTests : IAsyncLifetime
{
    private TestDatabase _database = null!;
    private LookupCatalog _catalog = null!;
    private TriageSessionStore _store = null!;
    private TriageBoardQuery _query = null!;

    public async ValueTask InitializeAsync()
    {
        var cancellationToken = Xunit.TestContext.Current.CancellationToken;
        _database = await TestDatabase.CreateAsync(cancellationToken);
        _catalog = new LookupCatalog(_database.CreateFactory());
        await _catalog.EnsureLoadedAsync(cancellationToken);
        _store = new TriageSessionStore(NullLogger<TriageSessionStore>.Instance);
        _query = new TriageBoardQuery(_database.CreateFactory(), _catalog, _store);
    }

    public async ValueTask DisposeAsync() => await _database.DisposeAsync();

    [Fact]
    public async Task GetRowsAsync_FinishedTrainingTicket_NeverIncluded_PerFR13()
    {
        var cancellationToken = Xunit.TestContext.Current.CancellationToken;
        await using (var db = _database.CreateContext())
        {
            db.Tickets.Add(new TicketEntity
            {
                WorkTypeId = _catalog.DefaultWorkTypeId,
                Summary = "Training ticket",
                StatusId = _catalog.FinishedStatusId,
                CreatedDate = DateTime.UtcNow,
            });
            await db.SaveChangesAsync(cancellationToken);
        }

        var rows = await _query.GetRowsAsync(cancellationToken);

        rows.Should().BeEmpty();
    }

    [Fact]
    public async Task GetRowsAsync_PlainNewTicket_NotInSessionNorAnalysed_IsExcluded()
    {
        var cancellationToken = Xunit.TestContext.Current.CancellationToken;
        await using (var db = _database.CreateContext())
        {
            db.Tickets.Add(new TicketEntity
            {
                WorkTypeId = _catalog.DefaultWorkTypeId,
                Summary = "Never uploaded this session",
                StatusId = _catalog.NewStatusId,
                CreatedDate = DateTime.UtcNow,
            });
            await db.SaveChangesAsync(cancellationToken);
        }

        var rows = await _query.GetRowsAsync(cancellationToken);

        rows.Should().BeEmpty();
    }

    [Fact]
    public async Task GetRowsAsync_QueuedSessionTicket_IsIncludedWithIssueKey()
    {
        var cancellationToken = Xunit.TestContext.Current.CancellationToken;
        int ticketId;
        await using (var db = _database.CreateContext())
        {
            var ticket = new TicketEntity
            {
                WorkTypeId = _catalog.DefaultWorkTypeId,
                Summary = "Freshly uploaded",
                StatusId = _catalog.NewStatusId,
                CreatedDate = DateTime.UtcNow,
            };
            db.Tickets.Add(ticket);
            await db.SaveChangesAsync(cancellationToken);
            ticketId = ticket.Id;
        }

        _store.Register(ticketId, "TT-1", new Ticket { Key = "TT-1", Summary = "Freshly uploaded" }, uploadId: 1);

        var rows = await _query.GetRowsAsync(cancellationToken);

        rows.Should().ContainSingle(r => r.Id == ticketId && r.IssueKey == "TT-1" && r.State == TicketDisplayState.Queued);
    }

    [Fact]
    public async Task GetDecisionTotalsAsync_CountsApprovedAndRejected_PerFR22()
    {
        var cancellationToken = Xunit.TestContext.Current.CancellationToken;
        await using (var db = _database.CreateContext())
        {
            db.Tickets.AddRange(
                new TicketEntity { WorkTypeId = _catalog.DefaultWorkTypeId, Summary = "Approved 1", StatusId = _catalog.HumanApprovedStatusId, CreatedDate = DateTime.UtcNow },
                new TicketEntity { WorkTypeId = _catalog.DefaultWorkTypeId, Summary = "Approved 2", StatusId = _catalog.HumanApprovedStatusId, CreatedDate = DateTime.UtcNow },
                new TicketEntity { WorkTypeId = _catalog.DefaultWorkTypeId, Summary = "Rejected 1", StatusId = _catalog.HumanRejectedStatusId, CreatedDate = DateTime.UtcNow });
            await db.SaveChangesAsync(cancellationToken);
        }

        var totals = await _query.GetDecisionTotalsAsync(cancellationToken);

        totals.Approved.Should().Be(2);
        totals.Rejected.Should().Be(1);
        totals.ApprovalRate.Should().BeApproximately(2.0 / 3.0, 0.0001);
    }

    [Fact]
    public async Task GetDecisionTotalsAsync_NoDecisions_ApprovalRateIsNull_PerFR22()
    {
        var totals = await _query.GetDecisionTotalsAsync(Xunit.TestContext.Current.CancellationToken);

        totals.ApprovalRate.Should().BeNull();
    }
}
