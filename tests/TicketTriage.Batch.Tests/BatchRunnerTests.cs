using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using TicketTriage.Core.Abstractions;
using TicketTriage.Core.Domain;

namespace TicketTriage.Batch.Tests;

public sealed class BatchRunnerTests : IDisposable
{
    private readonly TempDir _dir = new();

    public void Dispose() => _dir.Dispose();

    private readonly TestTimeProvider _time = new();

    private BatchRunner CreateRunner(
        string input,
        string output,
        FakeAnalysis? analysis = null,
        IFallbackSuggestionProvider? fallback = null,
        Action<BatchOptions>? configure = null)
    {
        var options = new BatchOptions
        {
            Input = input,
            Output = output,
            PollIntervalSeconds = 5,
            WaitTimeoutSeconds = 100,
            WorkerHeartbeatMaxAgeSeconds = 30,
            WorkerStartGraceSeconds = 20,
        };
        configure?.Invoke(options);
        analysis ??= new FakeAnalysis();
        return new BatchRunner(
            Options.Create(options), analysis, analysis, fallback ?? new FakeFallback(), _time, NullLogger<BatchRunner>.Instance);
    }

    private FakeAnalysis Analysis(
        Func<int, Ticket, TriageSuggestion>? suggest = null,
        Func<int, int, bool>? isPending = null,
        bool workerAlive = true)
    {
        var analysis = new FakeAnalysis(suggest);
        if (isPending is not null)
        {
            analysis.IsPending = isPending;
        }

        if (workerAlive)
        {
            analysis.Heartbeat = () => _time.UtcNow;
        }

        return analysis;
    }

    private string WriteInput(params string[] keys)
    {
        var path = _dir.Combine("challenge.json");
        var items = keys.Select(k => "{\"Issue key\":\"" + k + "\",\"Summary\":\"s " + k + "\",\"Description\":\"d\"}");
        File.WriteAllText(path, "[" + string.Join(",", items) + "]");
        return path;
    }

    private static JsonElement ReadArray(string path) => JsonDocument.Parse(File.ReadAllText(path)).RootElement;

    [Fact]
    public async Task RunAsync_WritesOneEntryPerTicketInInputOrder_PerAC1()
    {
        var output = _dir.Combine("result.json");

        var summary = await CreateRunner(WriteInput("A-2", "A-1", "A-3"), output)
            .RunAsync(TestContext.Current.CancellationToken);

        var keys = ReadArray(output).EnumerateArray().Select(e => e.GetProperty("Issue key").GetString());
        keys.Should().Equal("A-2", "A-1", "A-3");
        summary.Total.Should().Be(3);
        summary.OutputPath.Should().Be(output);
    }

    private string WriteKeylessEnvelope(int count)
    {
        var path = _dir.Combine("challenge.json");
        var records = Enumerable.Range(1, count).Select(i =>
            "{\"Work type\":\"Incident\",\"Summary\":\"s" + i + "\",\"Description\":\"d" + i + "\","
            + "\"Request type\":\"Access Removal\",\"Assignee\":null,\"Service Team(s)\":[],\"Resolution\":null,\"All Comments\":[]}");
        File.WriteAllText(path, "{\"runId\":\"r-1\",\"actualIssueCount\":" + count + ",\"records\":[" + string.Join(",", records) + "]}");
        return path;
    }

    [Fact]
    public async Task RunAsync_KeylessEnvelope_WritesMirroredEnvelopeInInputOrder_PerAC1()
    {
        var output = _dir.Combine("result.json");

        var summary = await CreateRunner(WriteKeylessEnvelope(20), output).RunAsync(TestContext.Current.CancellationToken);

        summary.Total.Should().Be(20);
        var root = ReadArray(output);
        root.GetProperty("runId").GetString().Should().Be("r-1");
        var records = root.GetProperty("records").EnumerateArray().ToList();
        records.Select(r => r.GetProperty("Summary").GetString()).Should().Equal(Enumerable.Range(1, 20).Select(i => "s" + i));
        foreach (var record in records)
        {
            record.TryGetProperty("Issue key", out _).Should().BeFalse();
            record.GetProperty("Request type").GetString().Should().Be("Access Removal");
        }
    }

