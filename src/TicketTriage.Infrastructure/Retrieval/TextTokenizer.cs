using System.Text;

namespace TicketTriage.Infrastructure.Retrieval;

/// <summary>
/// Unicode-aware tokenizer shared by index build and query vectorisation (FR2).
/// No stemming, no stop-word list — IDF down-weights common words instead.
/// </summary>
internal static class TextTokenizer
{
    /// <summary>Upper bound on characters considered, so a huge description cannot blow up build or query time.</summary>
    public const int MaxTextLength = 20_000;

    public static IReadOnlyList<string> Tokenize(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return [];
        }

        if (text.Length > MaxTextLength)
        {
            text = text[..MaxTextLength];
        }

        // NFC first so a decomposed "é" (e + U+0301) is one letter, not two tokens.
        var normalized = text.Normalize(NormalizationForm.FormC).ToLowerInvariant();

        List<string> tokens = [];
        var start = -1;
        for (var i = 0; i < normalized.Length; i++)
        {
            if (char.IsLetterOrDigit(normalized[i]))
            {
                if (start < 0)
                {
                    start = i;
                }

                continue;
            }

            if (start >= 0)
            {
                AddIfLongEnough(tokens, normalized, start, i);
                start = -1;
            }
        }

        if (start >= 0)
        {
            AddIfLongEnough(tokens, normalized, start, normalized.Length);
        }

        return tokens;
    }

    private static void AddIfLongEnough(List<string> tokens, string source, int start, int end)
    {
        if (end - start >= 2)
        {
            tokens.Add(source[start..end]);
        }
    }
}
