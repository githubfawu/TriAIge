using System.Text;
using TicketTriage.Web.Triage;

namespace TicketTriage.Web.Tests;

public class UploadParserTests
{
    private static readonly string FixturePath = Path.Combine(AppContext.BaseDirectory, "Fixtures", "challenge-sample.json");

    [Fact]
    public void Parse_FixtureFile_ReturnsFiveValidEntries_PerAC1()
    {
        var bytes = File.ReadAllBytes(FixturePath);

        var result = UploadParser.Parse(bytes);

        result.FileError.Should().BeNull();
        result.Entries.Should().HaveCount(5);
        result.Entries.Should().OnlyContain(e => e.IsValid);
    }

    [Fact]
    public void Parse_InvalidJson_ReturnsFileError_WithoutThrowing_PerAC3()
    {
        var result = UploadParser.Parse("{not json"u8.ToArray());

        result.FileError.Should().NotBeNull();
        result.Entries.Should().BeEmpty();
    }

    [Fact]
    public void Parse_ObjectRoot_ReturnsFileError_PerAC3()
    {
        var result = UploadParser.Parse("""{ "Issue key": "TT-1" }"""u8.ToArray());

        result.FileError.Should().Be("The JSON root must be an array of tickets.");
    }

    [Fact]
    public void Parse_EmptyArray_ReturnsFileError_PerAC3()
    {
        var result = UploadParser.Parse("[]"u8.ToArray());

        result.FileError.Should().Be("The file contains no tickets.");
    }

    [Fact]
    public void Parse_MoreThan200Entries_ReturnsFileError_PerAC3()
    {
        var sb = new StringBuilder("[");
        for (var i = 0; i < 201; i++)
        {
            if (i > 0)
            {
                sb.Append(',');
            }

            sb.Append($$"""{ "Issue key": "TT-{{i}}", "Summary": "Ticket {{i}}" }""");
        }

        sb.Append(']');

        var result = UploadParser.Parse(Encoding.UTF8.GetBytes(sb.ToString()));

        result.FileError.Should().Be("The file has more than 200 tickets.");
    }

    [Fact]
    public void Parse_FileLargerThan1MB_ReturnsFileError_WithoutParsing_PerAC3()
    {
        var oversized = new byte[UploadParser.MaxFileSizeBytes + 1];

        var result = UploadParser.Parse(oversized);

        result.FileError.Should().Be("File is larger than 1 MB.");
    }

    [Fact]
    public void Parse_EntryWithoutSummary_IsInvalid_OthersRemainValid_PerAC3()
    {
        var json = """
            [
              { "Issue key": "TT-1", "Summary": "Valid ticket" },
              { "Issue key": "TT-2", "Summary": "" }
            ]
            """;

        var result = UploadParser.Parse(Encoding.UTF8.GetBytes(json));

        result.Entries.Should().HaveCount(2);
        result.Entries[0].IsValid.Should().BeTrue();
        result.Entries[1].IsValid.Should().BeFalse();
        result.Entries[1].Reason.Should().Be("Summary is required.");
    }

    [Fact]
    public void Parse_DuplicateIssueKey_SecondEntryIsInvalid()
    {
        var json = """
            [
              { "Issue key": "TT-1", "Summary": "First" },
              { "Issue key": "TT-1", "Summary": "Second" }
            ]
            """;

        var result = UploadParser.Parse(Encoding.UTF8.GetBytes(json));

        result.Entries[0].IsValid.Should().BeTrue();
        result.Entries[1].IsValid.Should().BeFalse();
        result.Entries[1].Reason.Should().Contain("Duplicate issue key");
    }
}
