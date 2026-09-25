using System.Text.Json;
using TicketTriage.Core.Domain;

namespace TicketTriage.Core.Tests;

public class ResolutionStatusJsonTests
{
    [Theory]
    [InlineData(ResolutionStatus.Done, "\"done\"")]
    [InlineData(ResolutionStatus.Cancelled, "\"cancelled\"")]
    [InlineData(ResolutionStatus.Clarification, "\"clarification\"")]
    [InlineData(ResolutionStatus.CannotReproduce, "\"cannot reproduce\"")]
    public void Serialize_UsesLowercaseExportVocabulary_PerAC2(ResolutionStatus status, string json)
    {
        JsonSerializer.Serialize(status).Should().Be(json);
        JsonSerializer.Deserialize<ResolutionStatus>(json).Should().Be(status);
    }

    [Fact]
    public void TriageResult_From_WritesResolutionInLowercaseVocabulary_PerAC2()
    {
        var suggestion = new TriageSuggestion
        {
            TicketKey = "T-1",
            WorkType = WorkType.Incident,
            Urgency = Urgency.High,
            Impact = Impact.Minor,
            ResolutionStatus = ResolutionStatus.CannotReproduce,
            DraftComment = "text",
        };

        var json = JsonSerializer.Serialize(TriageResult.From(suggestion));

        json.Should().Contain("\"Resolution\":\"cannot reproduce\"");
    }
}
