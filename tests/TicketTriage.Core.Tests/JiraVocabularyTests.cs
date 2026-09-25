using TicketTriage.Core.Domain;

namespace TicketTriage.Core.Tests;

public class JiraVocabularyTests
{
    [Theory]
    [InlineData(Urgency.Critical, "Highest")]
    [InlineData(Urgency.High, "High")]
    [InlineData(Urgency.Medium, "Medium")]
    [InlineData(Urgency.Low, "Low")]
    [InlineData(Urgency.Lowest, "Lowest")]
    public void ToJira_Urgency_UsesExportScale_PerAC2(Urgency urgency, string expected) =>
        JiraVocabulary.ToJira(urgency).Should().Be(expected);

    [Theory]
    [InlineData(Impact.Major, "Highest")]
    [InlineData(Impact.Significant, "High")]
    [InlineData(Impact.Moderate, "Medium")]
    [InlineData(Impact.Minor, "Low")]
    [InlineData(Impact.NoImpact, "Lowest")]
    public void ToJira_Impact_UsesExportScale_PerAC2(Impact impact, string expected) =>
        JiraVocabulary.ToJira(impact).Should().Be(expected);

    [Fact]
    public void RoundTrip_AllUrgencies_IsCaseInsensitive_PerAC2()
    {
        foreach (var urgency in Enum.GetValues<Urgency>())
        {
            var text = JiraVocabulary.ToJira(urgency);
            JiraVocabulary.TryParseUrgency(text.ToLowerInvariant(), out var lower).Should().BeTrue();
            JiraVocabulary.TryParseUrgency(text.ToUpperInvariant(), out var upper).Should().BeTrue();
            lower.Should().Be(urgency);
            upper.Should().Be(urgency);
        }
    }

    [Fact]
    public void RoundTrip_AllImpacts_IsCaseInsensitive_PerAC2()
    {
        foreach (var impact in Enum.GetValues<Impact>())
        {
            var text = JiraVocabulary.ToJira(impact);
            JiraVocabulary.TryParseImpact(text.ToLowerInvariant(), out var lower).Should().BeTrue();
            JiraVocabulary.TryParseImpact(text.ToUpperInvariant(), out var upper).Should().BeTrue();
            lower.Should().Be(impact);
            upper.Should().Be(impact);
        }
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("urgent")]
    public void TryParse_UnknownValue_ReturnsFalse(string? value)
    {
        JiraVocabulary.TryParseUrgency(value, out _).Should().BeFalse();
        JiraVocabulary.TryParseImpact(value, out _).Should().BeFalse();
    }
}
