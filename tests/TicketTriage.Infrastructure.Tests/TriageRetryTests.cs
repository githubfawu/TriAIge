using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using TicketTriage.Core.Abstractions;
using TicketTriage.Core.Domain;
using TicketTriage.Infrastructure.Pipeline;
using TicketTriage.Infrastructure.Routing;

namespace TicketTriage.Infrastructure.Tests;

public class TriageRetryTests
{
    private const string Marker = "SECRET-MARKER-9137";
    private static readonly string Cat = ServiceCatalog.All[0].Name;

    private readonly RecordingFailureStore _store = new();

    private static SimilarTicket Old(string key, string? workType, double score, params string[] services) =>
        new(Tickets.Make(key) with { WorkType = workType, AffectedServices = services }, score);

    private TriagePipeline Build(
        ITicketClassifier classifier,
        IResolutionDrafter? drafter = null,
        int retryCount = 3,
        int timeoutSeconds = 60,
        IRoutingResolver? router = null,
        ISimilarTicketSource? similar = null,
        IRoutingStatisticsSource? statistics = null)
    {
        var log = new CallLog();
        return new TriagePipeline(
            new TicketNormalizer(NullLogger<TicketNormalizer>.Instance),
            similar ?? new FixedSimilarSource(Old("OLD-1", "Incident", 0.9, "Email")),
            classifier,
            router ?? new FakeRouter(log),
            statistics ?? new FakeRoutingStatisticsSource(),
            drafter ?? new ScriptedDrafter(),
            Options.Create(new TriageOptions { RetryCount = retryCount, TicketTimeoutSeconds = timeoutSeconds, RetryDelayMilliseconds = 0 }),
            _store,
            NullLogger<TriagePipeline>.Instance);
    }

    [Fact]
    public async Task Triage_FailsOnceThenSucceeds_YieldsSuggestionAndRecordsOneFailure_PerAC4()
    {
        var ct = TestContext.Current.CancellationToken;
        var pipeline = Build(new ScriptedClassifier(failures: 1));

        var result = await pipeline.TriageAsync(Tickets.Stream(Tickets.Make("T-1")), ct).ToListAsync(ct);

        result.Should().ContainSingle().Which.DraftComment.Should().Be("draft text");
        _store.Calls.Should().ContainSingle();
        _store.Calls[0].Attempt.Should().Be(1);
        _store.Calls[0].Reason.Should().Be("Classify:Exception");
        _store.Calls[0].ExceptionType.Should().Be(typeof(InvalidOperationException).FullName);
    }

    [Fact]
    public async Task Triage_AlwaysFails_FallsBackAfterRetryCountAndStreamContinues_PerAC5()
    {
        var ct = TestContext.Current.CancellationToken;
        var classifier = new ScriptedClassifier(failures: 3);
        var similar = new FixedSimilarSource(
            Old("O1", "Service Request", 0.5, Cat, "Non-Catalog"),
            Old("O2", "Service Request", 0.4, Cat),
            Old("O3", "Incident", 0.1, "Non-Catalog"));
        var pipeline = Build(classifier, retryCount: 3, similar: similar);

        var result = await pipeline.TriageAsync(Tickets.Stream(Tickets.Make("T-1"), Tickets.Make("T-2")), ct).ToListAsync(ct);

        result.Select(s => s.TicketKey).Should().Equal("T-1", "T-2");
        var fallback = result[0];
        fallback.WorkType.Should().Be(WorkType.ServiceRequest);
        fallback.AffectedServices.Should().Equal(Cat);
        fallback.Urgency.Should().Be(Urgency.Medium);
        fallback.Impact.Should().Be(Impact.Moderate);
        fallback.DraftComment.Should().BeNull();
        fallback.ServiceTeams.Should().Equal("Team A");
        _store.Calls.Should().HaveCount(3);
        _store.Calls.Select(c => c.Attempt).Should().Equal(1, 2, 3);
        result[1].DraftComment.Should().Be("draft text");
        similar.Calls.Should().Be(2, "similar tickets are fetched once per ticket");
    }

    [Fact]
    public async Task Triage_PersistedRetriesReachLimit_FallsBackImmediately_D1()
    {
        _store.PersistedRetries = 3;
        var classifier = new ScriptedClassifier(failures: null);
        var pipeline = Build(classifier, retryCount: 3);

        var suggestion = await pipeline.TriageAsync(Tickets.Make("T-1"), TestContext.Current.CancellationToken);

        suggestion.DraftComment.Should().BeNull();
        classifier.Calls.Should().Be(1);
    }

