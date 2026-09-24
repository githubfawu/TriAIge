using TicketTriage.Core.Domain;
using TicketTriage.Web.Tests.Support;
using TicketTriage.Web.Triage;

namespace TicketTriage.Web.Tests;

public sealed class TicketMapperTests : IAsyncLifetime
{
    private TestDatabase _database = null!;
    private LookupCatalog _catalog = null!;
    private readonly ManualTimeProvider _timeProvider = new(new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero));

    public async ValueTask InitializeAsync()
    {
        var cancellationToken = Xunit.TestContext.Current.CancellationToken;
        _database = await TestDatabase.CreateAsync(cancellationToken);
        _catalog = new LookupCatalog(_database.CreateFactory());
        await _catalog.EnsureLoadedAsync(cancellationToken);
    }

    public async ValueTask DisposeAsync() => await _database.DisposeAsync();

    [Fact]
    public void ToEntity_ValidTicket_MapsNamesToIds_AndAlwaysStatusNew()
    {
        var ticket = new Ticket
        {
            Key = "TT-1",
            Summary = "Outlook keeps freezing",
            WorkType = "Incident",
            AffectedServices = ["Outlook & Email"],
            ServiceTeams = ["Service Desk"],
            Urgency = "High",
            Impact = "Significant",
            Priority = "High",
            Resolution = "Done", // present, but Slice 1 always ingests as New (deviation from TrainingDataImporter).
        };

        var mapped = TicketMapper.ToEntity(ticket, _catalog, _timeProvider);

        mapped.Entity.StatusId.Should().Be(_catalog.NewStatusId);
        mapped.Entity.WorkTypeId.Should().Be(_catalog.FindWorkTypeId("Incident"));
        mapped.Entity.AffectedBusinessOrITServiceId.Should().Be(_catalog.FindAffectedServiceId("Outlook & Email"));
        mapped.Entity.ServiceTeamId.Should().Be(_catalog.FindServiceTeamId("Service Desk"));
        mapped.Entity.UrgencyId.Should().Be(_catalog.FindUrgencyId("High"));
        mapped.Entity.ImpactId.Should().Be(_catalog.FindImpactId("High")); // "Significant" -> DB "High"
        mapped.Hints.Should().BeEmpty();
    }

    [Fact]
    public void ToEntity_UnknownService_LeftEmptyWithHint_PerNotInCatalogRule()
    {
        var ticket = new Ticket { Key = "TT-2", Summary = "Access request", AffectedServices = ["Not A Real Service"] };

        var mapped = TicketMapper.ToEntity(ticket, _catalog, _timeProvider);

        mapped.Entity.AffectedBusinessOrITServiceId.Should().BeNull();
        mapped.Hints.Should().Contain(h => h.Contains("Unknown service"));
    }

    [Fact]
    public void ToEntity_MultipleServicesAndTeams_OnlyFirstStored_WithHint()
    {
        var ticket = new Ticket
        {
            Key = "TT-3",
            Summary = "Two services stalled",
            AffectedServices = ["NAV Calculation", "Fund Pricing"],
            ServiceTeams = ["Valuation & Pricing", "Risk & Controls"],
        };

        var mapped = TicketMapper.ToEntity(ticket, _catalog, _timeProvider);

        mapped.Entity.AffectedBusinessOrITServiceId.Should().Be(_catalog.FindAffectedServiceId("NAV Calculation"));
        mapped.Entity.ServiceTeamId.Should().Be(_catalog.FindServiceTeamId("Valuation & Pricing"));
        mapped.Hints.Should().Contain("Only first service stored.");
        mapped.Hints.Should().Contain("Only first team stored.");
    }

    [Fact]
    public void ToEntity_SummaryLongerThan250_TruncatedWithHint()
    {
        var longSummary = new string('x', 336);
        var ticket = new Ticket { Key = "TT-4", Summary = longSummary };

        var mapped = TicketMapper.ToEntity(ticket, _catalog, _timeProvider);

        mapped.Entity.Summary.Should().HaveLength(TicketMapper.SummaryMaxLength);
        mapped.Hints.Should().Contain(h => h.Contains("Summary truncated"));
    }

    [Fact]
    public void ToEntity_UnknownWorkType_DefaultsToIncident_WithHint()
    {
        var ticket = new Ticket { Key = "TT-5", Summary = "Odd work type", WorkType = "Not A Work Type" };

        var mapped = TicketMapper.ToEntity(ticket, _catalog, _timeProvider);

        mapped.Entity.WorkTypeId.Should().Be(_catalog.DefaultWorkTypeId);
        mapped.Hints.Should().Contain(h => h.Contains("Unknown work type"));
    }

    [Fact]
    public void ToEntity_MissingWorkType_DefaultsToIncident_WithoutHint()
    {
        var ticket = new Ticket { Key = "TT-6", Summary = "No work type given" };

        var mapped = TicketMapper.ToEntity(ticket, _catalog, _timeProvider);

        mapped.Entity.WorkTypeId.Should().Be(_catalog.DefaultWorkTypeId);
        mapped.Hints.Should().BeEmpty();
    }

    [Fact]
    public void ToChangedValues_PriorityAlwaysFromMatrix_HighUrgencyMajorImpact_IsHighest()
    {
        var suggestion = new TriageSuggestion
        {
            TicketKey = "TT-1",
            WorkType = WorkType.Incident,
            Urgency = Urgency.High,
            Impact = Impact.Major,
        };

        var (values, _) = TicketMapper.ToChangedValues(suggestion, _catalog);

        values.PriorityChangedId.Should().Be(_catalog.FindPriorityId("Highest"));
    }

    [Fact]
    public void ToChangedValues_UnknownServiceAndTeam_NullPlusNotInCatalog()
    {
        var suggestion = new TriageSuggestion
        {
            TicketKey = "TT-1",
            WorkType = WorkType.Incident,
            AffectedServices = ["Nonexistent Service"],
            ServiceTeams = ["Nonexistent Team"],
            Urgency = Urgency.Medium,
            Impact = Impact.Moderate,
        };

        var (values, notInCatalog) = TicketMapper.ToChangedValues(suggestion, _catalog);

        values.AffectedServiceChangedId.Should().BeNull();
        values.ServiceTeamChangedId.Should().BeNull();
        notInCatalog.Should().Contain(["AffectedService", "ServiceTeam"]);
    }

    [Fact]
    public void ToChangedValues_ResolutionStatus_MapsToJsonName()
    {
        var suggestion = new TriageSuggestion
        {
            TicketKey = "TT-1",
            WorkType = WorkType.Incident,
            Urgency = Urgency.Medium,
            Impact = Impact.Moderate,
            ResolutionStatus = ResolutionStatus.CannotReproduce,
        };

        var (values, _) = TicketMapper.ToChangedValues(suggestion, _catalog);

        values.ResolutionChanged.Should().Be("Cannot Reproduce");
    }

    [Fact]
    public void ToChangedValues_NoResolutionStatus_ResolutionChangedIsNull()
    {
        var suggestion = new TriageSuggestion
        {
            TicketKey = "TT-1",
            WorkType = WorkType.Incident,
            Urgency = Urgency.Medium,
            Impact = Impact.Moderate,
        };

        var (values, _) = TicketMapper.ToChangedValues(suggestion, _catalog);

        values.ResolutionChanged.Should().BeNull();
    }
}
