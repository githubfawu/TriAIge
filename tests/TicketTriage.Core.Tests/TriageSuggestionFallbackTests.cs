using TicketTriage.Core.Domain;

namespace TicketTriage.Core.Tests;

public class TriageSuggestionFallbackTests
{
    [Theory]
    [InlineData(null, true)]
    [InlineData("", true)]
    [InlineData("   ", true)]
    [InlineData("Please restart the client.", false)]
    public void IsFallback_DependsOnBlankDraftComment_PerAC3(string? comment, bool expected)
    {
        var suggestion = new TriageSuggestion
        {
            TicketKey = "DB-1",
            WorkType = WorkType.Incident,
            Urgency = Urgency.High,
            Impact = Impact.Minor,
            DraftComment = comment,
        };

        suggestion.IsFallback.Should().Be(expected);
    }
}