    [Fact]
    public async Task Triage_AttemptTimesOut_CountsAsFailure_PerAC7()
    {
        var classifier = new ScriptedClassifier(failures: 1, onFail: ct => Task.Delay(Timeout.Infinite, ct));
        var pipeline = Build(classifier, retryCount: 1, timeoutSeconds: 1);

        var suggestion = await pipeline.TriageAsync(Tickets.Make("T-1"), TestContext.Current.CancellationToken);

        suggestion.DraftComment.Should().BeNull();
        _store.Calls.Should().ContainSingle().Which.Reason.Should().Contain("Timeout");
    }

    [Fact]
    public async Task Stream_Cancelled_PropagatesWithoutRecordingOrFallback_PerAC7()
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        var classifier = new ScriptedClassifier(failures: 1, onFail: async ct =>
        {
            await cts.CancelAsync();
            ct.ThrowIfCancellationRequested();
        });
        var pipeline = Build(classifier);

        var act = async () => await pipeline.TriageAsync(Tickets.Stream(Tickets.Make("T-1")), cts.Token).ToListAsync(cts.Token);

        await act.Should().ThrowAsync<OperationCanceledException>();
        _store.Calls.Should().BeEmpty();
    }

    [Fact]
    public async Task Triage_EmptyComment_IsFailedAttemptWithCode_PerAC8()
    {
        var pipeline = Build(new ScriptedClassifier(), new ScriptedDrafter(draft: "  "), retryCount: 2);

        var suggestion = await pipeline.TriageAsync(Tickets.Make("T-1"), TestContext.Current.CancellationToken);

        suggestion.DraftComment.Should().BeNull();
        _store.Calls.Should().HaveCount(2);
        _store.Calls[0].Reason.Should().Be("Validate:EmptyComment");
    }

    [Fact]
    public async Task Triage_NoServices_IsFailedAttemptWithCode_PerAC8()
    {
        var pipeline = Build(new NoServiceClassifier(), retryCount: 1);

        await pipeline.TriageAsync(Tickets.Make("T-1"), TestContext.Current.CancellationToken);

        _store.Calls.Should().ContainSingle().Which.Reason.Should().Be("Validate:NoAffectedServices");
    }

    [Fact]
    public async Task Triage_StoreFails_AttemptStillCountsAndFallbackReturned()
    {
        _store.Throw = true;
        var classifier = new ScriptedClassifier(failures: null);
        var pipeline = Build(classifier, retryCount: 2);

        var suggestion = await pipeline.TriageAsync(Tickets.Make("T-1"), TestContext.Current.CancellationToken);

        suggestion.DraftComment.Should().BeNull();
        classifier.Calls.Should().Be(2);
        _store.Calls.Should().HaveCount(2);
    }

    [Fact]
    public async Task Triage_TicketWithoutId_UsesInMemoryCounter()
    {
        var classifier = new ScriptedClassifier(failures: null);
        var pipeline = Build(classifier, retryCount: 3);

        await pipeline.TriageAsync(Tickets.Make("T-1"), TestContext.Current.CancellationToken);

        classifier.Calls.Should().Be(3);
        _store.Calls.Should().OnlyContain(c => c.TicketId == null);
    }

    [Fact]
    public async Task Triage_FailureReason_ContainsNoTicketText()
    {
        var ticket = Tickets.Make("T-1") with { Summary = Marker };
        var pipeline = Build(new ScriptedClassifier(failures: null), retryCount: 1);

        await pipeline.TriageAsync(ticket, TestContext.Current.CancellationToken);

        _store.Calls.Should().ContainSingle().Which.Reason.Should().NotContain(Marker);
    }

    [Fact]
    public async Task Triage_FailureStackTrace_ContainsFramesButNoExceptionMessage()
    {
        var ticket = Tickets.Make("T-1") with { Summary = Marker };
        var pipeline = Build(new ScriptedClassifier(failures: null), retryCount: 1);

        await pipeline.TriageAsync(ticket, TestContext.Current.CancellationToken);

        var trace = _store.Calls.Should().ContainSingle().Which.StackTrace;
        trace.Should().NotBeNull().And.Contain("ScriptedClassifier");
        trace.Should().NotContain(Marker);
    }

    [Fact]
    public async Task Triage_FailedEarlierThenSucceeds_ResetsRetries()
    {
        var pipeline = Build(new ScriptedClassifier(failures: 1));

        await pipeline.TriageAsync(Tickets.Make("T-1") with { Id = 42 }, TestContext.Current.CancellationToken);

        _store.Calls.Should().ContainSingle();
        _store.ResetCalls.Should().Equal(42);
    }

    [Fact]
    public async Task Triage_FallbackOrNoId_DoesNotResetRetries()
    {
        var withoutId = Build(new ScriptedClassifier());
        await withoutId.TriageAsync(Tickets.Make("T-1"), TestContext.Current.CancellationToken);

        var fallback = Build(new ScriptedClassifier(failures: null), retryCount: 1);
        await fallback.TriageAsync(Tickets.Make("T-2") with { Id = 7 }, TestContext.Current.CancellationToken);

        _store.ResetCalls.Should().BeEmpty();
    }

    [Fact]
    public async Task Triage_ResetFails_StillReturnsSuggestion()
    {
        _store.ThrowOnReset = true;
        var pipeline = Build(new ScriptedClassifier());

        var suggestion = await pipeline.TriageAsync(Tickets.Make("T-1") with { Id = 1 }, TestContext.Current.CancellationToken);

        suggestion.DraftComment.Should().Be("draft text");
    }

    [Fact]
    public async Task Triage_NormalizerThrows_CountsAsFailedAttemptAndFallsBack()
    {
        var pipeline = new TriagePipeline(
            new TicketNormalizer(new ThrowingLogger()),
            new FixedSimilarSource(),
            new ScriptedClassifier(),
            new FakeRouter(new CallLog()),
            new FakeRoutingStatisticsSource(),
            new ScriptedDrafter(),
            Options.Create(new TriageOptions { RetryCount = 2, RetryDelayMilliseconds = 0 }),
            _store,
            NullLogger<TriagePipeline>.Instance);
        var poison = Tickets.Make("T-1", description: null);

        var suggestion = await pipeline.TriageAsync(poison, TestContext.Current.CancellationToken);

        suggestion.DraftComment.Should().BeNull();
        _store.Calls.Should().HaveCount(2);
        _store.Calls[0].Reason.Should().Be("Normalize:Exception");
    }

    [Fact]
    public async Task Triage_BackoffHonoursCancellation()
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        var pipeline = new TriagePipeline(
            new TicketNormalizer(NullLogger<TicketNormalizer>.Instance),
            new FixedSimilarSource(),
            new ScriptedClassifier(failures: null, onFail: async _ => await cts.CancelAsync()),
            new FakeRouter(new CallLog()),
            new FakeRoutingStatisticsSource(),
            new ScriptedDrafter(),
            Options.Create(new TriageOptions { RetryCount = 3, RetryDelayMilliseconds = 10000 }),
            _store,
            NullLogger<TriagePipeline>.Instance);

        var act = async () => await pipeline.TriageAsync(Tickets.Make("T-1"), cts.Token);

        await act.Should().ThrowAsync<OperationCanceledException>();
    }

    [Fact]
    public async Task Triage_StatisticsFailDuringFallback_ReturnsEmptyRouting()
    {
        var similar = new FixedSimilarSource(Old("OLD-1", "Incident", 0.9, Cat));
        var pipeline = Build(
            new ScriptedClassifier(failures: null),
            retryCount: 1,
            similar: similar,
            statistics: new FakeRoutingStatisticsSource(fail: true));

        var suggestion = await pipeline.TriageAsync(Tickets.Make("T-1"), TestContext.Current.CancellationToken);

        suggestion.DraftComment.Should().BeNull();
        suggestion.ServiceTeams.Should().BeEmpty();
        suggestion.Assignee.Should().BeNull();
    }

    [Fact]
    public async Task Triage_RouterDisagreesWithStatistics_RetriesThenFallsBackWithConsistentRouting_PerFR8()
    {
        var similar = new FixedSimilarSource(Old("OLD-1", "Incident", 0.9, Cat));
        var router = new FixedRouter(new RoutingDecision(["Other Team"], "bob"));
        var pipeline = Build(new ScriptedClassifier(), retryCount: 2, router: router, similar: similar);

        var suggestion = await pipeline.TriageAsync(Tickets.Make("T-1"), TestContext.Current.CancellationToken);

        _store.Calls.Should().HaveCount(2);
        _store.Calls.Should().OnlyContain(c => c.Reason == "Validate:InconsistentTeam,InconsistentAssignee");
        suggestion.DraftComment.Should().BeNull();
        suggestion.AffectedServices.Should().Equal(Cat);
        suggestion.ServiceTeams.Should().Equal("Team A");
        suggestion.Assignee.Should().Be("alice");
    }

    // Any flagged ticket makes the normalizer log, so this makes the normalizer throw.
    private sealed class ThrowingLogger : Microsoft.Extensions.Logging.ILogger<TicketNormalizer>
    {
        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull => null;

        public bool IsEnabled(Microsoft.Extensions.Logging.LogLevel logLevel) => true;

        public void Log<TState>(
            Microsoft.Extensions.Logging.LogLevel logLevel,
            Microsoft.Extensions.Logging.EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter) =>
            throw new InvalidOperationException("logger down");
    }

    private sealed class NoServiceClassifier : ITicketClassifier
    {
        public Task<TicketClassification> ClassifyAsync(Ticket ticket, IReadOnlyList<SimilarTicket> similarTickets, CancellationToken cancellationToken) =>
            Task.FromResult(new TicketClassification(WorkType.Incident, [], Urgency.High, Impact.Major));
    }
}

