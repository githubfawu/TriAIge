using System.Text.Json.Nodes;
using TicketTriage.Core.Domain;
using TicketTriage.Infrastructure.Challenge;

namespace TicketTriage.Batch.Tests;

public sealed class ChallengeDocumentTests : IDisposable
{
    private const string Marker = "ZX-SECRET-MARKER-91";

    private const string Envelope = """
        {"fetchedAtUtc":"2026-09-23T08:39:15Z","runId":"r-1","actualIssueCount":2,"mappedFieldKeys":["a","b"],
         "records":[
          {"Work type":"Incident","Summary":"s1","Description":"d1","Request type":"Access Removal","Severity":"S2",
           "Linked issues":[],"Assignee":"Wrong Person","Priority":"Lowest","Urgency":"Highest","Impact":"Highest",
           "Service Team(s)":["Wrong"],"Resolution":null,"All Comments":["reporter comment"]},
          {"Summary":"s2","Request type":"Nonsense"}]}
        """;

    private readonly TempDir _dir = new();

    public void Dispose() => _dir.Dispose();

    private string Write(string json)
    {
        var path = _dir.Combine("challenge.json");
        File.WriteAllText(path, json);
        return path;
    }

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
        var doc = await ChallengeFile.ReadAsync(Write(Envelope), TestContext.Current.CancellationToken);

