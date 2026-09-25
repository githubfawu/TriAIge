using System.ComponentModel;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using TicketTriage.Agents.Prompting;
using TicketTriage.Core.Abstractions;
using TicketTriage.Core.Domain;

namespace TicketTriage.Agents.Drafting;

/// <summary>
/// LLM-backed resolution draft. Invalid or missing output throws: the pipeline owns retry, Failed state and fallback.
/// </summary>
internal sealed class LlmResolutionDrafter : IResolutionDrafter
{
    private readonly ChatClientAgent _agent;
    private readonly ILogger<LlmResolutionDrafter> _logger;

    public LlmResolutionDrafter(IChatClient chatClient, ILogger<LlmResolutionDrafter> logger)
    {
        _logger = logger;
        _agent = new ChatClientAgent(chatClient, new ChatClientAgentOptions
        {
            Name = "Drafter",
            ChatOptions = new ChatOptions
            {
                Instructions = DraftPrompts.BuildInstructions(),
                Temperature = 0f,
            },
        });
    }

    public async Task<ResolutionDraft> DraftAsync(
        Ticket ticket,
        TicketClassification classification,
        RoutingDecision routing,
        IReadOnlyList<SimilarTicket> similarTickets,
        CancellationToken cancellationToken)
    {
        _logger.LogDebug("Drafting resolution for ticket {TicketKey} with prompt {PromptVersion}.", ticket.Key, DraftPrompts.Version);

        var response = await _agent.RunAsync<DraftDto>(
            DraftPrompts.BuildUserMessage(ticket, classification, routing, similarTickets),
            cancellationToken: cancellationToken);

        _logger.LogDebug("Drafted comment language: {Language}.", response.Result.Language);
        return response.Result.ToDomain();
    }

    internal sealed record DraftDto(
        [property: Description("Exactly one of: done, cancelled, clarification, cannot reproduce")] string? ResolutionStatus,
        [property: Description("Language of the ticket text, e.g. English or German")] string? Language,
        [property: Description("The resolution comment, written in that language")] string? Comment)
    {
        public ResolutionDraft ToDomain()
        {
            if (string.IsNullOrWhiteSpace(Comment))
            {
                throw new InvalidOperationException("The model returned an empty resolution comment.");
            }

            return new ResolutionDraft(
                EnumNames.Parse<ResolutionStatus>(ResolutionStatus, "resolution status"),
                Comment.Trim());
        }
    }
}
