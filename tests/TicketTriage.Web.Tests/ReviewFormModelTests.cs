using TicketTriage.Core.Domain;
using TicketTriage.Web.Triage;

namespace TicketTriage.Web.Tests;

public sealed class ReviewFormModelTests
{
    private static ReviewFormSnapshot Snapshot(
        WorkType workType = WorkType.Incident,
        string? affectedService = "Outlook & Email",
        string? serviceTeam = "Service Desk",
        string? assignee = "Dana Keller",
        Urgency urgency = Urgency.High,
        Impact impact = Impact.Major,
        ResolutionStatus? resolution = null,
        string? comment = null) =>
        new(workType, affectedService, serviceTeam, assignee, urgency, impact, resolution, comment);

    [Fact]
    public void Priority_HighUrgencyMajorImpact_IsHighest_PerAC6()
    {
        var form = new ReviewFormModel(Snapshot(urgency: Urgency.High, impact: Impact.Major));

        form.Priority.Should().Be(Priority.Highest);
    }

    [Fact]
    public void Priority_RecomputesImmediately_WhenUrgencyOrImpactChanges_PerAC6()
    {
        var form = new ReviewFormModel(Snapshot(urgency: Urgency.Low, impact: Impact.Minor));
        form.Priority.Should().Be(Priority.Low);

        form.Urgency = Urgency.Critical;
        form.Impact = Impact.Major;

        form.Priority.Should().Be(Priority.Highest);
    }

    [Fact]
    public void HasChanges_FormEqualsBaseline_IsFalse_AcceptEnabled_PerAC8()
    {
        var form = new ReviewFormModel(Snapshot());

        form.HasChanges.Should().BeFalse();
        form.EditedFields().Should().BeEmpty();
    }

    [Fact]
    public void HasChanges_AssigneeEdited_IsTrue_SaveEnabled_PerAC8()
    {
        var form = new ReviewFormModel(Snapshot(assignee: "Dana Keller"))
        {
            Assignee = "Someone Else",
        };

        form.HasChanges.Should().BeTrue();
        form.EditedFields().Should().ContainSingle().Which.Should().Be(ReviewField.Assignee);
    }

    [Fact]
    public void EditedFields_MultipleFieldsChanged_ListsAll_PerAC8()
    {
        var form = new ReviewFormModel(Snapshot(urgency: Urgency.Low, impact: Impact.Minor))
        {
            Urgency = Urgency.Critical,
            Impact = Impact.Major,
            Comment = "Draft resolution text",
        };

        form.EditedFields().Should().BeEquivalentTo([ReviewField.Urgency, ReviewField.Impact, ReviewField.Comment]);
    }

    [Fact]
    public void Reset_AfterEdits_RestoresBaseline_PerAC8()
    {
        var baseline = Snapshot(assignee: "Dana Keller", urgency: Urgency.Medium, impact: Impact.Moderate);
        var form = new ReviewFormModel(baseline)
        {
            Assignee = "Changed",
            Urgency = Urgency.Critical,
        };

        form.Reset();

        form.HasChanges.Should().BeFalse();
        form.Assignee.Should().Be("Dana Keller");
        form.Urgency.Should().Be(Urgency.Medium);
    }

    [Fact]
    public void BuildBaseline_NoSuggestionForField_FallsBackToOriginal()
    {
        var inputs = new ReviewFieldInputs(
            WorkType.Incident, Urgency.Medium, Impact.Moderate,
            ChangedAffectedService: null, OriginalAffectedService: "Trading Platform",
            ChangedServiceTeam: null, OriginalServiceTeam: "Trading Support",
            ChangedAssignee: null, OriginalAssignee: "Original Assignee",
            ChangedResolution: null, OriginalResolution: ResolutionStatus.Done);

        var baseline = ReviewFormModel.BuildBaseline(inputs, draftComment: null);

        baseline.AffectedService.Should().Be("Trading Platform");
        baseline.ServiceTeam.Should().Be("Trading Support");
        baseline.Assignee.Should().Be("Original Assignee");
        baseline.Resolution.Should().Be(ResolutionStatus.Done);
    }

    [Fact]
    public void BuildBaseline_SuggestionPresent_PrefersSuggestionOverOriginal()
    {
        var inputs = new ReviewFieldInputs(
            WorkType.ServiceRequest, Urgency.High, Impact.Significant,
            ChangedAffectedService: "Outlook & Email", OriginalAffectedService: "Trading Platform",
            ChangedServiceTeam: "Service Desk", OriginalServiceTeam: "Trading Support",
            ChangedAssignee: "Dana Keller", OriginalAssignee: "Original Assignee",
            ChangedResolution: ResolutionStatus.Clarification, OriginalResolution: ResolutionStatus.Done);

        var baseline = ReviewFormModel.BuildBaseline(inputs, draftComment: "Draft");

        baseline.AffectedService.Should().Be("Outlook & Email");
        baseline.ServiceTeam.Should().Be("Service Desk");
        baseline.Assignee.Should().Be("Dana Keller");
        baseline.Resolution.Should().Be(ResolutionStatus.Clarification);
        baseline.Comment.Should().Be("Draft");
    }
}