    [Fact]
    public async Task RunAsync_AllUrgencyImpactPairs_PriorityMatchesMatrix_PerAC2()
    {
        var output = _dir.Combine("result.json");

        await CreateRunner(WriteKeylessEnvelope(25), output, new FakeAnalysis(Suggestions.Cycling))
            .RunAsync(TestContext.Current.CancellationToken);

        var records = ReadArray(output).GetProperty("records").EnumerateArray().ToList();
        records.Should().HaveCount(25);
        foreach (var record in records)
        {
            JiraVocabulary.TryParseUrgency(record.GetProperty("Urgency").GetString(), out var urgency).Should().BeTrue();
            JiraVocabulary.TryParseImpact(record.GetProperty("Impact").GetString(), out var impact).Should().BeTrue();
            record.GetProperty("Priority").GetString().Should().Be(PriorityMatrix.Resolve(urgency, impact).ToString());
        }
    }

    private static readonly string[] SevenFields =
    [
        "Work type", "Affected Business or IT Services", "Service Team(s)", "Assignee", "Priority", "Resolution", "All Comments",
    ];

    private static readonly string[] ResolutionVocabulary = ["done", "cancelled", "clarification", "cannot reproduce"];

    [Fact]
    public async Task RunAsync_EveryRecordHasSevenFieldsAndResolutionInVocabulary_PerAC2()
    {
        var output = _dir.Combine("result.json");

        await CreateRunner(WriteKeylessEnvelope(25), output, new FakeAnalysis(Suggestions.Cycling))
            .RunAsync(TestContext.Current.CancellationToken);

        var records = ReadArray(output).GetProperty("records").EnumerateArray().ToList();
        records.Should().HaveCount(25);
        foreach (var record in records)
        {
            foreach (var field in SevenFields)
            {
                record.TryGetProperty(field, out _).Should().BeTrue(field);
            }

            record.GetProperty("Resolution").GetString().Should().BeOneOf(ResolutionVocabulary);
        }

        records.Select(r => r.GetProperty("Resolution").GetString()).Distinct().Should().BeEquivalentTo(ResolutionVocabulary);
    }

    [Fact]
    public async Task RunAsync_FallbackRecord_AlsoCarriesResolutionStatus_PerAC2()
    {
        var output = _dir.Combine("result.json");

        await CreateRunner(WriteInput("A-1", "A-2"), output, new FakeAnalysis(Suggestions.Standard(["A-2"])))
            .RunAsync(TestContext.Current.CancellationToken);

        var fallback = ReadArray(output)[1];
        fallback.GetProperty("All Comments").GetArrayLength().Should().Be(0);
        fallback.GetProperty("Resolution").GetString().Should().BeOneOf(ResolutionVocabulary);
    }

    [Fact]
    public async Task RunAsync_PriorityEqualsMatrixResult_PerAC2()
    {
        var output = _dir.Combine("result.json");

        await CreateRunner(WriteInput("A-1"), output).RunAsync(TestContext.Current.CancellationToken);

        ReadArray(output)[0].GetProperty("Priority").GetString()
            .Should().Be(PriorityMatrix.Resolve(Urgency.High, Impact.NoImpact).ToString());
    }

    [Fact]
    public async Task RunAsync_FallbackTicket_StillWrittenWithEmptyComments_PerAC3()
    {
        var output = _dir.Combine("result.json");
        var analysis = new FakeAnalysis(Suggestions.Standard(["A-2"]));

        var summary = await CreateRunner(WriteInput("A-1", "A-2"), output, analysis)
            .RunAsync(TestContext.Current.CancellationToken);

        var entries = ReadArray(output).EnumerateArray().ToList();
        entries.Should().HaveCount(2);
        entries[0].GetProperty("All Comments").GetArrayLength().Should().Be(1);
        entries[1].GetProperty("All Comments").GetArrayLength().Should().Be(0);
        summary.Fallbacks.Should().Be(1);
        summary.NotAnalysed.Should().Be(0);
    }

