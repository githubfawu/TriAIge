using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using TicketTriage.Core.Abstractions;
using TicketTriage.Core.Domain;
using TicketTriage.Infrastructure.Pipeline;

namespace TicketTriage.Infrastructure.Tests;

public class StopSystemOnFailureTests
{
    private readonly CallLog _log = new();
    private readonly RecordingFailureStore _store;
    private readonly FakeLifetime _lifetime;

    public StopSystemOnFailureTests()
    {
        _store = new RecordingFailureStore(_log);
        _lifetime = new FakeLifetime(_log);
    }

    private TriagePipeline Build(ITicketClassifier classifier, bool stop, bool withLifetime = true) =>
        new(
            new TicketNormalizer(NullLogger<TicketNormalizer>.Instance),
            new FixedSimilarSource(new SimilarTicket(Tickets.Make("OLD-1") with { WorkType = "Incident", AffectedServices = ["Email"] }, 0.9)),
            classifier,
            new FakeRouter(_log),
            new FakeRoutingStatisticsSource(),
            new ScriptedDrafter(),
            Options.Create(new TriageOptions { RetryCount = 3, StopSystemOnFailure = stop, RetryDelayMilliseconds = 0 }),
            _store,
            NullLogger<TriagePipeline>.Instance,
            withLifetime ? _lifetime : null);

    [Fact]
    public async Task StopSystemOnFailure_logs_first_failure_then_requests_stop_before_any_retry()
    {
        var ct = TestContext.Current.CancellationToken;
        var classifier = new ScriptedClassifier(failures: null);
        var pipeline = Build(classifier, stop: true);

        var act = async () => await pipeline.TriageAsync(Tickets.Stream(Tickets.Make("T-1"), Tickets.Make("T-2")), ct).ToListAsync(ct);

        await act.Should().ThrowAsync<OperationCanceledException>();
        classifier.Calls.Should().Be(1);
        _store.Calls.Should().ContainSingle().Which.TicketKey.Should().Be("T-1");
        _log.Steps.Should().Equal("store", "stop");
    }

    [Fact]
    public async Task StopSystemOnFailure_persisted_retries_at_limit_falls_back_without_stopping()
    {
        _store.PersistedRetries = 3;
        var pipeline = Build(new ScriptedClassifier(failures: null), stop: true);

        var suggestion = await pipeline.TriageAsync(Tickets.Make("T-1"), TestContext.Current.CancellationToken);

        suggestion.DraftComment.Should().BeNull();
        _lifetime.StopCalls.Should().Be(0);
    }

    [Fact]
    public async Task StopSystemOnFailure_persisted_retries_below_limit_still_stops()
    {
        _store.PersistedRetries = 1;
        var pipeline = Build(new ScriptedClassifier(failures: null), stop: true);

        var act = async () => await pipeline.TriageAsync(Tickets.Make("T-1"), TestContext.Current.CancellationToken);

        await act.Should().ThrowAsync<OperationCanceledException>();
        _lifetime.StopCalls.Should().Be(1);
    }

    [Fact]
    public async Task StopSystemOnFailure_single_ticket_throws_OperationCanceled()
    {
        var pipeline = Build(new ScriptedClassifier(failures: null), stop: true);

        var act = async () => await pipeline.TriageAsync(Tickets.Make("T-1"), TestContext.Current.CancellationToken);

        await act.Should().ThrowAsync<OperationCanceledException>();
        _lifetime.StopCalls.Should().Be(1);
    }

    [Fact]
    public async Task StopSystemOnFailure_without_lifetime_still_stops_processing()
    {
        var ct = TestContext.Current.CancellationToken;
        var classifier = new ScriptedClassifier(failures: null);
        var pipeline = Build(classifier, stop: true, withLifetime: false);

        var act = async () => await pipeline.TriageAsync(Tickets.Stream(Tickets.Make("T-1"), Tickets.Make("T-2")), ct).ToListAsync(ct);

        await act.Should().ThrowAsync<OperationCanceledException>();
        classifier.Calls.Should().Be(1);
    }

    [Fact]
    public async Task StopSystemOnFailure_false_retries_and_falls_back_without_stopping()
    {
        var ct = TestContext.Current.CancellationToken;
        var classifier = new ScriptedClassifier(failures: 3);
        var pipeline = Build(classifier, stop: false);

        var result = await pipeline.TriageAsync(Tickets.Stream(Tickets.Make("T-1"), Tickets.Make("T-2")), ct).ToListAsync(ct);

        result.Should().HaveCount(2);
        _store.Calls.Should().HaveCount(3);
        _lifetime.StopCalls.Should().Be(0);
    }


    private sealed class FakeLifetime(CallLog log) : IHostApplicationLifetime
    {
        public int StopCalls { get; private set; }

        public CancellationToken ApplicationStarted => CancellationToken.None;

        public CancellationToken ApplicationStopping => CancellationToken.None;

        public CancellationToken ApplicationStopped => CancellationToken.None;

        public void StopApplication()
        {
            StopCalls++;
            log.Steps.Add("stop");
        }
    }
}
