using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using TicketTriage.Agents.Llm;
using TicketTriage.Agents.Services;
using TicketTriage.Core.Abstractions;
using TicketTriage.Core.Domain;

namespace TicketTriage.Agents.Classification;

/// <summary>
/// LLM-backed classification (work type, services, urgency, impact) in one structured call.
/// Invalid or missing output throws: the pipeline owns retry, Failed state and deterministic fallback.
/// </summary>
internal sealed class LlmTicketClassifier : ITicketClassifier
{
    private readonly ChatClientAgent _agent;
    private readonly IReadOnlyList<ServiceDefinition> _catalog;
    private readonly ILogger<LlmTicketClassifier> _logger;

    public LlmTicketClassifier(
        IChatClient chatClient,
        IServiceCatalogProvider catalogProvider,
        ILogger<LlmTicketClassifier> logger)
    {
        _catalog = catalogProvider.GetServices();
        _logger = logger;
        _agent = new ChatClientAgent(chatClient, new ChatClientAgentOptions
        {
            Name = "Classifier",
            ChatOptions = new ChatOptions
            {
                Instructions = ClassifierPrompts.BuildInstructions(_catalog),
                Temperature = 0f,
            },
        });
    }

    public async Task<TicketClassification> ClassifyAsync(
        Ticket ticket,
        IReadOnlyList<SimilarTicket> similarTickets,
        CancellationToken cancellationToken)
    {
        _logger.LogDebug("Classifying ticket {TicketKey} with prompt {PromptVersion}.", ticket.Key, ClassifierPrompts.Version);

        var message = ClassifierPrompts.BuildUserMessage(ticket, similarTickets);
        var response = await LlmCallLog.TimeAsync(
            _logger,
            "Classifier",
            ticket.Key,
            message.Length,
            () => _agent.RunAsync<ClassificationDto>(message, cancellationToken: cancellationToken));

        return response.Result.ToDomain(_catalog);
    }
}
