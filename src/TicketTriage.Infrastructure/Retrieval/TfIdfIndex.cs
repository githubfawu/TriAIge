using System.Buffers;

namespace TicketTriage.Infrastructure.Retrieval;

/// <summary>A corpus row to index: <paramref name="Id"/> is the source row id, <paramref name="Text"/> the field to vectorise (FR1: ticket Description).</summary>
internal sealed record CorpusDocument(int Id, string Text);

/// <summary>A ranked search result. <see cref="Score"/> is cosine similarity, clamped to (0, 1].</summary>
internal readonly record struct SimilarityHit(int Id, double Score);

/// <summary>
/// Immutable, thread-safe TF-IDF + cosine kNN engine (FR1, FR2). No DB/DI dependency: built once from
/// in-memory documents, then <see cref="Search"/> can run concurrently since it touches no shared mutable state.
/// </summary>
internal sealed class TfIdfIndex
{
    private readonly int[] _ids;
    private readonly Posting[][] _postings;
    private readonly double[] _idf;
    private readonly Dictionary<string, int> _termIds;

    private TfIdfIndex(int[] ids, Posting[][] postings, double[] idf, Dictionary<string, int> termIds)
    {
        _ids = ids;
        _postings = postings;
        _idf = idf;
        _termIds = termIds;
    }

    public int DocumentCount => _ids.Length;

    public int TermCount => _idf.Length;

    public static TfIdfIndex Build(IReadOnlyList<CorpusDocument> documents)
    {
        ArgumentNullException.ThrowIfNull(documents);

        // Pass 1: tokenize once, collect per-document term counts and per-term document frequency.
        // A document with zero tokens is skipped entirely - it can never score > 0 and must not inflate N.
        List<(int Id, Dictionary<string, int> TermCounts)> kept = [];
        Dictionary<string, int> documentFrequency = [];
        foreach (var document in documents)
        {
            var tokens = TextTokenizer.Tokenize(document.Text);
            if (tokens.Count == 0)
            {
                continue;
            }

            Dictionary<string, int> termCounts = [];
            foreach (var token in tokens)
            {
                termCounts[token] = termCounts.GetValueOrDefault(token) + 1;
            }

            foreach (var term in termCounts.Keys)
            {
                documentFrequency[term] = documentFrequency.GetValueOrDefault(term) + 1;
            }

            kept.Add((document.Id, termCounts));
        }

        var documentCount = kept.Count;

        // Smoothed idf: ln((1+N)/(1+df)) + 1 is always >= 1, so a term present in every document still contributes.
        Dictionary<string, int> termIds = new(documentFrequency.Count);
        var idf = new double[documentFrequency.Count];
        foreach (var (term, df) in documentFrequency)
        {
            var termId = termIds.Count;
            termIds[term] = termId;
            idf[termId] = Math.Log((1.0 + documentCount) / (1.0 + df)) + 1.0;
        }

        // Pass 2: sublinear tf * idf per document, L2-normalised, then scattered into per-term posting lists.
        var ids = new int[documentCount];
        var postingLists = new List<Posting>[termIds.Count];
        for (var i = 0; i < postingLists.Length; i++)
        {
            postingLists[i] = [];
        }

        for (var docIndex = 0; docIndex < documentCount; docIndex++)
        {
            var (id, termCounts) = kept[docIndex];
            ids[docIndex] = id;

            var weights = new (int TermId, double Weight)[termCounts.Count];
            var w = 0;
            var sumSquares = 0.0;
            foreach (var (term, tf) in termCounts)
            {
                var termId = termIds[term];
                var weight = (1.0 + Math.Log(tf)) * idf[termId];
                weights[w++] = (termId, weight);
                sumSquares += weight * weight;
            }

            var norm = Math.Sqrt(sumSquares);
            foreach (var (termId, weight) in weights)
            {
                postingLists[termId].Add(new Posting(docIndex, weight / norm));
            }
        }

        var postings = new Posting[postingLists.Length][];
        for (var i = 0; i < postingLists.Length; i++)
        {
            postings[i] = [.. postingLists[i]];
        }

        return new TfIdfIndex(ids, postings, idf, termIds);
    }

    public IReadOnlyList<SimilarityHit> Search(string? text, int top, int? excludeId)
    {
        if (top <= 0 || DocumentCount == 0)
        {
            return [];
        }

        var tokens = TextTokenizer.Tokenize(text);
        if (tokens.Count == 0)
        {
            return [];
        }

        // Out-of-vocabulary query terms are dropped before normalisation (no IDF exists for them).
        Dictionary<string, int> queryTermCounts = [];
        foreach (var token in tokens)
        {
            if (_termIds.ContainsKey(token))
            {
                queryTermCounts[token] = queryTermCounts.GetValueOrDefault(token) + 1;
            }
        }

        if (queryTermCounts.Count == 0)
        {
            return [];
        }

        var queryWeights = new (int TermId, double Weight)[queryTermCounts.Count];
        var qw = 0;
        var sumSquares = 0.0;
        foreach (var (term, tf) in queryTermCounts)
        {
            var termId = _termIds[term];
            var weight = (1.0 + Math.Log(tf)) * _idf[termId];
            queryWeights[qw++] = (termId, weight);
            sumSquares += weight * weight;
        }

        var norm = Math.Sqrt(sumSquares);

        // Pooled per-call buffer, not a field: Search has no shared mutable state so it is safe under concurrent calls.
        var documentCount = DocumentCount;
        var scores = ArrayPool<double>.Shared.Rent(documentCount);
        try
        {
            Array.Clear(scores, 0, documentCount);
            return Rank(scores, documentCount, queryWeights, norm, top, excludeId);
        }
        finally
        {
            ArrayPool<double>.Shared.Return(scores);
        }
    }

    private List<SimilarityHit> Rank(double[] scores, int documentCount, (int TermId, double Weight)[] queryWeights, double norm, int top, int? excludeId)
    {
        foreach (var (termId, weight) in queryWeights)
        {
            var normalizedWeight = weight / norm;
            foreach (var posting in _postings[termId])
            {
                scores[posting.Doc] += normalizedWeight * posting.Weight;
            }
        }

        List<SimilarityHit> hits = [];
        for (var docIndex = 0; docIndex < documentCount; docIndex++)
        {
            var score = scores[docIndex];
            if (score <= 0)
            {
                continue;
            }

            var id = _ids[docIndex];
            if (excludeId is { } excluded && id == excluded)
            {
                continue;
            }

            // Float drift can push the dot product of two identical normalised vectors slightly above 1.0.
            hits.Add(new SimilarityHit(id, Math.Min(score, 1.0)));
        }

        return [.. hits.OrderByDescending(h => h.Score).ThenBy(h => h.Id).Take(top)];
    }

    private readonly record struct Posting(int Doc, double Weight);
}
