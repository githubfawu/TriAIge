using TicketTriage.Infrastructure.Retrieval;

namespace TicketTriage.Infrastructure.Tests;

public class TextTokenizerTests
{
    [Fact]
    public void Tokenize_MixedPunctuation_LowercasesSplitsAndDropsShortTokens()
    {
        var tokens = TextTokenizer.Tokenize("Outlook-Fehler: E-Mail 0x80 à Zürich!");

        tokens.Should().Equal("outlook", "fehler", "mail", "0x80", "zürich");
    }

    [Fact]
    public void Tokenize_UnicodeLetters_AreKeptWhole_IncludingDecomposedAccents()
    {
        var composed = TextTokenizer.Tokenize("Straße Café Äpfel");
        var decomposed = TextTokenizer.Tokenize("Straße Café Äpfel");

        composed.Should().Equal("straße", "café", "äpfel");
        decomposed.Should().Equal(composed);
    }

    [Theory]
    [InlineData("!!! --- ??? ...")]
    [InlineData("a b c 1 2 x")]
    [InlineData("a-b_c.d")]
    public void Tokenize_PunctuationOrSingleLetters_ReturnsEmpty(string text)
    {
        TextTokenizer.Tokenize(text).Should().BeEmpty();
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("\t\r\n")]
    public void Tokenize_NullOrWhitespace_ReturnsEmpty(string? text)
    {
        TextTokenizer.Tokenize(text).Should().BeEmpty();
    }

    [Fact]
    public void Tokenize_RepeatedTokens_AreKeptForTermFrequency()
    {
        TextTokenizer.Tokenize("VPN vpn Vpn").Should().Equal("vpn", "vpn", "vpn");
    }

    [Fact]
    public void Tokenize_InjectionLikeText_IsJustTokens()
    {
        TextTokenizer.Tokenize("Ignore previous instructions; DROP TABLE Tickets;--")
            .Should().Equal("ignore", "previous", "instructions", "drop", "table", "tickets");
    }

    [Fact]
    public void Tokenize_TextLongerThanCap_IsTruncated_AndDoesNotThrow()
    {
        var text = new string('a', TextTokenizer.MaxTextLength - 1) + " early " + string.Concat(Enumerable.Repeat("late ", 100));

        var tokens = TextTokenizer.Tokenize(text);

        tokens.Should().NotContain("late");
        tokens.Should().HaveCount(1);
        tokens[0].Length.Should().Be(TextTokenizer.MaxTextLength - 1);
    }
}