    [Fact]
    public async Task RunAsync_ExistingOutput_IsReplacedAndNoTempFileRemains()
    {
        var output = _dir.Combine("result.json");
        await File.WriteAllTextAsync(output, "old", TestContext.Current.CancellationToken);

        await CreateRunner(WriteInput("A-1"), output).RunAsync(TestContext.Current.CancellationToken);

        File.ReadAllText(output).Should().NotBe("old");
        Directory.GetFiles(_dir.Path).Select(Path.GetFileName)
            .Should().BeEquivalentTo(["challenge.json", "result.json"]);
    }

    [Fact]
    public async Task RunAsync_MissingOutputDirectory_IsCreated()
    {
        var output = Path.Combine(_dir.Path, "nested", "out", "result.json");

        await CreateRunner(WriteInput("A-1"), output).RunAsync(TestContext.Current.CancellationToken);

        File.Exists(output).Should().BeTrue();
    }

    [Fact]
    public async Task RunAsync_DuplicateKeys_AreBothKept()
    {
        var output = _dir.Combine("result.json");
        var analysis = Analysis();

        var summary = await CreateRunner(WriteInput("A-1", "A-1"), output, analysis)
            .RunAsync(TestContext.Current.CancellationToken);

        analysis.LastIngested.Should().HaveCount(2);

        ReadArray(output).GetArrayLength().Should().Be(2);
        summary.Total.Should().Be(2);
    }

    [Fact]
    public async Task RunAsync_WritesJiraEnumNames()
    {
        var output = _dir.Combine("result.json");

        await CreateRunner(WriteInput("A-1"), output).RunAsync(TestContext.Current.CancellationToken);

        ReadArray(output)[0].GetProperty("Work type").GetString().Should().Be("Service Request");
    }

    [Fact]
    public async Task RunAsync_NullRoot_Throws()
    {
        var input = _dir.Combine("challenge.json");
        await File.WriteAllTextAsync(input, "null", TestContext.Current.CancellationToken);

        var act = () => CreateRunner(input, _dir.Combine("result.json")).RunAsync(TestContext.Current.CancellationToken);

        await act.Should().ThrowAsync<BatchInputException>();
        File.Exists(_dir.Combine("result.json")).Should().BeFalse();
    }

    [Fact]
    public async Task RunAsync_CancelledWhileWaiting_LeavesExistingOutputUntouched_PerAC6()
    {
        var output = _dir.Combine("result.json");
        await File.WriteAllTextAsync(output, "old", TestContext.Current.CancellationToken);
        using var cts = new CancellationTokenSource();
        var analysis = Analysis(isPending: (_, id) => id == 2);
        analysis.OnPoll = poll =>
        {
            if (poll == 2)
            {
                cts.Cancel();
            }
        };

        var act = () => CreateRunner(WriteInput("A-1", "A-2"), output, analysis).RunAsync(cts.Token);

        await act.Should().ThrowAsync<OperationCanceledException>();
        File.ReadAllText(output).Should().Be("old");
        Directory.GetFiles(_dir.Path).Select(Path.GetFileName).Should().NotContain(n => n!.EndsWith(".tmp"));
    }

    [Fact]
    public async Task RunAsync_IngestorReturnsWrongCount_ThrowsAndWritesNothing()
    {
        var output = _dir.Combine("result.json");
        var runner = new BatchRunner(
            Options.Create(new BatchOptions { Input = WriteInput("A-1", "A-2"), Output = output }),
            new ShortIngestor(),
            new FakeAnalysis(),
            new FakeFallback(),
            _time,
            NullLogger<BatchRunner>.Instance);

        var act = () => runner.RunAsync(TestContext.Current.CancellationToken);

        await act.Should().ThrowAsync<InvalidOperationException>();
        File.Exists(output).Should().BeFalse();
    }

    [Fact]
    public async Task RunAsync_IngestsChallengeTicketsWithContentKeys_PerAC6()
    {
        var analysis = Analysis();

        await CreateRunner(WriteKeylessEnvelope(3), _dir.Combine("result.json"), analysis)
            .RunAsync(TestContext.Current.CancellationToken);

        analysis.LastOrigin.Should().Be(TicketOrigin.Challenge);
        analysis.LastIngested.Select(t => t.Key).Should().HaveCount(3).And.OnlyHaveUniqueItems()
            .And.AllSatisfy(k => k.Should().MatchRegex("^#[0-9a-f]{16}$"));
    }

