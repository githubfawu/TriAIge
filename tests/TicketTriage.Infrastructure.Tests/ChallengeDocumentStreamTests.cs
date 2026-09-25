using System.Text;
using System.Text.Json.Nodes;
using TicketTriage.Core.Domain;
using TicketTriage.Infrastructure.Challenge;

namespace TicketTriage.Infrastructure.Tests;

public sealed class ChallengeDocumentStreamTests
{
    private const string Marker = "ZX-SECRET-MARKER-91";

    private static MemoryStream Stream(string json) => new(Encoding.UTF8.GetBytes(json));

    private static TriageResult Result() => new()
    {
        WorkType = WorkType.Incident,
        AffectedServices = ["Service A"],
        ServiceTeams = ["Team A"],
        Assignee = "Jane Doe",
        Priority = Priority.Medium,
        Urgency = "High",
        Impact = "Low",
        Comments = ["Draft."],
    };

    [Fact]
    public async Task ReadAsync_Envelope_ReadsRecordsWithPositionalKeys_PerAC1()
    {
        await using var stream = Stream("""{"runId":"r","records":[{"Summary":"s1"},{"Summary":"s2"}]}""");

        var doc = await ChallengeDocument.ReadAsync(stream, TestContext.Current.CancellationToken);

        doc.Tickets.Select(t => t.Key).Should().Equal("#1", "#2");
    }

    [Fact]
    public async Task ReadAsync_PlainArray_KeepsExistingKeys()
    {
        await using var stream = Stream("""[{"Issue key":"CH-7","Summary":"s"},{"Summary":"t"}]""");

        var doc = await ChallengeDocument.ReadAsync(stream, TestContext.Current.CancellationToken);

        doc.Tickets.Select(t => t.Key).Should().Equal("CH-7", "#2");
    }

    [Theory]
    [InlineData("""{"runId":"r"}""")]
    [InlineData("""{"records":{}}""")]
    [InlineData("""{"records":[]}""")]
    [InlineData("""{"records":["x"]}""")]
    [InlineData("null")]
    [InlineData("42")]
    [InlineData("")]
    public async Task ReadAsync_BadShape_ThrowsChallengeFormatException_PerAC3(string json)
    {
        await using var stream = Stream(json);

        var act = () => ChallengeDocument.ReadAsync(stream, TestContext.Current.CancellationToken);

        await act.Should().ThrowAsync<ChallengeFormatException>();
    }

    [Fact]
    public async Task ReadAsync_InvalidJsonWithText_DoesNotLeakText()
    {
        await using var stream = Stream("{\"records\":[{\"Summary\":\"" + Marker + "\",");

        var act = () => ChallengeDocument.ReadAsync(stream, TestContext.Current.CancellationToken);

        var ex = (await act.Should().ThrowAsync<ChallengeFormatException>()).Which;
        ex.Message.Should().NotContain(Marker);
    }

    [Fact]
    public async Task ReadAsync_RecordWithoutSummary_ThrowsWithoutRecordText()
    {
        await using var stream = Stream("{\"records\":[{\"Description\":\"" + Marker + "\"}]}");

        var act = () => ChallengeDocument.ReadAsync(stream, TestContext.Current.CancellationToken);

        var ex = (await act.Should().ThrowAsync<ChallengeFormatException>()).Which;
        ex.Message.Should().Contain("index 0").And.NotContain(Marker);
    }

    [Fact]
    public async Task ReadAsync_StreamAboveSizeCap_IsRejectedWithoutValues()
    {
        await using var stream = Stream("[{\"Summary\":\"" + Marker + new string('x', (int)ChallengeDocument.MaxFileBytes) + "\"}]");

        var act = () => ChallengeDocument.ReadAsync(stream, TestContext.Current.CancellationToken);

        var ex = (await act.Should().ThrowAsync<ChallengeFormatException>()).Which;
        ex.Message.Should().Contain("limit").And.NotContain(Marker);
    }

    [Fact]
    public void Parse_MoreRecordsThanCap_IsRejectedWithoutValues()
    {
        var array = new JsonArray();
        for (var i = 0; i <= ChallengeDocument.MaxRecords; i++)
        {
            array.Add(new JsonObject { ["Summary"] = Marker });
        }

        var act = () => ChallengeDocument.Parse(array);

        act.Should().Throw<ChallengeFormatException>().Which.Message.Should().Contain("500").And.NotContain(Marker);
    }

    [Fact]
    public void Parse_NullNode_Throws()
    {
        var act = () => ChallengeDocument.Parse(null);

        act.Should().Throw<ChallengeFormatException>();
    }

    [Fact]
    public async Task WriteAsync_ToOutput_IsIndentedAndRoundTrips_PerAC5()
    {
        await using var input = Stream("""[{"Summary":"s1"}]""");
        var doc = await ChallengeDocument.ReadAsync(input, TestContext.Current.CancellationToken);
        using var output = new MemoryStream();

        await ChallengeDocument.WriteAsync(doc.ToOutput([Result()]), output, TestContext.Current.CancellationToken);

        var text = Encoding.UTF8.GetString(output.ToArray());
        text.Should().Contain("\n").And.NotContain("Issue key");
        var parsed = JsonNode.Parse(text)!.AsArray();
        parsed[0]!["Summary"]!.GetValue<string>().Should().Be("s1");
        parsed[0]!["Assignee"]!.GetValue<string>().Should().Be("Jane Doe");
    }
}