public class SuggestionValidatorTests
{
    private static readonly RoutingStatistics Stats = FakeRouting.Statistics;

    private static TriageSuggestion Valid() => new()
    {
        TicketKey = "T-1",
        WorkType = WorkType.Incident,
        AffectedServices = [FakeRouting.Service],
        ServiceTeams = ["Team A"],
        Assignee = "alice",
        Urgency = Urgency.High,
        Impact = Impact.Major,
        ResolutionStatus = ResolutionStatus.Done,
        DraftComment = "text",
    };

    private static IReadOnlyList<string> CodesOf(TriageSuggestion suggestion, RoutingStatistics? stats = null) =>
        FluentActions.Invoking(() => SuggestionValidator.Validate(suggestion, stats ?? Stats))
            .Should().Throw<TriageValidationException>().Which.Codes;

    [Fact]
    public void Validate_ValidSuggestion_DoesNotThrow_PerAC8() =>
        FluentActions.Invoking(() => SuggestionValidator.Validate(Valid(), Stats)).Should().NotThrow();

    [Fact]
    public void Validate_AllProblems_ReportsAllCodes_PerAC8()
    {
        var bad = Valid() with
        {
            WorkType = (WorkType)99,
            Urgency = (Urgency)99,
            Impact = (Impact)99,
            AffectedServices = ["Email", " "],
            ServiceTeams = ["Team A"],
            Assignee = "alice",
            ResolutionStatus = (ResolutionStatus)99,
            DraftComment = null,
        };

        CodesOf(bad).Should().BeEquivalentTo(
            [
                "InvalidWorkType", "InvalidUrgency", "InvalidImpact", "NoAffectedServices", "UnknownService",
                "InconsistentTeam", "InconsistentAssignee", "InvalidResolutionStatus", "EmptyComment",
            ]);
    }