    [Fact]
    public async Task RunAsync_ShuffledCompletion_KeepsChallengeOrderAndOwnSuggestions_PerAC6()
    {
        var output = _dir.Combine("result.json");
        var doneAtPoll = new Dictionary<int, int> { [3] = 2, [1] = 4, [2] = 3 };
        var analysis = Analysis(isPending: (poll, id) => poll < doneAtPoll[id]);

        var summary = await CreateRunner(WriteInput("A-1", "A-2", "A-3"), output, analysis)
            .RunAsync(TestContext.Current.CancellationToken);

        var entries = ReadArray(output).EnumerateArray().ToList();
        entries.Select(e => e.GetProperty("Issue key").GetString()).Should().Equal("A-1", "A-2", "A-3");
        entries.Select(e => e.GetProperty("Assignee").GetString()).Should().Equal("A-1", "A-2", "A-3");
        analysis.Polls.Should().Be(4);
        summary.NotAnalysed.Should().Be(0);
    }

    [Fact]
    public async Task RunAsync_AllAnalysedButNoHeartbeat_ExportsImmediately_PerAC6()
    {
        var output = _dir.Combine("result.json");
        var analysis = Analysis(workerAlive: false);

        var summary = await CreateRunner(WriteInput("A-1", "A-2"), output, analysis)
            .RunAsync(TestContext.Current.CancellationToken);

        analysis.Polls.Should().Be(1);
        summary.Total.Should().Be(2);
        File.Exists(output).Should().BeTrue();
    }

    [Fact]
    public async Task RunAsync_WorkerAliveButTimeout_ExportsPendingWithFallbackAndCountsThem_PerAC6()
    {
        var output = _dir.Combine("result.json");
        var analysis = Analysis(isPending: (_, id) => id == 2);
        var fallback = new FakeFallback();

        var summary = await CreateRunner(WriteInput("A-1", "A-2", "A-3"), output, analysis, fallback)
            .RunAsync(TestContext.Current.CancellationToken);

        summary.NotAnalysed.Should().Be(1);
        summary.Fallbacks.Should().Be(1);
        fallback.Calls.Should().ContainSingle().Which.Key.Should().Be("A-2");
        var entries = ReadArray(output).EnumerateArray().ToList();
        entries.Select(e => e.GetProperty("All Comments").GetArrayLength()).Should().Equal(1, 0, 1);
        entries[1].GetProperty("Assignee").GetString().Should().Be("fallback");
        _time.UtcNow.Should().BeOnOrAfter(new DateTime(2026, 1, 1, 12, 0, 0, DateTimeKind.Utc).AddSeconds(100));
    }

    [Fact]
    public async Task RunAsync_DuplicateKeysPending_FallbackComputedOnceAndUsedForBothPositions()
    {
        var output = _dir.Combine("result.json");
        var fallback = new FakeFallback();

        var summary = await CreateRunner(
                WriteInput("A-1", "A-1"), output, Analysis(isPending: (_, _) => true), fallback)
            .RunAsync(TestContext.Current.CancellationToken);

        fallback.Calls.Should().ContainSingle();
        summary.NotAnalysed.Should().Be(2);
        ReadArray(output).GetArrayLength().Should().Be(2);
    }

    [Fact]
    public async Task RunAsync_PendingAndNoHeartbeatPastGrace_ThrowsWorkerNotRunningAndKeepsOldOutput_PerAC6()
    {
        var output = _dir.Combine("result.json");
        await File.WriteAllTextAsync(output, "old", TestContext.Current.CancellationToken);
        var analysis = Analysis(isPending: (_, _) => true, workerAlive: false);

        var act = () => CreateRunner(WriteInput("A-1"), output, analysis).RunAsync(TestContext.Current.CancellationToken);

        (await act.Should().ThrowAsync<BatchWorkerUnavailableException>()).Which.Message.Should().Contain("start TicketTriage.Web");
        File.ReadAllText(output).Should().Be("old");
        Directory.GetFiles(_dir.Path).Select(Path.GetFileName).Should().NotContain(n => n!.EndsWith(".tmp"));
        _time.UtcNow.Should().BeOnOrAfter(new DateTime(2026, 1, 1, 12, 0, 0, DateTimeKind.Utc).AddSeconds(20));
    }

