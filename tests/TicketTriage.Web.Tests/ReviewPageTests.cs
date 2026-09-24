using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using TicketTriage.Core.Abstractions;
using TicketTriage.Core.Domain;
using TicketTriage.Web.Components.Pages;
using TicketTriage.Web.Tests.Support;
using TicketTriage.Web.Triage;

namespace TicketTriage.Web.Tests;

public sealed class ReviewPageTests : TriageBunitContext
{
    private static Ticket MakeTicket(string key) => new() { Key = key, Summary = $"Summary for {key}" };

    private static ReviewFormSnapshot MakeBaseline(
        WorkType workType = WorkType.Incident,
        string? affectedService = "Outlook & Email",
        string? serviceTeam = "Service Desk",
        string? assignee = "Dana Keller",
        Urgency urgency = Urgency.Medium,
        Impact impact = Impact.Moderate,
        ResolutionStatus? resolution = null,
        string? comment = null) =>
        new(workType, affectedService, serviceTeam, assignee, urgency, impact, resolution, comment);

    private static TicketReviewData MakeData(
        int id = 1,
        string summary = "Ticket summary",
        TicketDisplayState? state = TicketDisplayState.Pending,
        bool isTrainingTicket = false,
        bool hasSuggestion = true,
        ReviewFormSnapshot? formBaseline = null,
        string? originalAffectedService = "Trading Platform",
        string? originalServiceTeam = "Trading Support",
        string? originalAssignee = "Original Assignee",
        string? originalUrgency = "Low",
        string? originalImpact = "Lowest",
        IReadOnlyDictionary<ReviewField, string>? hints = null,
        string? failureReason = null,
        string? rejectReason = null) => new(
        id,
        $"TT-{id}",
        summary,
        Description: null,
        Comments: [],
        state,
        isTrainingTicket,
        failureReason,
        rejectReason,
        "Incident",
        originalAffectedService,
        originalServiceTeam,
        originalAssignee,
        originalUrgency,
        originalImpact,
        OriginalPriority: "Low",
        OriginalResolution: null,
        hasSuggestion,
        formBaseline ?? (hasSuggestion ? MakeBaseline() : null),
        hints ?? new Dictionary<ReviewField, string>(),
        ["Service Desk", "Trading Support"],
        ["Outlook & Email", "Trading Platform"],
        [],
        null);

    private static void RegisterCommonServices(TriageBunitContext context, ITriageBoardQuery boardQuery, TriageSessionStore store, ITriagePipeline? pipeline = null)
    {
        context.Services.AddSingleton(store);
        context.Services.AddSingleton(boardQuery);
        context.Services.AddSingleton<IUploadIngestService>(new FakeIngestService());
        context.Services.AddSingleton<IReviewDecisionService>(new FakeDecisionService());
        if (pipeline is not null)
        {
            context.Services.AddSingleton(pipeline);
        }
    }

    [Fact]
    public async Task QueuedTicket_ShowsAnalysing_MovesToFront_NeverCallsPipeline_ThenShowsFormWithoutReload_PerAC12()
    {
        var store = new TriageSessionStore(NullLogger<TriageSessionStore>.Instance);
        var pipeline = new FakeTriagePipeline();
        var boardQuery = new FakeReviewBoardQuery { Data = MakeData(1, state: TicketDisplayState.Queued, hasSuggestion: false, formBaseline: null) };
        RegisterCommonServices(this, boardQuery, store, pipeline);

        // Ticket 2 is queued first; opening ticket 1's review page should jump it to the front (FR10).
        store.Register(2, "TT-2", MakeTicket("TT-2"), uploadId: 1);
        store.Register(1, "TT-1", MakeTicket("TT-1"), uploadId: 1);

        var cut = Render<Review>(p => p.Add(x => x.Id, 1));
        cut.WaitForAssertion(() => cut.Markup.Should().Contain("Agent is analysing"));

        var dequeued = await store.DequeueAsync(Xunit.TestContext.Current.CancellationToken);
        dequeued.TicketId.Should().Be(1);

        // Simulate the worker finishing (never through the page - the page has no ITriagePipeline dependency at all).
        var suggestion = new TriageSuggestion { TicketKey = "TT-1", WorkType = WorkType.Incident, Urgency = Urgency.Medium, Impact = Impact.Moderate };
        boardQuery.Data = MakeData(1, state: TicketDisplayState.Pending, hasSuggestion: true, formBaseline: MakeBaseline());
        store.CompleteAnalysis(1, suggestion, []);

        cut.WaitForAssertion(() => cut.Markup.Should().Contain("Suggested / edited"));
        pipeline.Calls.Should().Be(0);
    }