    [Fact]
    public void Validate_InvalidWorkType_Throws_PerAC8() =>
        CodesOf(Valid() with { WorkType = (WorkType)99 }).Should().Equal("InvalidWorkType");

    [Fact]
    public void Validate_InvalidUrgency_Throws_PerAC8() =>
        CodesOf(Valid() with { Urgency = (Urgency)99 }).Should().Equal("InvalidUrgency");

    [Fact]
    public void Validate_InvalidImpact_Throws_PerAC8() =>
        CodesOf(Valid() with { Impact = (Impact)99 }).Should().Equal("InvalidImpact");

    [Fact]
    public void Validate_NullResolutionStatus_Throws_PerAC4() =>
        CodesOf(Valid() with { ResolutionStatus = null }).Should().Equal("InvalidResolutionStatus");

    [Fact]
    public void Validate_BlankComment_Throws_PerAC8() =>
        CodesOf(Valid() with { DraftComment = " " }).Should().Equal("EmptyComment");

    [Fact]
    public void Validate_EmptyServices_Throws_PerAC8() =>
        CodesOf(Valid() with { AffectedServices = [], ServiceTeams = [], Assignee = null }).Should().Equal("NoAffectedServices");

    [Fact]
    public void Validate_ServiceNotCanonical_ReportsUnknownService_PerAC3()
    {
        var lower = Valid() with { AffectedServices = [FakeRouting.Service.ToUpperInvariant()] };

        CodesOf(lower).Should().Equal("UnknownService");
    }

