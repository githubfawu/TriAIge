using System.Text;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using TicketTriage.Core.Abstractions;
using TicketTriage.Core.Domain;
using TicketTriage.Infrastructure.Challenge;
using TicketTriage.Web.Upload;

namespace TicketTriage.Web.Tests;

public sealed class ChallengeUploadServiceTests
{
    private const string Marker = "SECRET-TICKET-TEXT";

    private readonly FakeAnalysis _analysis = new();
    private readonly FakeFallback _fallback = new();
    private readonly ManualTimeProvider _time = new();
    private int _scopeResolutions;

    private ChallengeUploadService CreateService()
    {
        var services = new ServiceCollection();
        services.AddScoped<ITicketIngestor>(_ =>
        {
            _scopeResolutions++;
            return _analysis;
        });
        var provider = services.BuildServiceProvider();
        return new ChallengeUploadService(
            provider.GetRequiredService<IServiceScopeFactory>(),
            _analysis,
            _fallback,
            Options.Create(new UploadOptions()),
            _time,
            NullLogger<ChallengeUploadService>.Instance);
    }

    private static MemoryStream Json(string json) => new(Encoding.UTF8.GetBytes(json));

    private static string Records(int count, bool withKeys = false) =>
        string.Join(",", Enumerable.Range(1, count).Select(i =>
            "{" + (withKeys ? $"\"Issue key\":\"K-{i}\"," : "") + $"\"Summary\":\"s{i}\",\"Description\":\"d{i}\"}}"));

    private static string Envelope(int count) => "{\"meta\":{\"x\":1},\"records\":[" + Records(count) + "]}";

    [Fact]
    public async Task UploadAsync_TwentyKeylessRecords_IngestsAsChallengeContentKeys_PerAC1()
    {
        var session = await CreateService().UploadAsync(Json(Envelope(20)), Xunit.TestContext.Current.CancellationToken);

        _analysis.LastOrigin.Should().Be(TicketOrigin.Challenge);
        _analysis.LastIngested.Select(t => t.Key).Should().OnlyHaveUniqueItems()
            .And.AllSatisfy(k => k.Should().MatchRegex("^#[0-9a-f]{16}$"));
        session.TicketIds.Should().HaveCount(20);
        session.Counts.Should().Be(new IngestCounts(20, 0, 0, 0));
    }

    [Fact]
    public async Task UploadAsync_DifferentKeylessFile_CreatesNewTicketsInsteadOfOverwriting()
    {
        var service = CreateService();
        var first = await service.UploadAsync(Json(Envelope(2)), Xunit.TestContext.Current.CancellationToken);

        var second = await service.UploadAsync(
            Json("""[{"Summary":"other 1"},{"Summary":"other 2"}]"""), Xunit.TestContext.Current.CancellationToken);

        second.Counts.Should().Be(new IngestCounts(2, 0, 0, 0));
        second.TicketIds.Should().NotIntersectWith(first.TicketIds);
    }

    [Fact]
    public async Task UploadAsync_PlainArray_IsAccepted_PerAC1()
    {
        var session = await CreateService().UploadAsync(Json("[" + Records(3, withKeys: true) + "]"), Xunit.TestContext.Current.CancellationToken);

        session.TicketIds.Should().HaveCount(3);
        _analysis.LastIngested.Select(t => t.Key).Should().Equal("K-1", "K-2", "K-3");
    }

    [Theory]
    [InlineData("{ not json")]
    [InlineData("[]")]
    [InlineData("42")]
    [InlineData("{\"records\":{}}")]
    public async Task UploadAsync_InvalidOrEmptyInput_ThrowsFormatException_AndIngestsNothing_PerAC4(string input)
    {
        var act = () => CreateService().UploadAsync(Json(input), Xunit.TestContext.Current.CancellationToken);

        await act.Should().ThrowAsync<ChallengeFormatException>();
        _analysis.IngestCalls.Should().Be(0);
    }

    [Fact]
    public async Task UploadAsync_ErrorMessage_DoesNotContainTicketValues_PerAC4()
    {
        var input = "[{\"Summary\":\"" + Marker + "\",\"Created\":\"" + Marker + "\"}]";

        var act = () => CreateService().UploadAsync(Json(input), Xunit.TestContext.Current.CancellationToken);

        var ex = await act.Should().ThrowAsync<ChallengeFormatException>();
        ex.Which.Message.Should().NotContain(Marker);
    }

    [Fact]
    public async Task UploadAsync_StreamLargerThanLimit_ThrowsFormatException_PerAC4()
    {
        await using var huge = new ZeroStream(ChallengeDocument.MaxFileBytes + 1);

        var act = () => CreateService().UploadAsync(huge, Xunit.TestContext.Current.CancellationToken);

        await act.Should().ThrowAsync<ChallengeFormatException>();
    }