    [Fact]
    public async Task RunAsync_PendingAndStaleHeartbeat_ThrowsWorkerNotRunningWithoutFile_PerAC6()
    {
        var output = _dir.Combine("result.json");
        var analysis = Analysis(isPending: (_, _) => true, workerAlive: false);
        var start = _time.UtcNow;
        analysis.Heartbeat = () => start.AddMinutes(-10);

        var act = () => CreateRunner(WriteInput("A-1"), output, analysis).RunAsync(TestContext.Current.CancellationToken);

        await act.Should().ThrowAsync<BatchWorkerUnavailableException>();
        File.Exists(output).Should().BeFalse();
    }

    [Fact]
    public async Task RunAsync_WorkerStartsWithinGrace_WaitsAndExports_PerAC6()
    {
        var output = _dir.Combine("result.json");
        var analysis = Analysis(isPending: (poll, _) => poll < 3, workerAlive: false);

        var summary = await CreateRunner(WriteInput("A-1"), output, analysis).RunAsync(TestContext.Current.CancellationToken);

        summary.NotAnalysed.Should().Be(0);
        File.Exists(output).Should().BeTrue();
    }

    [Theory]
    [InlineData(0, 1, 1, 1)]
    [InlineData(1, 0, 1, 1)]
    [InlineData(1, 1, 0, 1)]
    [InlineData(1, 1, 1, 0)]
    [InlineData(-1, 1, 1, 1)]
    public void BatchOptions_NonPositiveWaitSetting_IsInvalid_PerAC6(int poll, int timeout, int maxAge, int grace)
    {
        BatchOptions.IsValid(new BatchOptions
        {
            PollIntervalSeconds = poll,
            WaitTimeoutSeconds = timeout,
            WorkerHeartbeatMaxAgeSeconds = maxAge,
            WorkerStartGraceSeconds = grace,
        }).Should().BeFalse();
    }

    [Fact]
    public void BatchOptions_Defaults_AreValid_PerAC6() => BatchOptions.IsValid(new BatchOptions()).Should().BeTrue();

    private sealed class ShortIngestor : ITicketIngestor
    {
        public Task<IReadOnlyList<IngestResult>> IngestAsync(
            IReadOnlyList<Ticket> tickets, TicketOrigin origin, CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<IngestResult>>([new IngestResult(1, IngestOutcome.Created)]);
    }

    [Fact]
    public async Task RunAsync_OutputLockedAtWrite_KeepsOldOutputAndLeavesNoTemp()
    {
        if (!OperatingSystem.IsWindows())
        {
            return; // Replacing a file that another handle holds open only fails on Windows.
        }

        var output = _dir.Combine("result.json");
        await File.WriteAllTextAsync(output, "old", TestContext.Current.CancellationToken);
        using var held = new FileStream(output, FileMode.Open, FileAccess.Read, FileShare.None);

        var act = () => CreateRunner(WriteInput("A-1"), output).RunAsync(TestContext.Current.CancellationToken);

        var thrown = await Record.ExceptionAsync(act);
        thrown.Should().Match<Exception>(e => e is IOException || e is UnauthorizedAccessException);
        held.Dispose();
        File.ReadAllText(output).Should().Be("old");
        Directory.GetFiles(_dir.Path).Select(Path.GetFileName).Should().NotContain(n => n!.EndsWith(".tmp"));
    }

