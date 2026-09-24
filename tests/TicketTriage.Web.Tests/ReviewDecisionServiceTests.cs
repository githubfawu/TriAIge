using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using TicketTriage.Core.Domain;
using TicketTriage.Infrastructure.Persistence;
using TicketTriage.Web.Tests.Support;
using TicketTriage.Web.Triage;

namespace TicketTriage.Web.Tests;

public sealed class ReviewDecisionServiceTests : IAsyncLifetime
{
    private TestDatabase _database = null!;
    private LookupCatalog _catalog = null!;
    private TriageSessionStore _store = null!;
    private ReviewDecisionService _decisions = null!;

    public async ValueTask InitializeAsync()
    {
        var cancellationToken = Xunit.TestContext.Current.CancellationToken;
        _database = await TestDatabase.CreateAsync(cancellationToken);
        _catalog = new LookupCatalog(_database.CreateFactory());
        await _catalog.EnsureLoadedAsync(cancellationToken);
        _store = new TriageSessionStore(NullLogger<TriageSessionStore>.Instance);
        _decisions = new ReviewDecisionService(_database.CreateFactory(), _catalog, _store);
    }

    public async ValueTask DisposeAsync() => await _database.DisposeAsync();

    private async Task<int> InsertPendingTicketAsync(CancellationToken cancellationToken)
    {
        await using var db = _database.CreateContext();
        var ticket = new TicketEntity
        {
            WorkTypeId = _catalog.DefaultWorkTypeId,
            Summary = "Original summary",
            AssigneeChanged = null,
            StatusId = _catalog.NewStatusId,
            CreatedDate = DateTime.UtcNow,
            // A suggestion already present (mirrors what SuggestionWriter would have written, FR7).
            WorkTypeChangedId = _catalog.FindWorkTypeId("Service Request"),
            UrgencyChangedId = _catalog.FindUrgencyId("High"),
            ImpactChangedId = _catalog.FindImpactId("Highest"),
            PriorityChangedId = _catalog.FindPriorityId("Highest"),
            ServiceTeamChangedId = _catalog.FindServiceTeamId("Service Desk"),
            AffectedBusinessOrITServiceChangedId = _catalog.FindAffectedServiceId("Outlook & Email"),
        };
        db.Tickets.Add(ticket);
        await db.SaveChangesAsync(cancellationToken);
        return ticket.Id;
    }

    private ReviewFormModel MakeForm(int ticketId, string? comment = null)
    {
        var baseline = new ReviewFormSnapshot(
            WorkType.ServiceRequest,
            "Outlook & Email",
            "Service Desk",
            "Dana Keller",
            Urgency.High,
            Impact.Major,
            ResolutionStatus.Done,
            comment);
        return new ReviewFormModel(baseline);
    }

    [Fact]
    public async Task ApproveAsync_Accept_PerAC7()
    {
        var cancellationToken = Xunit.TestContext.Current.CancellationToken;
        var ticketId = await InsertPendingTicketAsync(cancellationToken);
        var form = MakeForm(ticketId, comment: "Resolved by restarting the service.");

        var result = await _decisions.ApproveAsync(ticketId, form, cancellationToken);

        result.Outcome.Should().Be(DecisionOutcome.Saved);

        await using var db = _database.CreateContext();
        var ticket = await db.Tickets.Include(t => t.Comments).AsNoTracking().SingleAsync(t => t.Id == ticketId, cancellationToken);

        ticket.StatusId.Should().Be(_catalog.HumanApprovedStatusId);
        ticket.WorkTypeId.Should().Be(_catalog.FindWorkTypeId("Service Request"));
        ticket.ServiceTeamId.Should().Be(_catalog.FindServiceTeamId("Service Desk"));
        ticket.AffectedBusinessOrITServiceId.Should().Be(_catalog.FindAffectedServiceId("Outlook & Email"));
        ticket.Assignee.Should().Be("Dana Keller");
        ticket.UrgencyId.Should().Be(_catalog.FindUrgencyId("High"));
        ticket.ImpactId.Should().Be(_catalog.FindImpactId("Highest"));
        ticket.PriorityId.Should().Be(_catalog.FindPriorityId("Highest")); // High x Major = Highest, via matrix
        ticket.Resolution.Should().Be("Done");

        ticket.WorkTypeChangedId.Should().BeNull();
        ticket.AffectedBusinessOrITServiceChangedId.Should().BeNull();
        ticket.ServiceTeamChangedId.Should().BeNull();
        ticket.AssigneeChanged.Should().BeNull();
        ticket.UrgencyChangedId.Should().BeNull();
        ticket.ImpactChangedId.Should().BeNull();
        ticket.PriorityChangedId.Should().BeNull();
        ticket.ResolutionChanged.Should().BeNull();

        ticket.Comments.Should().ContainSingle().Which.CommentText.Should().Be("Resolved by restarting the service.");
    }