    [Fact]
    public async Task UploadAsync_StreamThrowingIOException_IsMappedToFormatException_PerAC4()
    {
        await using var broken = new ThrowingStream();

        var act = () => CreateService().UploadAsync(broken, Xunit.TestContext.Current.CancellationToken);

        await act.Should().ThrowAsync<ChallengeFormatException>();
    }

    [Fact]
    public async Task UploadAsync_SameFileTwice_ReportsUnchangedAndLocked_PerAC3()
    {
        var service = CreateService();
        await service.UploadAsync(Json(Envelope(4)), Xunit.TestContext.Current.CancellationToken);
        _analysis.LockedKeys.Add(_analysis.LastIngested[1].Key);

        var second = await service.UploadAsync(Json(Envelope(4)), Xunit.TestContext.Current.CancellationToken);

        second.Counts.Should().Be(new IngestCounts(0, 0, 3, 1));
        second.TicketIds.Should().Equal(1, 2, 3, 4);
    }

    [Fact]
    public async Task UploadAsync_ResolvesIngestorFromNewScopePerUpload()
    {
        var service = CreateService();

        await service.UploadAsync(Json(Envelope(1)), Xunit.TestContext.Current.CancellationToken);
        await service.UploadAsync(Json(Envelope(1)), Xunit.TestContext.Current.CancellationToken);

        _scopeResolutions.Should().Be(2);
    }

    [Fact]
    public async Task UploadAsync_DuplicateRealKeys_AreReported()
    {
        var input = "[{\"Issue key\":\"A-1\",\"Summary\":\"a\"},{\"Issue key\":\"A-1\",\"Summary\":\"b\"}]";

        var session = await CreateService().UploadAsync(Json(input), Xunit.TestContext.Current.CancellationToken);

        session.DuplicateKeys.Should().Equal("A-1");
        session.TicketIds.Should().Equal(1, 1);
    }

    [Fact]
    public async Task GetProgressAsync_DuplicateIds_CountTotalOverDistinctIds()
    {
        var service = CreateService();
        var input = "[{\"Issue key\":\"A-1\",\"Summary\":\"a\"},{\"Issue key\":\"A-1\",\"Summary\":\"b\"},{\"Issue key\":\"A-2\",\"Summary\":\"c\"}]";
        var session = await service.UploadAsync(Json(input), Xunit.TestContext.Current.CancellationToken);
        _analysis.IsPending = id => id == 2;

        var progress = await service.GetProgressAsync(session, Xunit.TestContext.Current.CancellationToken);

        progress.Total.Should().Be(2);
        progress.Pending.Should().Be(1);
        progress.Analysed.Should().Be(1);
        progress.IsComplete.Should().BeFalse();
    }

    [Fact]
    public async Task GetProgressAsync_AllAnalysed_IsCompleteAndCountsFallbacks_PerAC2()
    {
        _analysis.BlankDraft = true;
        var service = CreateService();
        var session = await service.UploadAsync(Json(Envelope(3)), Xunit.TestContext.Current.CancellationToken);
        _analysis.Heartbeat = () => _time.UtcNow;

        var progress = await service.GetProgressAsync(session, Xunit.TestContext.Current.CancellationToken);

        progress.IsComplete.Should().BeTrue();
        progress.Analysed.Should().Be(3);
        progress.Fallbacks.Should().Be(3);
        progress.Worker.Should().Be(WorkerStatus.Alive);
    }

    [Fact]
    public async Task GetProgressAsync_NoHeartbeatWithinGrace_IsStarting_ThenNotRunning_PerAC2()
    {
        var service = CreateService();
        var session = await service.UploadAsync(Json(Envelope(2)), Xunit.TestContext.Current.CancellationToken);
        _analysis.IsPending = _ => true;

        var early = await service.GetProgressAsync(session, Xunit.TestContext.Current.CancellationToken);
        _time.Advance(TimeSpan.FromSeconds(121));
        var late = await service.GetProgressAsync(session, Xunit.TestContext.Current.CancellationToken);

        early.Worker.Should().Be(WorkerStatus.Starting);
        late.Worker.Should().Be(WorkerStatus.NotRunning);
        late.TimedOut.Should().BeFalse();
    }

    [Fact]
    public async Task GetProgressAsync_LiveWorkerPastWaitTimeout_IsTimedOut_PerAC2()
    {
        var service = CreateService();
        var session = await service.UploadAsync(Json(Envelope(2)), Xunit.TestContext.Current.CancellationToken);
        _analysis.IsPending = _ => true;
        _analysis.Heartbeat = () => _time.UtcNow;

        var before = await service.GetProgressAsync(session, Xunit.TestContext.Current.CancellationToken);
        _time.Advance(TimeSpan.FromSeconds(1200));
        var after = await service.GetProgressAsync(session, Xunit.TestContext.Current.CancellationToken);

        before.TimedOut.Should().BeFalse();
        after.Worker.Should().Be(WorkerStatus.Alive);
        after.TimedOut.Should().BeTrue();
    }