    [Fact]
    public void Validate_UnknownServiceWithEmptyRouting_OnlyReportsUnknownService_PerAC3() =>
        CodesOf(Valid() with { AffectedServices = ["Email"], ServiceTeams = [], Assignee = null }).Should().Equal("UnknownService");

    [Fact]
    public void Validate_UnknownServiceWithTeam_ReportsInconsistentTeam_PerAC3() =>
        CodesOf(Valid() with { AffectedServices = ["Email"], Assignee = null }).Should().Equal("UnknownService", "InconsistentTeam");

    [Fact]
    public void Validate_CatalogServiceUnknownToStatisticsWithEmptyRouting_Passes_PerAC3()
    {
        var suggestion = Valid() with { ServiceTeams = [], Assignee = null };

        FluentActions.Invoking(() => SuggestionValidator.Validate(suggestion, RoutingStatistics.Empty)).Should().NotThrow();
    }

    [Fact]
    public void Validate_KnownServiceWithoutTeam_ReportsMissingTeam_PerAC3() =>
        CodesOf(Valid() with { ServiceTeams = [] }).Should().Equal("MissingTeam");

    [Theory]
    [InlineData("Other Team")]
    [InlineData("team a")]
    public void Validate_TeamDiffersFromStatistics_ReportsInconsistentTeam_PerAC3(string team) =>
        CodesOf(Valid() with { ServiceTeams = [team] }).Should().Equal("InconsistentTeam");

    [Fact]
    public void Validate_ExtraTeam_ReportsInconsistentTeam_PerAC3() =>
        CodesOf(Valid() with { ServiceTeams = ["Team A", "Team B"] }).Should().Equal("InconsistentTeam");

    [Fact]
    public void Validate_KnownServiceWithoutAssignee_ReportsMissingAssignee_PerAC3() =>
        CodesOf(Valid() with { Assignee = null }).Should().Equal("MissingAssignee");

    [Fact]
    public void Validate_AssigneeDiffersFromStatistics_ReportsInconsistentAssignee_PerAC3() =>
        CodesOf(Valid() with { Assignee = "bob" }).Should().Equal("InconsistentAssignee");

    [Fact]
    public void Validate_StatisticsWithoutAssignee_RequiresNullAssignee_PerAC3()
    {
        var stats = RoutingStatistics.Build([(FakeRouting.Service, "Team A", null, 1)]);

        FluentActions.Invoking(() => SuggestionValidator.Validate(Valid() with { Assignee = null }, stats)).Should().NotThrow();
        CodesOf(Valid(), stats).Should().Equal("InconsistentAssignee");
    }

    [Fact]
    public void Validate_PriorityIsAlwaysMatrixDerived_PerAC2()
    {
        foreach (var urgency in Enum.GetValues<Urgency>())
        {
            foreach (var impact in Enum.GetValues<Impact>())
            {
                var suggestion = Valid() with { Urgency = urgency, Impact = impact };

                suggestion.Priority.Should().Be(PriorityMatrix.Resolve(urgency, impact));
                FluentActions.Invoking(() => SuggestionValidator.Validate(suggestion, Stats)).Should().NotThrow();
            }
        }
    }
}

public class FallbackSuggestionFactoryTests
{
    private static readonly string S0 = ServiceCatalog.All[0].Name;
    private static readonly string S1 = ServiceCatalog.All[1].Name;

    private static SimilarTicket Old(string? workType, double score, params string[] services) =>
        new(Tickets.Make("O") with { WorkType = workType, AffectedServices = services }, score);

    [Fact]
    public void Classify_NoSimilarTickets_ReturnsIncidentNoServices_PerAC5()
    {
        var result = FallbackSuggestionFactory.Classify([]);

        result.WorkType.Should().Be(WorkType.Incident);
        result.AffectedServices.Should().BeEmpty();
        result.Urgency.Should().Be(Urgency.Medium);
        result.Impact.Should().Be(Impact.Moderate);
    }

    [Fact]
    public void Classify_ParsesServiceRequestName_PerAC5() =>
        FallbackSuggestionFactory.Classify([Old("Service Request", 0.1)]).WorkType.Should().Be(WorkType.ServiceRequest);

    [Fact]
    public void Classify_TieOnCount_HigherScoreWins_PerAC5()
    {
        var result = FallbackSuggestionFactory.Classify(
            [Old("Incident", 0.2, S0), Old("Service Request", 0.7, S1)]);

        result.WorkType.Should().Be(WorkType.ServiceRequest);
        result.AffectedServices.Should().Equal(S1);
    }

