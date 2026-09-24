using TicketTriage.Infrastructure.Retrieval;

namespace TicketTriage.Infrastructure.Tests;

public class TfIdfIndexTests
{
    private static TfIdfIndex BuildIndex(params (int Id, string Text)[] docs) =>
        TfIdfIndex.Build([.. docs.Select(d => new CorpusDocument(d.Id, d.Text))]);

    [Fact]
    [Trait("Category", "Smoke")]
    public void Search_RealisticMixedLanguageCorpus_ReturnsSaneRankedHits()
    {
        var index = BuildIndex(
            (1, "Outlook startet nicht mehr, Fehlermeldung beim Öffnen des Postfachs."),
            (2, "VPN connection drops every few minutes when working from home."),
            (3, "Neues Passwort für das Benutzerkonto zurücksetzen bitte."),
            (4, "Printer on floor 3 shows paper jam error."),
            (5, "Outlook Postfach synchronisiert nicht, Fehler 0x80040115."));

        var hits = index.Search("Outlook Postfach Fehler nach Update", top: 3, excludeId: null);

        index.DocumentCount.Should().Be(5);
        index.TermCount.Should().BeGreaterThan(0);
        hits.Should().NotBeEmpty().And.HaveCountLessThanOrEqualTo(3);
        hits.Select(h => h.Id).Should().OnlyContain(id => id == 1 || id == 5);
        hits.Should().OnlyContain(h => h.Score > 0 && h.Score <= 1.0);
    }

    [Fact]
    public void Search_DistinctiveSharedTerm_RanksThatDocumentFirst_PerAC1()
    {
        var index = BuildIndex(
            (1, "user printer paper jam"),
            (2, "user vpn connection timeout"),
            (3, "user outlook crash startup"));

        var hits = index.Search("user vpn connection", top: 5, excludeId: null);

        hits[0].Id.Should().Be(2);
        hits.Should().HaveCount(3);
        hits[0].Score.Should().BeGreaterThan(hits[1].Score);
    }

    [Fact]
    public void Search_ReturnsAtMostTop_StrictlyDescending_ScoresInUnitInterval_PerAC2()
    {
        var index = BuildIndex(
            (1, "server disk full"),
            (2, "server disk"),
            (3, "server"),
            (4, "server memory leak disk"),
            (5, "server memory"));

        var hits = index.Search("server disk full", top: 3, excludeId: null);
        var all = index.Search("server disk full", top: 100, excludeId: null);

        hits.Should().HaveCount(3);
        all.Should().HaveCount(5);
        hits.Zip(hits.Skip(1)).Should().OnlyContain(p => p.First.Score > p.Second.Score);
        all.Zip(all.Skip(1)).Should().OnlyContain(p => p.First.Score > p.Second.Score);
        all.Should().OnlyContain(h => h.Score > 0 && h.Score <= 1.0);
        hits.Select(h => h.Id).Should().Equal(all.Take(3).Select(h => h.Id));
    }

    [Fact]
    public void Search_EqualScores_OrderedByIdAscending()
    {
        var index = BuildIndex(
            (9, "alpha beta"),
            (3, "alpha beta"),
            (5, "alpha beta"),
            (1, "gamma delta"));

        var hits = index.Search("alpha beta", top: 10, excludeId: null);

        hits.Select(h => h.Id).Should().Equal(3, 5, 9);
        hits.Select(h => h.Score).Distinct().Should().HaveCount(1);
    }

    [Fact]
    public void Search_ExcludeId_NeverReturnsThatDocument_EvenIfIdentical_PerAC3()
    {
        var index = BuildIndex(
            (1, "reset password portal"),
            (2, "reset password portal"),
            (3, "unrelated hardware order"));

        var hits = index.Search("reset password portal", top: 10, excludeId: 1);

        hits.Select(h => h.Id).Should().Equal(2);
    }

    [Fact]
    public void Search_NoExcludeId_ReturnsIdenticalDocument()
    {
        var index = BuildIndex((1, "reset password portal"), (2, "other words here"));

        var hits = index.Search("reset password portal", top: 10, excludeId: null);

        hits.Select(h => h.Id).Should().Equal(1);
    }

