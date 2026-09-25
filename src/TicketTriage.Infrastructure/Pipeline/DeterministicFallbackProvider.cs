using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using TicketTriage.Core.Abstractions;
using TicketTriage.Core.Domain;
using TicketTriage.Infrastructure.Routing;

namespace TicketTriage.Infrastructure.Pipeline;

/// <summary>Same fallback the pipeline uses after exhausted retries, computed without any LLM call.</summary>
internal sealed class DeterministicFallbackProvider(
    TicketNormalizer normalizer,
    ISimilarTicketSource similarSource,
    IRoutingStatisticsSource statisticsSource,
    IAssigneeWorkload workload,
    IOptions<TriageOptions> options,
    ILogger<DeterministicFallbackProvider> logger) : IFallbackSuggestionProvider
{
    public async Task<TriageSuggestion> CreateAsync(Ticket ticket, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(ticket);
        var normalized = normalizer.Normalize(ticket).Ticket;

        IReadOnlyList<SimilarTicket> similar;
        try
        {
            similar = await similarSource.FindSimilarAsync(normalized, options.Value.SimilarTicketCount, cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning("Similar tickets unavailable for {TicketKey}: {ExceptionType}", ticket.Key, ex.GetType().FullName);
            similar = [];
        }

        RoutingStatistics statistics;
        try
        {
            statistics = await statisticsSource.GetAsync(cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning("Routing statistics unavailable for {TicketKey}: {ExceptionType}", ticket.Key, ex.GetType().FullName);
            statistics = RoutingStatistics.Empty;
        }

        string? assignee = null;
        try
        {
            assignee = await workload.PeekLeastLoadedAsync(cancellationToken);
            await workload.ReserveAsync(assignee, cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning("Assignee workload unavailable for {TicketKey}: {ExceptionType}", ticket.Key, ex.GetType().FullName);
        }

        return FallbackSuggestionFactory.Create(normalized, similar, statistics, assignee, logger);
    }
}
