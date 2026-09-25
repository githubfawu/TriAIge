using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using TicketTriage.Core.Abstractions;
using TicketTriage.Core.Domain;

namespace TicketTriage.Batch.Tests;

public sealed class BatchCommandTests : IDisposable
{
    private const string Marker = "ZX-SECRET-MARKER-91";
    private const string OldOutput = "old output";

    private readonly TempDir _dir = new();
    private readonly StringWriter _stdout = new();
    private readonly StringWriter _stderr = new();

    public void Dispose() => _dir.Dispose();

    private string Output => _dir.Combine("result.json");

    private BatchRunner CreateRunner(string input, FakeAnalysis? analysis = null)
    {
        analysis ??= new FakeAnalysis();
        return new BatchRunner(
            Options.Create(new BatchOptions
            {
                Input = input,
                Output = Output,
                PollIntervalSeconds = 5,
                WaitTimeoutSeconds = 100,
                WorkerHeartbeatMaxAgeSeconds = 30,
                WorkerStartGraceSeconds = 20,
            }),
            analysis,
            analysis,
            new FakeFallback(),
            new TestTimeProvider(),
            NullLogger<BatchRunner>.Instance);
    }

    private Task<int> RunAsync(BatchRunner runner, CancellationToken? token = null) =>
        BatchCommand.ExecuteAsync(runner, _stdout, _stderr, token ?? TestContext.Current.CancellationToken);

    private string WriteInput(string content)
    {
        var path = _dir.Combine("challenge.json");
        File.WriteAllText(path, content);
        return path;
    }

    private string WriteTickets(params string[] keys) => WriteInput(
        "[" + string.Join(",", keys.Select(k => "{\"Issue key\":\"" + k + "\",\"Summary\":\"" + Marker + "\",\"Description\":\"" + Marker + "\"}")) + "]");

    private void SeedOldOutput() => File.WriteAllText(Output, OldOutput);

    private void AssertNothingWritten()
    {
        File.ReadAllText(Output).Should().Be(OldOutput);
        Directory.GetFiles(_dir.Path).Select(Path.GetFileName).Should().NotContain(n => n!.EndsWith(".tmp"));
    }

    [Theory]
    [InlineData("[]")]
    [InlineData("null")]
    [InlineData("[null]")]
    [InlineData("[{\"Issue key\": ")]
    [InlineData("not json")]
    public async Task ExecuteAsync_BadInputContent_Returns1AndKeepsExistingOutput_PerAC4(string content)
    {
        SeedOldOutput();

        var exitCode = await RunAsync(CreateRunner(WriteInput(content)));

        exitCode.Should().Be(1);
        _stderr.ToString().Should().NotBeNullOrWhiteSpace();
        AssertNothingWritten();
    }

    [Theory]
    [InlineData("{\"runId\":\"r\"}")]
    [InlineData("{\"records\":[]}")]
    public async Task ExecuteAsync_EnvelopeWithoutUsableRecords_Returns1AndKeepsExistingOutput_PerAC1(string content)
    {
        SeedOldOutput();

        var exitCode = await RunAsync(CreateRunner(WriteInput(content)));

        exitCode.Should().Be(1);
        _stderr.ToString().Should().NotBeNullOrWhiteSpace();
        AssertNothingWritten();
    }

    [Fact]
    public async Task ExecuteAsync_MissingInputFile_Returns1AndCreatesNoOutput_PerAC4()
    {
        var exitCode = await RunAsync(CreateRunner(_dir.Combine("missing.json")));

        exitCode.Should().Be(1);
        _stderr.ToString().Should().NotBeNullOrWhiteSpace();
        File.Exists(Output).Should().BeFalse();
    }

    [Fact]
    public async Task ExecuteAsync_InvalidInput_NeverIngests_PerAC4()
    {
        var analysis = new FakeAnalysis();

        await RunAsync(CreateRunner(WriteInput("[]"), analysis));

        analysis.IngestCalls.Should().Be(0);
    }

