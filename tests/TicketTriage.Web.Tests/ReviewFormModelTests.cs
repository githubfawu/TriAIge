using TicketTriage.Core.Domain;
using TicketTriage.Web.Triage;

namespace TicketTriage.Web.Tests;

public sealed class ReviewFormModelTests
{
    private static ReviewFormSnapshot Snapshot(
        WorkType workType = WorkType.Incident,
        IReadOnlyList<string>? affectedServices = null,
        string? serviceTeam = "Service Desk",
        string? assignee = "Dana Keller",
        Urgency urgency = Urgency.High,
        Impact impact = Impact.Major,
        ResolutionStatus? resolution = null,
        string? comment = null) =>
        new(workType, affectedServices ?? ["Outlook & Email"], serviceTeam, assignee, urgency, impact, resolution, comment);

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
        form.ToEdits().Should().BeNull();
    }

    [Fact]
    public void HasChanges_AssigneeEdited_IsTrue_SaveEnabled_PerAC8()
    {
        var form = new ReviewFormModel(Snapshot(assignee: "Dana Keller"))
        {
            Assignee = "Someone Else",
        };

        form.HasChanges.Should().BeTrue();
        form.EditedFields().Should().ContainSingle().Which.Should().Be(SuggestionField.Assignee);
        form.ToEdits().Should().BeEquivalentTo(new ReviewEdits { Assignee = "Someone Else" });
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

        form.EditedFields().Should().BeEquivalentTo([SuggestionField.Urgency, SuggestionField.Impact, SuggestionField.DraftComment]);
    }

    [Fact]
    public void EditedFields_AffectedServicesEdited_IsOrderInsensitive()
    {
        var form = new ReviewFormModel(Snapshot(affectedServices: ["Outlook & Email", "Trading Platform"]))
        {
            AffectedServices = ["Trading Platform", "Outlook & Email"],
        };

        form.HasChanges.Should().BeFalse();
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
    public void ToEdits_OnlyIncludesChangedFields()
    {
        var form = new ReviewFormModel(Snapshot(urgency: Urgency.Low, impact: Impact.Minor))
        {
            Urgency = Urgency.Critical,
        };

        var edits = form.ToEdits();

        edits.Should().NotBeNull();
        edits!.HasAny.Should().BeTrue();
        edits.Urgency.Should().Be(Urgency.Critical);
        edits.Impact.Should().BeNull();
        edits.Assignee.Should().BeNull();
    }
}