    [Fact]
    public void PendingTicket_ShowsOriginalAndSuggestionSideBySide_WithDiffMarks_PerFR15()
    {
        var store = new TriageSessionStore(NullLogger<TriageSessionStore>.Instance);
        var boardQuery = new FakeReviewBoardQuery
        {
            Data = MakeData(1, formBaseline: MakeBaseline(affectedService: "Outlook & Email"), originalAffectedService: "Trading Platform"),
        };
        RegisterCommonServices(this, boardQuery, store);

        var cut = Render<Review>(p => p.Add(x => x.Id, 1));

        cut.Markup.Should().Contain("Trading Platform"); // original, read-only left panel
        cut.Markup.Should().Contain("Outlook &amp; Email"); // suggested value pre-selected in the form (HTML-escaped &)
        cut.Markup.Should().Contain("tt-field-changed"); // AffectedService differs from original -> highlighted
        cut.Markup.Should().Contain("was: Trading Platform");
    }

    [Fact]
    public void EditingAField_DisablesAccept_EnablesSave_PerFR18()
    {
        var store = new TriageSessionStore(NullLogger<TriageSessionStore>.Instance);
        var boardQuery = new FakeReviewBoardQuery { Data = MakeData(1) };
        RegisterCommonServices(this, boardQuery, store);

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
        var store = new TriageSessionStore(NullLogger<TriageSessionStore>.Instance);
        var boardQuery = new FakeReviewBoardQuery { Data = MakeData(1, formBaseline: MakeBaseline(assignee: "Dana Keller")) };
        RegisterCommonServices(this, boardQuery, store);

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
        var store = new TriageSessionStore(NullLogger<TriageSessionStore>.Instance);
        var boardQuery = new FakeReviewBoardQuery { Data = MakeData(1) };
        RegisterCommonServices(this, boardQuery, store);

        var cut = Render<Review>(p => p.Add(x => x.Id, 1));

        cut.Markup.Should().Contain("owner-code"); // Assignee/ServiceTeam/Priority
        cut.Markup.Should().Contain("owner-llm"); // WorkType/AffectedService
        cut.Markup.Should().Contain("owner-mixed"); // Urgency/Impact/Resolution/Comment
    }

    [Fact]
    public void ApprovedTicket_IsReadOnly_PerFR20()
    {
        var store = new TriageSessionStore(NullLogger<TriageSessionStore>.Instance);
        var boardQuery = new FakeReviewBoardQuery { Data = MakeData(1, state: TicketDisplayState.Approved, formBaseline: null) };
        RegisterCommonServices(this, boardQuery, store);

        var cut = Render<Review>(p => p.Add(x => x.Id, 1));

        cut.Markup.Should().Contain("approved");
        cut.FindAll("#review-accept-button").Should().BeEmpty();
    }

    [Fact]
    public void RejectedTicket_ShowsReasonFromRam_PerFR20()
    {
        var store = new TriageSessionStore(NullLogger<TriageSessionStore>.Instance);
        var boardQuery = new FakeReviewBoardQuery
        {
            Data = MakeData(1, state: TicketDisplayState.Rejected, formBaseline: null, rejectReason: "Duplicate of TT-9"),
        };
        RegisterCommonServices(this, boardQuery, store);

        var cut = Render<Review>(p => p.Add(x => x.Id, 1));

        cut.Markup.Should().Contain("rejected");
        cut.Markup.Should().Contain("Duplicate of TT-9");
    }

    [Fact]
    public void TrainingTicket_ShowsNotPartOfTriage_PerFR20()
    {
        var store = new TriageSessionStore(NullLogger<TriageSessionStore>.Instance);
        var boardQuery = new FakeReviewBoardQuery
        {
            Data = MakeData(1, state: null, isTrainingTicket: true, hasSuggestion: false, formBaseline: null),
        };
        RegisterCommonServices(this, boardQuery, store);

        var cut = Render<Review>(p => p.Add(x => x.Id, 1));

        cut.Markup.Should().Contain("Not part of triage");
    }