    [Fact]
    public async Task ExecuteAsync_CancelledWhileWaiting_Returns1AndKeepsOutput_PerAC6()
    {
        SeedOldOutput();
        using var cts = new CancellationTokenSource();
        var analysis = new FakeAnalysis { IsPending = (_, _) => true, OnPoll = _ => cts.Cancel() };

        var exitCode = await RunAsync(CreateRunner(WriteTickets("A-1", "A-2"), analysis), cts.Token);

        exitCode.Should().Be(1);
        _stderr.ToString().Should().Contain("cancelled").And.Contain("no output written");
        AssertNothingWritten();
    }

    [Fact]
    public async Task ExecuteAsync_WorkerNotRunning_Returns3WithStartWebHintAndKeepsOutput_PerAC6()
    {
        SeedOldOutput();
        var analysis = new FakeAnalysis { IsPending = (_, _) => true };

        var exitCode = await RunAsync(CreateRunner(WriteTickets("A-1"), analysis));

        exitCode.Should().Be(3);
        _stderr.ToString().Should().Contain("Analysis worker is not running - start TicketTriage.Web");
        AssertNothingWritten();
    }

    [Fact]
    public async Task ExecuteAsync_TicketsNotAnalysedInTime_Returns0PrintsNotAnalysedCount_PerAC6()
    {
        var time = new TestTimeProvider();
        var analysis = new FakeAnalysis { IsPending = (_, id) => id == 2, Heartbeat = () => time.UtcNow };
        var runner = new BatchRunner(
            Options.Create(new BatchOptions { Input = WriteTickets("A-1", "A-2", "A-3"), Output = Output }),
            analysis,
            analysis,
            new FakeFallback(),
            time,
            NullLogger<BatchRunner>.Instance);

        var exitCode = await RunAsync(runner);

        exitCode.Should().Be(0);
        _stdout.ToString().Should().Contain("fallback/failed: 1 (not analysed: 1)").And.Contain("Warning: 1 ticket(s)");
    }

    [Fact]
    public async Task ExecuteAsync_MissingInputOption_Returns2WithUsage_PerAC6()
    {
        var runner = new BatchRunner(
            new ThrowingOptions(),
            new FakeAnalysis(),
            new FakeAnalysis(),
            new FakeFallback(),
            TimeProvider.System,
            NullLogger<BatchRunner>.Instance);

        var exitCode = await RunAsync(runner);

        exitCode.Should().Be(2);
        _stderr.ToString().Should().Contain("Missing --input").And.Contain("Usage:");
    }

    [Fact]
    public async Task ExecuteAsync_InvalidOptions_DoesNotRunDatabaseInitialisation_PerAC6()
    {
        var runner = new BatchRunner(
            new ThrowingOptions(), new FakeAnalysis(), new FakeAnalysis(), new FakeFallback(), TimeProvider.System, NullLogger<BatchRunner>.Instance);
        var initialised = false;

        var exitCode = await BatchCommand.ExecuteAsync(
            runner, _stdout, _stderr, TestContext.Current.CancellationToken, _ => { initialised = true; return Task.CompletedTask; });

        exitCode.Should().Be(2);
        initialised.Should().BeFalse();
    }

    [Fact]
    public async Task ExecuteAsync_UnwritableOutput_DoesNotRunDatabaseInitialisation()
    {
        Directory.CreateDirectory(Output);
        var initialised = false;

        var exitCode = await BatchCommand.ExecuteAsync(
            CreateRunner(WriteTickets("A-1")), _stdout, _stderr, TestContext.Current.CancellationToken,
            _ => { initialised = true; return Task.CompletedTask; });

        exitCode.Should().Be(1);
        initialised.Should().BeFalse();
    }

    [Fact]
    public async Task ExecuteAsync_DatabaseInitialisationFails_Returns1WithTypeNameOnly()
    {
        var exitCode = await BatchCommand.ExecuteAsync(
            CreateRunner(WriteTickets("A-1")), _stdout, _stderr, TestContext.Current.CancellationToken,
            _ => throw new InvalidOperationException(Marker));

        exitCode.Should().Be(1);
        _stderr.ToString().Should().Contain(nameof(InvalidOperationException)).And.NotContain(Marker);
        File.Exists(Output).Should().BeFalse();
    }

