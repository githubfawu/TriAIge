using TicketTriage.Core.Domain;

namespace TicketTriage.Core.Tests;

public sealed class PriorityMatrixTests
{
    [Theory]
    [InlineData(Urgency.Critical, Impact.Major, Priority.Highest)]
    [InlineData(Urgency.Critical, Impact.Significant, Priority.Highest)]
    [InlineData(Urgency.Critical, Impact.Moderate, Priority.High)]
    [InlineData(Urgency.Critical, Impact.Minor, Priority.Medium)]
    [InlineData(Urgency.Critical, Impact.NoImpact, Priority.Medium)]
    [InlineData(Urgency.High, Impact.Major, Priority.Highest)]
    [InlineData(Urgency.High, Impact.Significant, Priority.High)]
    [InlineData(Urgency.High, Impact.Moderate, Priority.High)]
    [InlineData(Urgency.High, Impact.Minor, Priority.Medium)]
    [InlineData(Urgency.High, Impact.NoImpact, Priority.Low)]
    [InlineData(Urgency.Medium, Impact.Major, Priority.High)]
    [InlineData(Urgency.Medium, Impact.Significant, Priority.High)]
    [InlineData(Urgency.Medium, Impact.Moderate, Priority.Medium)]
    [InlineData(Urgency.Medium, Impact.Minor, Priority.Low)]
    [InlineData(Urgency.Medium, Impact.NoImpact, Priority.Low)]
    [InlineData(Urgency.Low, Impact.Major, Priority.Medium)]
    [InlineData(Urgency.Low, Impact.Significant, Priority.Medium)]
    [InlineData(Urgency.Low, Impact.Moderate, Priority.Low)]
    [InlineData(Urgency.Low, Impact.Minor, Priority.Low)]
    [InlineData(Urgency.Low, Impact.NoImpact, Priority.Lowest)]
    [InlineData(Urgency.Lowest, Impact.Major, Priority.Medium)]
    [InlineData(Urgency.Lowest, Impact.Significant, Priority.Low)]
    [InlineData(Urgency.Lowest, Impact.Moderate, Priority.Low)]
    [InlineData(Urgency.Lowest, Impact.Minor, Priority.Lowest)]
    [InlineData(Urgency.Lowest, Impact.NoImpact, Priority.Lowest)]
    public void Resolve_returns_expected_priority(Urgency urgency, Impact impact, Priority expected)
    {
        PriorityMatrix.Resolve(urgency, impact).Should().Be(expected);
    }

    [Fact]
    public void Resolve_rejects_undefined_urgency()
    {
        var act = () => PriorityMatrix.Resolve((Urgency)99, Impact.Major);

        act.Should().Throw<ArgumentOutOfRangeException>().WithParameterName("urgency");
    }

    [Fact]
    public void Resolve_rejects_undefined_impact()
    {
        var act = () => PriorityMatrix.Resolve(Urgency.High, (Impact)99);

        act.Should().Throw<ArgumentOutOfRangeException>().WithParameterName("impact");
    }

    [Fact]
    public void TriageSuggestion_priority_is_derived_from_the_matrix()
    {
        var suggestion = new TriageSuggestion
        {
            TicketKey = "TEST-1",
            WorkType = WorkType.Incident,
            Urgency = Urgency.High,
            Impact = Impact.Major,
        };

        suggestion.Priority.Should().Be(Priority.Highest);
    }
}