    [Fact]
    public void Search_IdenticalText_ScoreIsExactlyOne()
    {
        var index = BuildIndex(
            (1, "network drive mapping failed on laptop after windows update"),
            (2, "network printer offline"),
            (3, "laptop battery replacement"));

        var hits = index.Search("network drive mapping failed on laptop after windows update", top: 5, excludeId: null);

        hits[0].Id.Should().Be(1);
        hits[0].Score.Should().Be(1.0);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("!!! a ?")]
    public void Search_BlankQuery_ReturnsEmpty_PerAC4(string? query)
    {
        var index = BuildIndex((1, "some indexed text"));

        index.Search(query, top: 5, excludeId: null).Should().BeEmpty();
    }

    [Fact]
    public void Search_EmptyCorpus_ReturnsEmpty_PerAC4()
    {
        var index = TfIdfIndex.Build([]);

        index.DocumentCount.Should().Be(0);
        index.TermCount.Should().Be(0);
        index.Search("anything at all", top: 5, excludeId: null).Should().BeEmpty();
    }

    [Fact]
    public void Search_OnlyOutOfVocabularyTerms_ReturnsEmpty()
    {
        var index = BuildIndex((1, "known words only"));

        index.Search("zzzz qqqq", top: 5, excludeId: null).Should().BeEmpty();
    }

    [Fact]
    public void Search_OutOfVocabularyTermsAlongsideKnownOnes_AreIgnored_NotPenalised()
    {
        var index = BuildIndex((1, "alpha beta"), (2, "gamma delta"));

        var hits = index.Search("alpha beta zzzz qqqq", top: 5, excludeId: null);

        hits.Should().ContainSingle().Which.Score.Should().BeApproximately(1.0, 1e-9);
    }

    [Fact]
    public void Search_NoSharedTerm_DocumentIsDropped()
    {
        var index = BuildIndex((1, "apple banana"), (2, "cherry grape"));

        var hits = index.Search("apple", top: 5, excludeId: null);

        hits.Select(h => h.Id).Should().Equal(1);
    }

    [Fact]
    public void Search_RareTermOutweighsCommonTerm()
    {
        var index = BuildIndex(
            (1, "rare aaa"),
            (2, "common bbb"),
            (3, "common ccc"),
            (4, "common ddd"));

        var hits = index.Search("rare common", top: 10, excludeId: null);

        hits[0].Id.Should().Be(1);
        hits.Single(h => h.Id == 2).Score.Should().BeLessThan(hits[0].Score);
    }

    [Fact]
    public void Search_RepeatedTerm_UsesSublinearTf()
    {
        var index = BuildIndex(
            (1, "alpha alpha alpha alpha beta"),
            (2, "gamma delta"));

        var hits = index.Search("alpha", top: 5, excludeId: null);

        // idf is equal for every term here and cancels out in the normalisation.
        var tf = 1.0 + Math.Log(4);
        var expected = tf / Math.Sqrt(tf * tf + 1.0);
        hits.Should().ContainSingle().Which.Score.Should().BeApproximately(expected, 1e-9);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void Search_TopZeroOrNegative_ReturnsEmpty(int top)
    {
        var index = BuildIndex((1, "alpha beta"));

        index.Search("alpha beta", top, excludeId: null).Should().BeEmpty();
    }

    [Fact]
    public void Build_DocumentWithoutTokens_IsIgnored()
    {
        var index = BuildIndex(
            (1, "!!! ???"),
            (2, "a"),
            (3, "   "),
            (4, "real words"));

        index.DocumentCount.Should().Be(1);
        index.TermCount.Should().Be(2);
        index.Search("real words", top: 5, excludeId: null).Select(h => h.Id).Should().Equal(4);
    }

    [Fact]
    public void Build_NullDocuments_Throws()
    {
        var act = () => TfIdfIndex.Build(null!);

        act.Should().Throw<ArgumentNullException>();
    }

    [Fact]
    public void Search_ConcurrentCalls_ReturnSameResultsAsSequential()
    {
        var index = BuildIndex(
            (1, "outlook crash on startup"),
            (2, "vpn timeout error"),
            (3, "outlook vpn combination"),
            (4, "printer jam"));
        var expected = index.Search("outlook vpn error", top: 3, excludeId: null);
        var failures = 0;

        Parallel.For(0, 200, _ =>
        {
            if (!index.Search("outlook vpn error", top: 3, excludeId: null).SequenceEqual(expected))
            {
                Interlocked.Increment(ref failures);
            }
        });

        failures.Should().Be(0);
    }
}
