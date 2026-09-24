using Microsoft.EntityFrameworkCore;
using TicketTriage.Core.Domain;
using TicketTriage.Infrastructure.Persistence;
using TicketTriage.Web.Tests.Support;
using TicketTriage.Web.Triage;

namespace TicketTriage.Web.Tests;

public sealed class SuggestionWriterTests : IAsyncLifetime
{
    private TestDatabase _database = null!;
    private LookupCatalog _catalog = null!;
    private SuggestionWriter _writer = null!;

    public async ValueTask InitializeAsync()
    {
        var cancellationToken = Xunit.TestContext.Current.CancellationToken;
        _database = await TestDatabase.CreateAsync(cancellationToken);
        _catalog = new LookupCatalog(_database.CreateFactory());
        await _catalog.EnsureLoadedAsync(cancellationToken);
        _writer = new SuggestionWriter(_database.CreateFactory(), _catalog);
    }

    public async ValueTask DisposeAsync() => await _database.DisposeAsync();

    [Fact]
    public async Task WriteAsync_NewTicketWithoutSuggestion_SetsAllChangedColumns_PerFR7()
    {
        var cancellationToken = Xunit.TestContext.Current.CancellationToken;
        var ticketId = await InsertTicketAsync(cancellationToken);

        var suggestion = new TriageSuggestion
        {
            TicketKey = "TT-1",
            WorkType = WorkType.ServiceRequest,
            AffectedServices = ["Outlook & Email"],
            ServiceTeams = ["Service Desk"],
            Assignee = "Dana Keller",
            Urgency = Urgency.High,
            Impact = Impact.Major,
            ResolutionStatus = ResolutionStatus.Done,
        };

        var notInCatalog = await _writer.WriteAsync(ticketId, suggestion, cancellationToken);

        notInCatalog.Should().BeEmpty();

        await using var db = _database.CreateContext();
        var ticket = await db.Tickets.AsNoTracking().SingleAsync(t => t.Id == ticketId, cancellationToken);
        ticket.WorkTypeChangedId.Should().Be(_catalog.FindWorkTypeId("Service Request"));
        ticket.AffectedBusinessOrITServiceChangedId.Should().Be(_catalog.FindAffectedServiceId("Outlook & Email"));
        ticket.ServiceTeamChangedId.Should().Be(_catalog.FindServiceTeamId("Service Desk"));
        ticket.AssigneeChanged.Should().Be("Dana Keller");
        ticket.UrgencyChangedId.Should().Be(_catalog.FindUrgencyId("High"));
        ticket.ImpactChangedId.Should().Be(_catalog.FindImpactId("Highest")); // Major -> Highest
        ticket.PriorityChangedId.Should().Be(_catalog.FindPriorityId("Highest")); // High x Major = Highest
        ticket.ResolutionChanged.Should().Be("Done");
    }

    [Fact]
    public async Task WriteAsync_TicketAlreadyHasSuggestion_DoesNotOverwrite_PerConditionalUpdateRule()
    {
        var cancellationToken = Xunit.TestContext.Current.CancellationToken;
        var ticketId = await InsertTicketAsync(cancellationToken);

        var firstSuggestion = new TriageSuggestion
        {
            TicketKey = "TT-1",
            WorkType = WorkType.Incident,
            Urgency = Urgency.Low,
            Impact = Impact.Minor,
        };
        await _writer.WriteAsync(ticketId, firstSuggestion, cancellationToken);

        var secondSuggestion = new TriageSuggestion
        {
            TicketKey = "TT-1",
            WorkType = WorkType.ServiceRequest,
            Urgency = Urgency.Critical,
            Impact = Impact.Major,
        };
        await _writer.WriteAsync(ticketId, secondSuggestion, cancellationToken);

        await using var db = _database.CreateContext();
        var ticket = await db.Tickets.AsNoTracking().SingleAsync(t => t.Id == ticketId, cancellationToken);
        ticket.WorkTypeChangedId.Should().Be(_catalog.FindWorkTypeId("Incident"));
        ticket.UrgencyChangedId.Should().Be(_catalog.FindUrgencyId("Low"));
    }

    private async Task<int> InsertTicketAsync(CancellationToken cancellationToken)
    {
        await using var db = _database.CreateContext();
        var ticket = new TicketEntity
        {
            WorkTypeId = _catalog.DefaultWorkTypeId,
            Summary = "Test ticket",
            StatusId = _catalog.NewStatusId,
            CreatedDate = DateTime.UtcNow,
        };
        db.Tickets.Add(ticket);
        await db.SaveChangesAsync(cancellationToken);
        return ticket.Id;
    }
}