    [Fact]
    public async Task ApproveAsync_NoResolutionChosen_KeepsOriginalResolutionColumn_PerPlanSection0()
    {
        var cancellationToken = Xunit.TestContext.Current.CancellationToken;
        var ticketId = await InsertPendingTicketAsync(cancellationToken);
        await using (var db = _database.CreateContext())
        {
            var ticket = await db.Tickets.SingleAsync(t => t.Id == ticketId, cancellationToken);
            ticket.Resolution = "Pre-existing resolution text";
            await db.SaveChangesAsync(cancellationToken);
        }

        var baseline = new ReviewFormSnapshot(WorkType.ServiceRequest, "Outlook & Email", "Service Desk", "Dana Keller", Urgency.High, Impact.Major, Resolution: null, Comment: null);
        var form = new ReviewFormModel(baseline);

        await _decisions.ApproveAsync(ticketId, form, cancellationToken);

        await using var verifyDb = _database.CreateContext();
        var updated = await verifyDb.Tickets.AsNoTracking().SingleAsync(t => t.Id == ticketId, cancellationToken);
        updated.Resolution.Should().Be("Pre-existing resolution text");
    }

    [Fact]
    public async Task ApproveAsync_EmptyComment_DoesNotCreateCommentRow()
    {
        var cancellationToken = Xunit.TestContext.Current.CancellationToken;
        var ticketId = await InsertPendingTicketAsync(cancellationToken);
        var form = MakeForm(ticketId, comment: "   ");

        await _decisions.ApproveAsync(ticketId, form, cancellationToken);

        await using var db = _database.CreateContext();
        var count = await db.Comments.CountAsync(c => c.TicketId == ticketId, cancellationToken);
        count.Should().Be(0);
    }

    [Fact]
    public async Task ApproveAsync_WithEdits_RecordsEditedFieldsInStore_PerAC8()
    {
        var cancellationToken = Xunit.TestContext.Current.CancellationToken;
        var ticketId = await InsertPendingTicketAsync(cancellationToken);
        _store.Register(ticketId, "TT-1", new Ticket { Key = "TT-1", Summary = "s" }, uploadId: 1);
        await _store.DequeueAsync(cancellationToken);
        _store.CompleteAnalysis(ticketId, new TriageSuggestion { TicketKey = "TT-1", WorkType = WorkType.Incident, Urgency = Urgency.Medium, Impact = Impact.Moderate }, []);

        var form = MakeForm(ticketId);
        form.Assignee = "Someone Else"; // one edit away from the suggestion

        await _decisions.ApproveAsync(ticketId, form, cancellationToken);

        var entry = _store.Get(ticketId);
        entry.Should().NotBeNull();
        entry!.EditedFields.Should().ContainSingle().Which.Should().Be(nameof(ReviewField.Assignee));
    }

    [Fact]
    public async Task RejectAsync_EmptyReason_Throws_PerAC9()
    {
        var cancellationToken = Xunit.TestContext.Current.CancellationToken;
        var ticketId = await InsertPendingTicketAsync(cancellationToken);

        var act = async () => await _decisions.RejectAsync(ticketId, "   ", cancellationToken);

        await act.Should().ThrowAsync<ArgumentException>();
    }

    [Fact]
    public async Task RejectAsync_WithReason_SetsHumanRejected_OriginalUnchanged_PerAC9()
    {
        var cancellationToken = Xunit.TestContext.Current.CancellationToken;
        var ticketId = await InsertPendingTicketAsync(cancellationToken);

        var result = await _decisions.RejectAsync(ticketId, "Duplicate of TT-9", cancellationToken);

        result.Outcome.Should().Be(DecisionOutcome.Saved);

        await using var db = _database.CreateContext();
        var ticket = await db.Tickets.AsNoTracking().SingleAsync(t => t.Id == ticketId, cancellationToken);
        ticket.StatusId.Should().Be(_catalog.HumanRejectedStatusId);
        ticket.WorkTypeId.Should().Be(_catalog.DefaultWorkTypeId); // original untouched
        ticket.WorkTypeChangedId.Should().BeNull();
        ticket.UrgencyChangedId.Should().BeNull();

        _store.Get(ticketId)?.RejectReason.Should().Be("Duplicate of TT-9");
    }

    [Fact]
    public async Task ApproveAsync_TicketAlreadyDecided_ReturnsAlreadyDecided_DbUnchanged_PerAC10()
    {
        var cancellationToken = Xunit.TestContext.Current.CancellationToken;
        var ticketId = await InsertPendingTicketAsync(cancellationToken);

        var firstForm = MakeForm(ticketId);
        var firstResult = await _decisions.ApproveAsync(ticketId, firstForm, cancellationToken);
        firstResult.Outcome.Should().Be(DecisionOutcome.Saved);

        TicketEntity Snapshot()
        {
            using var db = _database.CreateContext();
            return db.Tickets.AsNoTracking().Single(t => t.Id == ticketId);
        }

        var before = Snapshot();

        var secondForm = MakeForm(ticketId);
        secondForm.Assignee = "Someone Completely Different";
        var secondResult = await _decisions.ApproveAsync(ticketId, secondForm, cancellationToken);

        secondResult.Outcome.Should().Be(DecisionOutcome.AlreadyDecided);

        var after = Snapshot();
        after.Assignee.Should().Be(before.Assignee);
        after.StatusId.Should().Be(before.StatusId);
    }
}