    [Fact]
    public async Task ExecuteAsync_Success_Returns0AndPrintsOutputPath_PerAC6()
    {
        var exitCode = await RunAsync(CreateRunner(WriteTickets("A-1")));

        exitCode.Should().Be(0);
        _stdout.ToString().Should().Contain(Output);
        File.Exists(Output).Should().BeTrue();
    }

    [Fact]
    public async Task ExecuteAsync_Success_PrintsSummaryCounts_PerAC3()
    {
        var pipeline = new FakeAnalysis(Suggestions.Standard(["A-2"]));

        await RunAsync(CreateRunner(WriteTickets("A-1", "A-2", "A-3"), pipeline));

        _stdout.ToString().Should().Contain("Tickets: 3 · fallback/failed: 1 (not analysed: 0) · duration:").And.Contain("output: " + Output);
    }

    [Fact]
    public async Task ExecuteAsync_AllTicketsFallback_PrintsWarning_PerAC7()
    {
        var pipeline = new FakeAnalysis(Suggestions.Standard(["A-1", "A-2"]));

        var exitCode = await RunAsync(CreateRunner(WriteTickets("A-1", "A-2"), pipeline));

        exitCode.Should().Be(0);
        _stdout.ToString().Should().Contain("Warning: every ticket used the fallback");
    }

    [Fact]
    public async Task ExecuteAsync_MixedResults_PrintsNoWarning_PerAC7()
    {
        var pipeline = new FakeAnalysis(Suggestions.Standard(["A-1"]));

        await RunAsync(CreateRunner(WriteTickets("A-1", "A-2"), pipeline));

        _stdout.ToString().Should().NotContain("Warning");
    }

    [Fact]
    public async Task ExecuteAsync_NoFallbacks_PrintsNoWarning_PerAC7()
    {
        await RunAsync(CreateRunner(WriteTickets("A-1")));

        _stdout.ToString().Should().Contain("fallback/failed: 0").And.NotContain("Warning");
    }

    [Fact]
    public async Task ExecuteAsync_Success_StdoutContainsNoTicketText_PerAC3()
    {
        await RunAsync(CreateRunner(WriteTickets("A-1"), new FakeAnalysis(Suggestions.Standard(["A-1"]))));

        _stdout.ToString().Should().NotContain(Marker);
    }

    [Fact]
    public async Task ExecuteAsync_UnexpectedException_PrintsTypeNameOnly_PerAC6()
    {
        var exitCode = await RunAsync(CreateRunner(WriteTickets("A-1"), new FakeAnalysis { ThrowOnIngest = true }));

        exitCode.Should().Be(1);
        _stderr.ToString().Should().Contain(nameof(InvalidOperationException)).And.NotContain(Marker);
    }

    [Fact]
    public async Task ExecuteAsync_InvalidJsonWithTicketText_DoesNotLeakTextToConsole()
    {
        var exitCode = await RunAsync(CreateRunner(WriteInput("[{\"Issue key\":\"A-1\",\"Summary\":\"" + Marker + "\",\"Created\":\"" + Marker + "\"}]")));

        exitCode.Should().Be(1);
        _stderr.ToString().Should().NotBeNullOrWhiteSpace().And.NotContain(Marker);
        _stdout.ToString().Should().NotContain(Marker);
    }

    [Fact]
    [Trait("Category", "Smoke")]
    public async Task ExecuteAsync_RealisticInput_CompletesWithExit0_Smoke()
    {
        var exitCode = await RunAsync(CreateRunner(WriteTickets("CH-1", "CH-2")));

        exitCode.Should().Be(0);
        _stdout.ToString().Should().NotContain(Marker);
        _stderr.ToString().Should().BeEmpty();
    }

    private sealed class ThrowingOptions : IOptions<BatchOptions>
    {
        public BatchOptions Value => throw new OptionsValidationException(
            nameof(BatchOptions), typeof(BatchOptions), ["Missing --input <path>."]);
    }
}
