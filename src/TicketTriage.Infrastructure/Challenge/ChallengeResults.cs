using TicketTriage.Core.Abstractions;
using TicketTriage.Core.Domain;

namespace TicketTriage.Infrastructure.Challenge;

/// <summary>One output row; <see cref="WasAnalysed"/> is false when the worker stored no suggestion and the fallback was used.</summary>
public sealed record ChallengeResultRow(int Position, Ticket Ticket, TriageResult Result, bool IsFallback, bool WasAnalysed);

public sealed record ChallengeResultSet(IReadOnlyList<ChallengeResultRow> Rows, int Fallbacks, int NotAnalysed);

public static class ChallengeResults
{
    /// <summary>
    /// One row per entry of <paramref name="ids"/> (duplicates allowed, input order). A ticket without a stored suggestion gets
    /// the deterministic fallback, built once per id. A blank draft comment counts as fallback.
    /// </summary>
    public static async Task<ChallengeResultSet> BuildAsync(
        IReadOnlyList<int> ids,
        IReadOnlyList<AnalysisState> states,
        IFallbackSuggestionProvider fallbackProvider,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(ids);
        ArgumentNullException.ThrowIfNull(states);
        ArgumentNullException.ThrowIfNull(fallbackProvider);

        var byId = states.ToDictionary(s => s.TicketId);
        var fallbackCache = new Dictionary<int, TriageSuggestion>();
        var rows = new List<ChallengeResultRow>(ids.Count);
        var fallbacks = 0;
        var notAnalysed = 0;
        for (var position = 0; position < ids.Count; position++)
        {
            var id = ids[position];
            if (!byId.TryGetValue(id, out var state))
            {
                throw new InvalidOperationException($"No analysis state was returned for the ticket at position {position}.");
            }

            var suggestion = state.Suggestion;
            var analysed = suggestion is not null;
            if (suggestion is null)
            {
                if (!fallbackCache.TryGetValue(id, out suggestion))
                {
                    suggestion = await fallbackProvider.CreateAsync(state.Ticket, cancellationToken);
                    fallbackCache[id] = suggestion;
                }

                notAnalysed++;
            }

            var isFallback = suggestion.IsFallback;
            if (isFallback)
            {
                fallbacks++;
            }

            rows.Add(new ChallengeResultRow(position, state.Ticket, TriageResult.From(suggestion), isFallback, analysed));
        }

        return new ChallengeResultSet(rows, fallbacks, notAnalysed);
    }
}