    [Fact]
    public async Task RunAsync_CancelledAfterAnalysisBeforeWrite_LeavesOldOutputUntouched()
    {
        var output = _dir.Combine("result.json");
        await File.WriteAllTextAsync(output, "old", TestContext.Current.CancellationToken);
        using var cts = new CancellationTokenSource();
        var fallback = new CancellingFallback(cts);

        // Alive worker, pending forever: the run times out, builds the fallback (which cancels) and must not write.
        var act = () => CreateRunner(WriteInput("A-1"), output, Analysis(isPending: (_, _) => true), fallback)
            .RunAsync(cts.Token);

        await act.Should().ThrowAsync<OperationCanceledException>();
        File.ReadAllText(output).Should().Be("old");
        Directory.GetFiles(_dir.Path).Select(Path.GetFileName).Should().NotContain(n => n!.EndsWith(".tmp"));
    }

    [Fact]
    public async Task RunAsync_OutputIsExistingDirectory_ThrowsBatchInputExceptionBeforeIngest()
    {
        var output = _dir.Combine("result.json");
        Directory.CreateDirectory(output);
        var analysis = new FakeAnalysis { ThrowOnIngest = true };

        var act = () => CreateRunner(WriteInput("A-1"), output, analysis).RunAsync(TestContext.Current.CancellationToken);

        await act.Should().ThrowAsync<BatchInputException>();
        analysis.IngestCalls.Should().Be(0);
    }

    private sealed class CancellingFallback(CancellationTokenSource cts) : IFallbackSuggestionProvider
    {
        public async Task<TriageSuggestion> CreateAsync(Ticket ticket, CancellationToken cancellationToken)
        {
            await cts.CancelAsync();
            return await new FakeFallback().CreateAsync(ticket, CancellationToken.None);
        }
    }

    [Fact]
    public async Task RunAsync_OutputDirectoryCannotBeCreated_ThrowsBatchInputException()
    {
        var blocker = _dir.Combine("blocker");
        await File.WriteAllTextAsync(blocker, "x", TestContext.Current.CancellationToken);

        var act = () => CreateRunner(WriteInput("A-1"), Path.Combine(blocker, "sub", "result.json"))
            .RunAsync(TestContext.Current.CancellationToken);

        await act.Should().ThrowAsync<BatchInputException>();
    }

    [Fact]
    public async Task RunAsync_RootOutputPath_ThrowsBatchInputException()
    {
        var root = Path.GetPathRoot(_dir.Path)!;

        var act = () => CreateRunner(WriteInput("A-1"), root).RunAsync(TestContext.Current.CancellationToken);

        await act.Should().ThrowAsync<BatchInputException>();
    }

    [Fact]
    public async Task RunAsync_InputEqualsOutput_ThrowsAndKeepsInput()
    {
        var input = WriteInput("A-1");
        var before = File.ReadAllText(input);

        var act = () => CreateRunner(input, input).RunAsync(TestContext.Current.CancellationToken);

        await act.Should().ThrowAsync<BatchInputException>();
        File.ReadAllText(input).Should().Be(before);
    }

    [Fact]
    public async Task RunAsync_InputEqualsOutputViaRelativeSegments_Throws()
    {
        var input = WriteInput("A-1");
        var alias = Path.Combine(_dir.Path, "sub", "..", "challenge.json");

        var act = () => CreateRunner(input, alias).RunAsync(TestContext.Current.CancellationToken);

        await act.Should().ThrowAsync<BatchInputException>();
    }

    [Fact]
    [Trait("Category", "Smoke")]
    public async Task RunAsync_RealisticInput_CompletesWithSaneShape_Smoke()
    {
        var input = _dir.Combine("challenge.json");
        await File.WriteAllTextAsync(input, """
            [{"Issue key":"CH-1","Summary":"VPN drops","Description":"Cannot connect since morning",
              "Work type":"Incident","Affected Business or IT Services":["VPN"],"Service Team(s)":[],
              "Created":"2024-05-01T10:00:00.000+02:00","All Comments":[]}]
            """, TestContext.Current.CancellationToken);
        var output = _dir.Combine("result.json");

        var summary = await CreateRunner(input, output).RunAsync(TestContext.Current.CancellationToken);

        summary.Total.Should().Be(1);
        summary.Duration.Should().BeGreaterThanOrEqualTo(TimeSpan.Zero);
        var entry = ReadArray(output)[0];
        entry.GetProperty("Issue key").GetString().Should().Be("CH-1");
        entry.TryGetProperty("Priority", out _).Should().BeTrue();
    }
}
