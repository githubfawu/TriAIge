using TicketTriage.Core.Domain;

namespace TicketTriage.Core.Tests;

public class ServiceTeamCatalogTests
{
    [Theory]
    [InlineData("Service Desk", "Service Desk")]
    [InlineData("  trading support ", "Trading Support")]
    public void Find_KnownTeam_ReturnsCanonicalName(string input, string expected) =>
        ServiceTeamCatalog.Find(input).Should().Be(expected);

    [Theory]
    [InlineData("Made Up Team")]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(null)]
    public void Find_UnknownOrBlank_ReturnsNull(string? input) =>
        ServiceTeamCatalog.Find(input).Should().BeNull();

    [Fact]
    public void NormalizeAssignee_StripsControlCharactersAndTrims() =>
        ServiceTeamCatalog.NormalizeAssignee("  ali\u0000ce\r\n\u001b[31m ").Should().Be("alice[31m");

    [Theory]
    [InlineData("")]
    [InlineData(" \t ")]
    [InlineData("\u0001\u0002")]
    public void NormalizeAssignee_NothingUsable_ReturnsNull(string input) =>
        ServiceTeamCatalog.NormalizeAssignee(input).Should().BeNull();

    [Fact]
    public void NormalizeAssignee_TooLong_ReturnsNull() =>
        ServiceTeamCatalog.NormalizeAssignee(new string('a', ServiceTeamCatalog.MaxNameLength + 1)).Should().BeNull();
}
