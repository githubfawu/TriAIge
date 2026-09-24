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

    private BatchRunner CreateRunner(string input, string output, ITriagePipeline? pipeline = null) => new(
        Options.Create(new BatchOptions { Input = input, Output = output }),
        pipeline ?? new FakeTriagePipeline(),
        TimeProvider.System,
        NullLogger<BatchRunner>.Instance);

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
        var pipeline = new FakeTriagePipeline(fallbackKeys: ["A-2"]);

        var summary = await CreateRunner(WriteInput("A-1", "A-2"), output, pipeline)
            .RunAsync(TestContext.Current.CancellationToken);

        var entries = ReadArray(output).EnumerateArray().ToList();
        entries.Should().HaveCount(2);
        entries[0].GetProperty("All Comments").GetArrayLength().Should().Be(1);
        entries[1].GetProperty("All Comments").GetArrayLength().Should().Be(0);
        summary.Fallbacks.Should().Be(1);
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

        var summary = await CreateRunner(WriteInput("A-1", "A-1"), output)
            .RunAsync(TestContext.Current.CancellationToken);

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
    public async Task RunAsync_PipelineCancelled_LeavesExistingOutputUntouched()
    {
        var output = _dir.Combine("result.json");
        await File.WriteAllTextAsync(output, "old", TestContext.Current.CancellationToken);

        var act = () => CreateRunner(WriteInput("A-1", "A-2"), output, new FakeTriagePipeline(cancelAtIndex: 1))
            .RunAsync(TestContext.Current.CancellationToken);

        await act.Should().ThrowAsync<OperationCanceledException>();
        File.ReadAllText(output).Should().Be("old");
    }

    [Fact]
    public async Task RunAsync_FewerResultsThanTickets_ThrowsAndWritesNothing()
    {
        var output = _dir.Combine("result.json");

        var act = () => CreateRunner(WriteInput("A-1", "A-2"), output, new ScriptedPipeline(1))
            .RunAsync(TestContext.Current.CancellationToken);

        await act.Should().ThrowAsync<InvalidOperationException>();
        File.Exists(output).Should().BeFalse();
        Directory.GetFiles(_dir.Path).Select(Path.GetFileName).Should().NotContain(n => n!.EndsWith(".tmp"));
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
    public async Task RunAsync_CancelledAfterStreamBeforeWrite_LeavesOldOutputUntouched()
    {
        var output = _dir.Combine("result.json");
        await File.WriteAllTextAsync(output, "old", TestContext.Current.CancellationToken);
        using var cts = new CancellationTokenSource();

        var act = () => CreateRunner(WriteInput("A-1"), output, new ScriptedPipeline(1, () => cts))
            .RunAsync(cts.Token);

        await act.Should().ThrowAsync<OperationCanceledException>();
        File.ReadAllText(output).Should().Be("old");
        Directory.GetFiles(_dir.Path).Select(Path.GetFileName).Should().NotContain(n => n!.EndsWith(".tmp"));
    }

    [Fact]
    public async Task RunAsync_OutputIsExistingDirectory_ThrowsBatchInputExceptionBeforePipeline()
    {
        var output = _dir.Combine("result.json");
        Directory.CreateDirectory(output);
        var pipeline = new ScriptedPipeline(1, () => throw new InvalidOperationException("pipeline ran"));

        var act = () => CreateRunner(WriteInput("A-1"), output, pipeline).RunAsync(TestContext.Current.CancellationToken);

        await act.Should().ThrowAsync<BatchInputException>();
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
