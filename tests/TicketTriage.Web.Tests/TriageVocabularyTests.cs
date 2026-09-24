using TicketTriage.Core.Domain;
using TicketTriage.Web.Tests.Support;
using TicketTriage.Web.Triage;

namespace TicketTriage.Web.Tests;

/// <summary>Guards Leitplanke 2 against the real seeded DB (not a hand-copied mirror): every Core enum value must
/// resolve to a seed row via <see cref="LookupCatalog"/>, so <see cref="TicketMapper"/>'s "missing seed"
/// exceptions never fire in practice.</summary>
public sealed class TriageVocabularyTests : IAsyncLifetime
{
    private TestDatabase _database = null!;
    private LookupCatalog _catalog = null!;

    public static TheoryData<WorkType> WorkTypes => new(Enum.GetValues<WorkType>());

    public static TheoryData<Urgency> Urgencies => new(Enum.GetValues<Urgency>());

    public static TheoryData<Impact> Impacts => new(Enum.GetValues<Impact>());

    public static TheoryData<Priority> Priorities => new(Enum.GetValues<Priority>());

    public async ValueTask InitializeAsync()
    {
        var cancellationToken = Xunit.TestContext.Current.CancellationToken;
        _database = await TestDatabase.CreateAsync(cancellationToken);
        _catalog = new LookupCatalog(_database.CreateFactory());
        await _catalog.EnsureLoadedAsync(cancellationToken);
    }

    public async ValueTask DisposeAsync() => await _database.DisposeAsync();

    [Theory]
    [MemberData(nameof(WorkTypes))]
    public void EveryWorkType_ExistsInSeed(WorkType workType) =>
        _catalog.FindWorkTypeId(TriageVocabulary.ToJsonName(workType)).Should().NotBeNull();

    [Theory]
    [MemberData(nameof(Urgencies))]
    public void EveryUrgency_ExistsInSeed(Urgency urgency) =>
        _catalog.FindUrgencyId(TriageVocabulary.ToJsonName(urgency)).Should().NotBeNull();

    [Theory]
    [MemberData(nameof(Impacts))]
    public void EveryImpact_TranslatesToASeededDbName(Impact impact) =>
        _catalog.FindImpactId(TriageVocabulary.DbImpactNameFor(impact)).Should().NotBeNull();

    [Theory]
    [MemberData(nameof(Priorities))]
    public void EveryPriority_ExistsInSeed(Priority priority) =>
        _catalog.FindPriorityId(TriageVocabulary.ToJsonName(priority)).Should().NotBeNull();

    [Fact]
    public void TranslateImpactNameToDb_AllFiveRawNames_TranslateToKnownDbNames()
    {
        TriageVocabulary.TranslateImpactNameToDb("Major").Should().Be("Highest");
        TriageVocabulary.TranslateImpactNameToDb("Significant").Should().Be("High");
        TriageVocabulary.TranslateImpactNameToDb("Moderate").Should().Be("Medium");
        TriageVocabulary.TranslateImpactNameToDb("Minor").Should().Be("Low");
        TriageVocabulary.TranslateImpactNameToDb("No Impact").Should().Be("Lowest");
    }

    [Fact]
    public void TranslateImpactNameToDb_UnknownName_PassesThroughUnchanged() =>
        TriageVocabulary.TranslateImpactNameToDb("Whatever").Should().Be("Whatever");

    [Fact]
    public void ParseEnumOrNull_UnknownValue_ReturnsNull() =>
        TriageVocabulary.ParseEnumOrNull<WorkType>("Not A Work Type").Should().BeNull();

    [Fact]
    public void ParseEnumOrNull_EmptyValue_ReturnsNull() =>
        TriageVocabulary.ParseEnumOrNull<Urgency>(null).Should().BeNull();

    [Fact]
    public void Truncate_ShorterThanMax_ReturnsUnchanged() =>
        TriageVocabulary.Truncate("short", 10).Should().Be("short");

    [Fact]
    public void Truncate_LongerThanMax_Cuts() =>
        TriageVocabulary.Truncate("0123456789ABC", 10).Should().Be("0123456789");
}