    [Fact]
    public void NewTicketNotQueued_ShowsAnalyseNowButton_PerFR10()
    {
        var store = new TriageSessionStore(NullLogger<TriageSessionStore>.Instance);
        var boardQuery = new FakeReviewBoardQuery
        {
            Data = MakeData(1, state: null, isTrainingTicket: false, hasSuggestion: false, formBaseline: null),
        };
        RegisterCommonServices(this, boardQuery, store);

        var cut = Render<Review>(p => p.Add(x => x.Id, 1));

        cut.Markup.Should().Contain("Analyse now");
    }

    [Fact]
    public void FailedTicket_ShowsReasonAndRequeueButton_PerAC11()
    {
        var store = new TriageSessionStore(NullLogger<TriageSessionStore>.Instance);
        var boardQuery = new FakeReviewBoardQuery
        {
            Data = MakeData(1, state: TicketDisplayState.Failed, hasSuggestion: false, formBaseline: null, failureReason: "InvalidOperationException"),
        };
        RegisterCommonServices(this, boardQuery, store);

        var cut = Render<Review>(p => p.Add(x => x.Id, 1));

        cut.Markup.Should().Contain("InvalidOperationException");
        cut.Markup.Should().Contain("Re-queue");
    }

    [Fact]
    public void ScriptInSummary_IsEscaped_NeverRenderedAsMarkup_PerNFR5()
    {
        var store = new TriageSessionStore(NullLogger<TriageSessionStore>.Instance);
        var boardQuery = new FakeReviewBoardQuery
        {
            Data = MakeData(1, summary: "<script>alert('xss')</script>"),
        };
        RegisterCommonServices(this, boardQuery, store);

        var cut = Render<Review>(p => p.Add(x => x.Id, 1));

        cut.Markup.Should().NotContain("<script>");
        cut.Markup.Should().Contain("&lt;script&gt;");
    }

    [Fact]
    public void NotFoundTicket_ShowsWarning()
    {
        var store = new TriageSessionStore(NullLogger<TriageSessionStore>.Instance);
        var boardQuery = new FakeReviewBoardQuery { Data = null };
        RegisterCommonServices(this, boardQuery, store);

        var cut = Render<Review>(p => p.Add(x => x.Id, 999));

        cut.Markup.Should().Contain("was not found");
    }

    private sealed class FakeReviewBoardQuery : ITriageBoardQuery
    {
        public TicketReviewData? Data { get; set; }

        public Task<IReadOnlyList<TicketBoardRow>> GetRowsAsync(CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<TicketBoardRow>>([]);

        public Task<TicketReviewData?> GetReviewAsync(int id, CancellationToken cancellationToken) => Task.FromResult(Data);

        public Task<int?> GetNextPendingIdAsync(int excludeId, CancellationToken cancellationToken) => Task.FromResult<int?>(null);

        public Task<DecisionTotals> GetDecisionTotalsAsync(CancellationToken cancellationToken) => Task.FromResult(new DecisionTotals(0, 0));
    }

    private sealed class FakeIngestService : IUploadIngestService
    {
        public Task<UploadPreview> PreviewAsync(byte[] content, CancellationToken cancellationToken) =>
            throw new NotSupportedException("Not used by the Review page tests.");

        public Task<SaveResult> SaveAndEnqueueAsync(UploadPreview preview, bool confirmedWithoutTrainingData, CancellationToken cancellationToken) =>
            throw new NotSupportedException("Not used by the Review page tests.");

        public Task<bool> EnqueueExistingAsync(int ticketId, CancellationToken cancellationToken) => Task.FromResult(true);
    }

    private sealed class FakeDecisionService : IReviewDecisionService
    {
        public Task<DecisionResult> ApproveAsync(int ticketId, ReviewFormModel form, CancellationToken cancellationToken) =>
            Task.FromResult(new DecisionResult(DecisionOutcome.Saved));

        public Task<DecisionResult> RejectAsync(int ticketId, string reason, CancellationToken cancellationToken) =>
            Task.FromResult(new DecisionResult(DecisionOutcome.Saved));
    }
}
