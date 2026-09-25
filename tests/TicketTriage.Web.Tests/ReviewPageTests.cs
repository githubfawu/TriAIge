using Microsoft.Extensions.DependencyInjection;
using TicketTriage.Core.Abstractions;
using TicketTriage.Core.Domain;
using TicketTriage.Web.Components.Pages;
using TicketTriage.Web.Tests.Support;
using TicketTriage.Web.Triage;

namespace TicketTriage.Web.Tests;

public sealed class ReviewPageTests : TriageBunitContext
{
    private static TriageSuggestion MakeSuggestion(
        WorkType workType = WorkType.Incident,
        IReadOnlyList<string>? affectedServices = null,
        string? serviceTeam = "Service Desk",
        string? assignee = "Dana Keller",
        Urgency urgency = Urgency.Medium,
        Impact impact = Impact.Moderate,
        ResolutionStatus? resolution = null,
        string? comment = "Draft.") => new()
        {
            TicketKey = "DB-1",
            WorkType = workType,
            AffectedServices = affectedServices ?? ["Outlook & Email"],
            ServiceTeams = serviceTeam is null ? [] : [serviceTeam],
            Assignee = assignee,
            Urgency = urgency,
            Impact = impact,
            ResolutionStatus = resolution,
            DraftComment = comment,
        };

    private static TicketReview MakeReview(
        int id = 1,
        string summary = "Ticket summary",
        bool isAnalysing = false,
        bool isFailed = false,
        TriageSuggestion? suggestion = null,
        ReviewDecisionInfo? decision = null,
        string? originalWorkType = "Incident",
        IReadOnlyList<string>? originalAffectedServices = null,
        IReadOnlyList<string>? originalServiceTeams = null,
        string? originalAssignee = "Original Assignee",
        string? originalUrgency = "Low",
        string? originalImpact = "Lowest",
        string? originalPriority = "Low",
        string? originalResolution = null) => new(
        new Ticket
        {
            Id = id,
            Key = $"DB-{id}",
            Summary = summary,
            WorkType = originalWorkType,
            AffectedServices = originalAffectedServices ?? ["Trading Platform"],
            ServiceTeams = originalServiceTeams ?? ["Trading Support"],
            Assignee = originalAssignee,
            Urgency = originalUrgency,
            Impact = originalImpact,
            Priority = originalPriority,
            Resolution = originalResolution,
        },
        suggestion,
        suggestion,
        "Reviewing",
        1,
        isAnalysing,
        isFailed,
        decision,
        []);

    private static (FakeReviewService ReviewService, FakeTicketBoardQuery BoardQuery) RegisterServices(TriageBunitContext context, TicketReview? review)
    {
        var reviewService = new FakeReviewService { Review = review };
        var boardQuery = new FakeTicketBoardQuery();
        context.Services.AddSingleton<IReviewService>(reviewService);
        context.Services.AddSingleton<ITicketBoardQuery>(boardQuery);
        return (reviewService, boardQuery);
    }

    [Fact]
    public void QueuedTicket_ShowsAnalysing_PerAC12()
    {
        RegisterServices(this, MakeReview(isAnalysing: true, suggestion: null));

        var cut = Render<Review>(p => p.Add(x => x.Id, 1));

        cut.Markup.Should().Contain("Agent is analysing");
    }

    [Fact]
    public void PendingTicket_ShowsOriginalAndSuggestionSideBySide_WithDiffMarks_PerFR15()
    {
        RegisterServices(this, MakeReview(
            suggestion: MakeSuggestion(affectedServices: ["Outlook & Email"]),
            originalAffectedServices: ["Trading Platform"]));

        var cut = Render<Review>(p => p.Add(x => x.Id, 1));

        cut.Markup.Should().Contain("Trading Platform"); // original, read-only left panel
        cut.Markup.Should().Contain("Outlook &amp; Email"); // suggested value pre-selected in the form
        cut.Markup.Should().Contain("tt-field-changed"); // AffectedServices differs from original -> highlighted
        cut.Markup.Should().Contain("was: Trading Platform");
    }

    [Fact]
    public void EditingAField_DisablesAccept_EnablesSave_PerFR18()
    {
        RegisterServices(this, MakeReview(suggestion: MakeSuggestion()));

        var cut = Render<Review>(p => p.Add(x => x.Id, 1));

        cut.Find("#review-accept-button").HasAttribute("disabled").Should().BeFalse();
        cut.Find("#review-save-button").HasAttribute("disabled").Should().BeTrue();

        cut.Find("#review-assignee-input").Input("Someone Else");

        cut.Find("#review-accept-button").HasAttribute("disabled").Should().BeTrue();
        cut.Find("#review-save-button").HasAttribute("disabled").Should().BeFalse();
    }

    [Fact]
    public void ResetButton_RestoresBaselineAfterEdit()
    {
        RegisterServices(this, MakeReview(suggestion: MakeSuggestion(assignee: "Dana Keller")));

        var cut = Render<Review>(p => p.Add(x => x.Id, 1));
        cut.Find("#review-assignee-input").Input("Someone Else");
        cut.Find("#review-save-button").HasAttribute("disabled").Should().BeFalse();

        cut.Find("#review-reset-button").Click();

        cut.Find("#review-accept-button").HasAttribute("disabled").Should().BeFalse();
        cut.Find("#review-save-button").HasAttribute("disabled").Should().BeTrue();
    }

