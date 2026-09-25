using System.Diagnostics;
using Microsoft.Agents.AI;
using Microsoft.Extensions.Logging;

namespace TicketTriage.Agents.Llm;

/// <summary>
/// Duration and token usage of one agent call, to find slow models or oversized prompts. Only sizes and counts are
/// logged, never prompt or response text (personal data).
/// </summary>
internal static partial class LlmCallLog
{
    public static async Task<TResponse> TimeAsync<TResponse>(
        ILogger logger,
        string agentName,
        string ticketKey,
        int promptChars,
        Func<Task<TResponse>> call)
        where TResponse : AgentResponse
    {
        ArgumentNullException.ThrowIfNull(logger);
        ArgumentNullException.ThrowIfNull(call);

        LogStarted(logger, agentName, ticketKey, promptChars);
        var started = Stopwatch.GetTimestamp();
        try
        {
            var response = await call();
            var usage = response.Usage;
            LogCompleted(
                logger,
                agentName,
                ticketKey,
                ElapsedMs(started),
                promptChars,
                usage?.InputTokenCount,
                usage?.CachedInputTokenCount,
                usage?.OutputTokenCount,
                usage?.ReasoningTokenCount);
            return response;
        }
        catch (Exception ex)
        {
            LogFailed(logger, agentName, ticketKey, ElapsedMs(started), ex.GetType().Name);
            throw;
        }
    }

    private static long ElapsedMs(long started) => (long)Stopwatch.GetElapsedTime(started).TotalMilliseconds;

    [LoggerMessage(Level = LogLevel.Debug, Message = "LLM call {Agent} for {TicketKey} started (prompt {PromptChars} chars).")]
    private static partial void LogStarted(ILogger logger, string agent, string ticketKey, int promptChars);

    [LoggerMessage(
        Level = LogLevel.Information,
        Message = "LLM call {Agent} for {TicketKey} took {ElapsedMs} ms (prompt {PromptChars} chars; tokens in {InputTokens}, "
            + "cached {CachedInputTokens}, out {OutputTokens}, reasoning {ReasoningTokens}).")]
    private static partial void LogCompleted(
        ILogger logger,
        string agent,
        string ticketKey,
        long elapsedMs,
        int promptChars,
        long? inputTokens,
        long? cachedInputTokens,
        long? outputTokens,
        long? reasoningTokens);

    [LoggerMessage(Level = LogLevel.Information, Message = "LLM call {Agent} for {TicketKey} failed after {ElapsedMs} ms ({ExceptionType}).")]
    private static partial void LogFailed(ILogger logger, string agent, string ticketKey, long elapsedMs, string exceptionType);
}