    [Fact]
    public async Task ExportAsync_PendingTicket_UsesFallbackOnce_PerAC2()
    {
        var service = CreateService();
        var input = "[{\"Issue key\":\"A-1\",\"Summary\":\"a\"},{\"Issue key\":\"A-1\",\"Summary\":\"a\"},{\"Issue key\":\"A-2\",\"Summary\":\"c\"}]";
        var session = await service.UploadAsync(Json(input), Xunit.TestContext.Current.CancellationToken);
        _analysis.IsPending = id => id == 1;

        var export = await service.ExportAsync(session, Xunit.TestContext.Current.CancellationToken);

        _fallback.Calls.Should().HaveCount(1);
        export.Rows.Should().HaveCount(3);
        export.NotAnalysed.Should().Be(2);
        export.Fallbacks.Should().Be(2);
        export.Rows.Select(r => r.WasAnalysed).Should().Equal(false, false, true);
    }

    [Fact]
    public async Task ExportAsync_KeylessEnvelope_MirrorsInputWithoutIssueKey_PerAC5()
    {
        var service = CreateService();
        var session = await service.UploadAsync(Json(Envelope(20)), Xunit.TestContext.Current.CancellationToken);

        var export = await service.ExportAsync(session, Xunit.TestContext.Current.CancellationToken);

        using var doc = JsonDocument.Parse(export.Json);
        doc.RootElement.GetProperty("meta").GetProperty("x").GetInt32().Should().Be(1);
        var records = doc.RootElement.GetProperty("records").EnumerateArray().ToList();
        records.Should().HaveCount(20);
        records.Select(r => r.EnumerateObject().Any(p => p.Name == "Issue key")).Should().OnlyContain(has => !has);
        records.Select(r => r.GetProperty("Summary").GetString()).Should().Equal(Enumerable.Range(1, 20).Select(i => "s" + i));
        records[0].GetProperty("Assignee").GetString().Should().Be(_analysis.LastIngested[0].Key);
    }

    [Fact]
    public async Task ExportAsync_Bytes_EqualBatchStyleWriterOutput_PerAC5()
    {
        var service = CreateService();
        var raw = Envelope(3);
        var session = await service.UploadAsync(Json(raw), Xunit.TestContext.Current.CancellationToken);

        var export = await service.ExportAsync(session, Xunit.TestContext.Current.CancellationToken);

        var expected = new MemoryStream();
        var document = ChallengeDocument.Parse(System.Text.Json.Nodes.JsonNode.Parse(raw));
        await ChallengeDocument.WriteAsync(
            document.ToOutput([.. export.Rows.Select(r => r.Result)]), expected, Xunit.TestContext.Current.CancellationToken);
        export.Json.Should().Equal(expected.ToArray());
    }

    [Fact]
    [Trait("Category", "Smoke")]
    public async Task Smoke_UploadPollExport_CompletesWithSaneShape()
    {
        var ct = Xunit.TestContext.Current.CancellationToken;
        var service = CreateService();
        _analysis.Heartbeat = () => _time.UtcNow;

        var session = await service.UploadAsync(Json(Envelope(20)), ct);
        var progress = await service.GetProgressAsync(session, ct);
        var export = await service.ExportAsync(session, ct);

        progress.Total.Should().Be(20);
        progress.IsComplete.Should().BeTrue();
        progress.Worker.Should().Be(WorkerStatus.Alive);
        export.Rows.Should().HaveCount(20);
        export.NotAnalysed.Should().Be(0);
        export.Json.Should().NotBeEmpty();
    }

    private sealed class ZeroStream(long length) : Stream
    {
        private long _position;

        public override bool CanRead => true;

        public override bool CanSeek => false;

        public override bool CanWrite => false;

        public override long Length => length;

        public override long Position
        {
            get => _position;
            set => throw new NotSupportedException();
        }

        public override int Read(byte[] buffer, int offset, int count)
        {
            var n = (int)Math.Min(count, length - _position);
            Array.Fill(buffer, (byte)' ', offset, n);
            _position += n;
            return n;
        }

        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            var n = (int)Math.Min(buffer.Length, length - _position);
            buffer.Span[..n].Fill((byte)' ');
            _position += n;
            return ValueTask.FromResult(n);
        }

        public override void Flush()
        {
        }

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    private sealed class ThrowingStream : Stream
    {
        public override bool CanRead => true;

        public override bool CanSeek => false;

        public override bool CanWrite => false;

        public override long Length => throw new NotSupportedException();

        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override int Read(byte[] buffer, int offset, int count) => throw new IOException("limit exceeded");

        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) =>
            throw new IOException("limit exceeded");

        public override void Flush()
        {
        }

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