        doc.Tickets.Select(t => t.Key).Should().Equal("#1", "#2");
        doc.Tickets.Select(t => t.Summary).Should().Equal("s1", "s2");
    }

    [Fact]
    public async Task ToOutput_Envelope_KeepsEnvelopeMetadata_PerAC1()
    {
        var doc = await ChallengeFile.ReadAsync(Write(Envelope), TestContext.Current.CancellationToken);

        var output = (JsonObject)doc.ToOutput([Result(), Result()]);

        output["runId"]!.GetValue<string>().Should().Be("r-1");
        output["actualIssueCount"]!.GetValue<int>().Should().Be(2);
        output["mappedFieldKeys"]!.AsArray().Should().HaveCount(2);
        output["records"]!.AsArray().Should().HaveCount(2);
    }

    [Fact]
    public async Task ToOutput_PlainArray_StaysArray_PerAC1()
    {
        var doc = await ChallengeFile.ReadAsync(
            Write("""[{"Summary":"s1"},{"Summary":"s2"}]"""), TestContext.Current.CancellationToken);

        var output = doc.ToOutput([Result(), Result()]);

        output.Should().BeOfType<JsonArray>().Which.Should().HaveCount(2);
    }

    [Fact]
    public async Task ToOutput_KeylessRecords_NeverAddIssueKey_PerAC1()
    {
        var doc = await ChallengeFile.ReadAsync(Write(Envelope), TestContext.Current.CancellationToken);

        var records = doc.ToOutput([Result(), Result()])["records"]!.AsArray();

        records.Select(r => ((JsonObject)r!).ContainsKey("Issue key")).Should().OnlyContain(x => !x);
    }

    [Fact]
    public async Task ToOutput_RecordWithIssueKey_KeepsIt()
    {
        var doc = await ChallengeFile.ReadAsync(
            Write("""[{"Issue key":"CH-7","Summary":"s"}]"""), TestContext.Current.CancellationToken);

        var record = (JsonObject)doc.ToOutput([Result()]).AsArray()[0]!;

        record["Issue key"]!.GetValue<string>().Should().Be("CH-7");
        doc.Tickets[0].Key.Should().Be("CH-7");
    }

    [Fact]
    public async Task ToOutput_PreservesExtraFieldsAndOrder_PerAC2()
    {
        var doc = await ChallengeFile.ReadAsync(Write(Envelope), TestContext.Current.CancellationToken);

        var first = (JsonObject)doc.ToOutput([Result(), Result()])["records"]!.AsArray()[0]!;

        first["Request type"]!.GetValue<string>().Should().Be("Access Removal");
        first["Severity"]!.GetValue<string>().Should().Be("S2");
        first["Linked issues"]!.AsArray().Should().BeEmpty();
        first.Select(p => p.Key).Take(3).Should().Equal("Work type", "Summary", "Description");
    }

    [Fact]
    public async Task ToOutput_PrefilledPredictedFields_AreOverwritten_PerAC2()
    {
        var doc = await ChallengeFile.ReadAsync(Write(Envelope), TestContext.Current.CancellationToken);

        var first = (JsonObject)doc.ToOutput([Result(), Result()])["records"]!.AsArray()[0]!;

        first["Assignee"]!.GetValue<string>().Should().Be("Jane Doe");
        first["Priority"]!.GetValue<string>().Should().Be("Medium");
        first["Urgency"]!.GetValue<string>().Should().Be("High");
        first["Impact"]!.GetValue<string>().Should().Be("Low");
        first["Service Team(s)"]!.AsArray().Select(n => n!.GetValue<string>()).Should().Equal("Team A");
        first["All Comments"]!.AsArray().Select(n => n!.GetValue<string>()).Should().Equal("Draft.");
    }

    [Fact]
    public async Task ToOutput_ResultCountMismatch_Throws()
    {
        var doc = await ChallengeFile.ReadAsync(Write(Envelope), TestContext.Current.CancellationToken);

        var act = () => doc.ToOutput([Result()]);

        act.Should().Throw<InvalidOperationException>();
    }

    [Theory]
    [InlineData("""{"runId":"r"}""")]
    [InlineData("""{"records":null}""")]
    [InlineData("""{"records":{}}""")]
    [InlineData("""{"records":[]}""")]
    [InlineData("""{"records":["x"]}""")]
    [InlineData("""{"records":[null]}""")]
    [InlineData("null")]
    [InlineData("42")]
    public async Task ReadAsync_BadShape_ThrowsBatchInputException_PerAC1(string json)
    {
        var act = () => ChallengeFile.ReadAsync(Write(json), TestContext.Current.CancellationToken);

        await act.Should().ThrowAsync<BatchInputException>();
    }

    [Fact]
    public async Task ReadAsync_RecordWithoutSummary_ThrowsWithoutRecordText()
    {
        var act = () => ChallengeFile.ReadAsync(
            Write("""{"records":[{"Description":"ZX-SECRET-MARKER-91"}]}"""), TestContext.Current.CancellationToken);

        var ex = (await act.Should().ThrowAsync<BatchInputException>()).Which;
        ex.Message.Should().Contain("index 0").And.NotContain(Marker);
    }

    [Fact]
    public async Task ReadAsync_InvalidJsonWithText_DoesNotLeakText()
    {
        var act = () => ChallengeFile.ReadAsync(
            Write("{\"records\":[{\"Summary\":\"" + Marker + "\","), TestContext.Current.CancellationToken);

        var ex = (await act.Should().ThrowAsync<BatchInputException>()).Which;
        ex.Message.Should().NotContain(Marker);
    }

    [Fact]
    public async Task DuplicateKeys_OnlyRealKeysCount()
    {
        var doc = await ChallengeFile.ReadAsync(
            Write("""[{"Issue key":"A-1","Summary":"a"},{"Issue key":"A-1","Summary":"b"},{"Summary":"c"},{"Summary":"d"}]"""),
            TestContext.Current.CancellationToken);

        doc.DuplicateKeys.Should().Equal("A-1");
    }

    [Fact]
    public async Task ReadAsync_FileAboveSizeCap_IsRejectedWithoutValues()
    {
        var path = _dir.Combine("big.json");
        await File.WriteAllTextAsync(path, "[{\"Summary\":\"" + Marker + new string('x', 10 * 1024 * 1024) + "\"}]", TestContext.Current.CancellationToken);

        var act = async () => await ChallengeFile.ReadAsync(path, TestContext.Current.CancellationToken);

        var ex = (await act.Should().ThrowAsync<BatchInputException>()).Which;
        ex.Message.Should().Contain("limit").And.NotContain(Marker);
    }

    [Fact]
    public async Task ReadAsync_MoreRecordsThanCap_IsRejectedWithoutValues()
    {
        var path = Write("[" + string.Join(',', Enumerable.Repeat("{\"Summary\":\"" + Marker + "\"}", 10_001)) + "]");

        var act = async () => await ChallengeFile.ReadAsync(path, TestContext.Current.CancellationToken);

        var ex = (await act.Should().ThrowAsync<BatchInputException>()).Which;
        ex.Message.Should().Contain("500").And.NotContain(Marker);
    }
}