    [Fact]
    public void Classify_FullTie_UsesEnumOrderAndOrdinalName_PerAC5()
    {
        var result = FallbackSuggestionFactory.Classify(
            [Old("Service Request", 0.5, S1), Old("Incident", 0.5, S0)]);

        var expectedService = string.CompareOrdinal(S0, S1) < 0 ? S0 : S1;
        result.WorkType.Should().Be(WorkType.Incident);
        result.AffectedServices.Should().Equal(expectedService);
    }

    [Fact]
    public void Classify_OnlyNonCatalogServices_ReturnsEmptyServices_PerAC5() =>
        FallbackSuggestionFactory.Classify([Old("Incident", 0.9, "Not In Catalog"), Old("Incident", 0.8, "Not In Catalog")])
            .AffectedServices.Should().BeEmpty();

    [Fact]
    public void Classify_NonCatalogMoreFrequent_IsIgnoredInFavourOfCatalogService_PerAC5() =>
        FallbackSuggestionFactory.Classify([Old("Incident", 0.9, "Not In Catalog"), Old("Incident", 0.8, "Not In Catalog", S0)])
            .AffectedServices.Should().Equal(S0);

    private static Task<TriageSuggestion> Fallback(params SimilarTicket[] similar) =>
        Task.FromResult(FallbackSuggestionFactory.Create(Tickets.Make("T-1"), similar, FakeRouting.Statistics, NullLogger.Instance));

    [Fact]
    public void Create_RoutingComesFromStatisticsForFallbackService_PerFR8()
    {
        var result = FallbackSuggestionFactory.Create(
            Tickets.Make("T-1"), [Old("Incident", 0.9, S0)], FakeRouting.Statistics, NullLogger.Instance);

        result.ServiceTeams.Should().Equal("Team A");
        result.Assignee.Should().Be("alice");
    }

    [Fact]
    public void Create_ServiceUnknownToStatistics_HasNoRouting_PerFR8()
    {
        var result = FallbackSuggestionFactory.Create(
            Tickets.Make("T-1"), [Old("Incident", 0.9, S0)], RoutingStatistics.Empty, NullLogger.Instance);

        result.ServiceTeams.Should().BeEmpty();
        result.Assignee.Should().BeNull();
    }

    private static SimilarTicket WithStatus(string? status, double score) =>
        new(Tickets.Make("O") with { Resolution = status }, score);

    [Fact]
    public async Task Create_StatusIsMajorityOfSimilarTickets_PerAC4()
    {
        var result = await Fallback(WithStatus("done", 0.9), WithStatus("clarification", 0.2), WithStatus("clarification", 0.1));

        result.ResolutionStatus.Should().Be(ResolutionStatus.Clarification);
    }

    [Fact]
    public async Task Create_StatusTieOnCount_HigherSummedScoreWins_PerAC4()
    {
        var result = await Fallback(WithStatus("done", 0.2), WithStatus("cancelled", 0.7));

        result.ResolutionStatus.Should().Be(ResolutionStatus.Cancelled);
    }

    [Fact]
    public async Task Create_StatusFullTie_UsesEnumOrder_PerAC4()
    {
        var result = await Fallback(WithStatus("cannot reproduce", 0.5), WithStatus("cancelled", 0.5));

        result.ResolutionStatus.Should().Be(ResolutionStatus.Cancelled);
    }

    [Fact]
    public async Task Create_UnparsableOrMissingStatuses_AreIgnored_PerAC4()
    {
        var result = await Fallback(WithStatus("bogus", 0.9), WithStatus(null, 0.9), WithStatus("cannot reproduce", 0.1));

        result.ResolutionStatus.Should().Be(ResolutionStatus.CannotReproduce);
    }

    [Fact]
    public async Task Create_NoUsableStatus_DefaultsToDone_PerAC4()
    {
        (await Fallback()).ResolutionStatus.Should().Be(ResolutionStatus.Done);
        (await Fallback(WithStatus("bogus", 0.9))).ResolutionStatus.Should().Be(ResolutionStatus.Done);
    }

    [Fact]
    public void Classify_UnparseableWorkTypes_AreIgnored_PerAC5() =>
        FallbackSuggestionFactory.Classify([Old("Bogus", 0.9), Old(null, 0.9), Old("Service Request", 0.1)])
            .WorkType.Should().Be(WorkType.ServiceRequest);
}