    [Fact]
    public void FieldBadges_UseOwnerCssClasses_PerAC14()
    {
        RegisterServices(this, MakeReview(suggestion: MakeSuggestion()));

        var cut = Render<Review>(p => p.Add(x => x.Id, 1));

        cut.Markup.Should().Contain("owner-code"); // Assignee/ServiceTeams
        cut.Markup.Should().Contain("owner-llm"); // WorkType/AffectedServices
        cut.Markup.Should().Contain("owner-mixed"); // Urgency/Impact/ResolutionStatus/DraftComment
    }

    [Fact]
    public void ApprovedTicket_IsReadOnly_PerFR20()
    {
        RegisterServices(this, MakeReview(decision: new ReviewDecisionInfo(ReviewDecision.Approved, null, null, DateTime.UtcNow)));

        var cut = Render<Review>(p => p.Add(x => x.Id, 1));

        cut.Markup.Should().Contain("approved");
        cut.FindAll("#review-accept-button").Should().BeEmpty();
    }

    [Fact]
    public void RejectedTicket_ShowsReason_PerFR20()
    {
        RegisterServices(this, MakeReview(
            decision: new ReviewDecisionInfo(ReviewDecision.Rejected, "Duplicate of TT-9", null, DateTime.UtcNow)));

        var cut = Render<Review>(p => p.Add(x => x.Id, 1));

        cut.Markup.Should().Contain("rejected");
        cut.Markup.Should().Contain("Duplicate of TT-9");
    }

    [Fact]
    public void FailedTicket_ShowsReasonAndRequeueButton_PerAC11()
    {
        var (_, boardQuery) = RegisterServices(this, MakeReview(isAnalysing: true, isFailed: true));
        boardQuery.FailureReason = "InvalidOperationException";

        var cut = Render<Review>(p => p.Add(x => x.Id, 1));

        cut.Markup.Should().Contain("InvalidOperationException");
        cut.Markup.Should().Contain("Re-queue");
    }

    [Fact]
    public void ScriptInSummary_IsEscaped_NeverRenderedAsMarkup_PerNFR5()
    {
        RegisterServices(this, MakeReview(summary: "<script>alert('xss')</script>", isAnalysing: true));

        var cut = Render<Review>(p => p.Add(x => x.Id, 1));

        cut.Markup.Should().NotContain("<script>");
        cut.Markup.Should().Contain("&lt;script&gt;");
    }

    [Fact]
    public void NotFoundTicket_ShowsWarning()
    {
        RegisterServices(this, review: null);

        var cut = Render<Review>(p => p.Add(x => x.Id, 999));

        cut.Markup.Should().Contain("was not found");
    }

    [Fact]
    public void AcceptAsync_NoEdits_CallsApproveWithNullEdits_AndNavigatesToTickets()
    {
        var (reviewService, boardQuery) = RegisterServices(this, MakeReview(suggestion: MakeSuggestion()));
        boardQuery.NextPendingId = null;
        var navigation = Services.GetRequiredService<Microsoft.AspNetCore.Components.NavigationManager>();

        var cut = Render<Review>(p => p.Add(x => x.Id, 1));
        cut.Find("#review-accept-button").Click();

        reviewService.ApproveCalls.Should().ContainSingle(c => c.TicketId == 1 && c.Edits == null);
        cut.WaitForAssertion(() => navigation.Uri.Should().EndWith("/tickets"));
    }

    [Fact]
    public void SaveAsync_WithEdits_CallsApproveWithEdits()
    {
        var (reviewService, _) = RegisterServices(this, MakeReview(suggestion: MakeSuggestion(assignee: "Dana Keller")));

        var cut = Render<Review>(p => p.Add(x => x.Id, 1));
        cut.Find("#review-assignee-input").Input("Someone Else");
        cut.Find("#review-save-button").Click();

        reviewService.ApproveCalls.Should().ContainSingle(c => c.TicketId == 1 && c.Edits!.Assignee == "Someone Else");
    }

    [Fact]
    public void RejectAsync_ViaDialog_CallsRejectWithReason()
    {
        var (reviewService, _) = RegisterServices(this, MakeReview(suggestion: MakeSuggestion()));

        // The dialog is normally hosted by MainLayout's <MudDialogProvider/>; render one here too so the
        // page's DialogService.ShowAsync call actually has somewhere to render into (mirrors RejectDialogTests).
        var providerCut = Render<MudBlazor.MudDialogProvider>();
        var cut = Render<Review>(p => p.Add(x => x.Id, 1));
        cut.Find("#review-reject-button").Click();

        providerCut.Find("textarea").Input("Duplicate of TT-9");
        providerCut.Find("button.mud-button-filled").Click();

        cut.WaitForAssertion(() => reviewService.RejectCalls.Should().ContainSingle(c => c.TicketId == 1 && c.Reason == "Duplicate of TT-9"));
    }
}
