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
internal sealed class LlmResolutionDrafter : IResolutionDrafter, IResolutionDraftAgent
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

    public async Task<string> DraftAsync(
        Ticket ticket,
        TicketClassification classification,
        IReadOnlyList<SimilarTicket> similarTickets,
        CancellationToken cancellationToken) =>
        (await DraftWithStatusAsync(ticket, classification, similarTickets, cancellationToken)).Comment;

    public async Task<ResolutionDraft> DraftWithStatusAsync(
        Ticket ticket,
        TicketClassification classification,
        IReadOnlyList<SimilarTicket> similarTickets,
        CancellationToken cancellationToken)
    {
        _logger.LogDebug("Drafting resolution for ticket {TicketKey} with prompt {PromptVersion}.", ticket.Key, DraftPrompts.Version);

        var assignee = DraftPrompts.InferAssignee(classification, similarTickets);
        var response = await _agent.RunAsync<DraftDto>(
            DraftPrompts.BuildUserMessage(ticket, classification, similarTickets, assignee),
            cancellationToken: cancellationToken);

        return response.Result.ToDomain();
    }

    internal sealed record DraftDto(
        [property: Description("Done, Cancelled, Clarification or Cannot Reproduce")] string? ResolutionStatus,
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
                Comment.Trim(),
                string.IsNullOrWhiteSpace(Language) ? "unknown" : Language.Trim());
        }
    }
}
