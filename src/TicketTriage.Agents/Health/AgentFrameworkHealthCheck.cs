using Microsoft.Agents.AI;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using TicketTriage.Agents.Llm;

namespace TicketTriage.Agents.Health;

/// <summary>
/// Sends a minimal prompt to the triage agent. Results are cached so frequent probes don't burn tokens.
/// Must be registered as a singleton so the cache survives between probes.
/// </summary>
public sealed class AgentFrameworkHealthCheck(
    [FromKeyedServices(TriageAgent.Name)] AIAgent agent,
    IOptions<LlmOptions> llmOptions,
    TimeProvider timeProvider,
    ILogger<AgentFrameworkHealthCheck> logger) : IHealthCheck, IDisposable
{
    public const string Name = "agent-framework";

    internal static readonly TimeSpan ProbeTimeout = TimeSpan.FromSeconds(10);
    internal static readonly TimeSpan DegradedThreshold = TimeSpan.FromSeconds(5);
    internal static readonly TimeSpan CacheDuration = TimeSpan.FromSeconds(60);

    private const string ProbePrompt = "Reply with OK";

    private readonly SemaphoreSlim _probeLock = new(1, 1);
    private (HealthCheckResult Result, DateTimeOffset CachedAt)? _cache;

    public async Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default)
    {
        if (TryGetCached(out var cached))
        {
            return cached;
        }

        await _probeLock.WaitAsync(cancellationToken);
        try
        {
            // Another caller may have refreshed the cache while we were waiting.
            if (TryGetCached(out cached))
            {
                return cached;
            }

            var result = await ProbeAsync(context.Registration.FailureStatus, cancellationToken);
            _cache = (result, timeProvider.GetUtcNow());
            return result;
        }
        finally
        {
            _probeLock.Release();
        }
    }

    public void Dispose() => _probeLock.Dispose();

    private bool TryGetCached(out HealthCheckResult result)
    {
        if (_cache is { } entry && timeProvider.GetUtcNow() - entry.CachedAt < CacheDuration)
        {
            result = entry.Result;
            return true;
        }

        result = default;
        return false;
    }

    private async Task<HealthCheckResult> ProbeAsync(HealthStatus failureStatus, CancellationToken cancellationToken)
    {
        var data = new Dictionary<string, object> { ["provider"] = llmOptions.Value.Provider.ToString() };

        using var timeout = new CancellationTokenSource(ProbeTimeout, timeProvider);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeout.Token);
        var started = timeProvider.GetTimestamp();

        try
        {
            var response = await agent.RunAsync(ProbePrompt, cancellationToken: linked.Token);
            var elapsed = timeProvider.GetElapsedTime(started);

            data["elapsedMs"] = (long)elapsed.TotalMilliseconds;
            data["reply"] = Truncate(response.Text, 50);

            return elapsed > DegradedThreshold
                ? HealthCheckResult.Degraded($"Agent responded slowly ({elapsed.TotalSeconds:F1} s).", data: data)
                : HealthCheckResult.Healthy("Agent responded.", data);
        }
        catch (OperationCanceledException) when (timeout.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
        {
            logger.LogWarning("Agent health probe timed out after {Timeout}.", ProbeTimeout);
            return new HealthCheckResult(failureStatus, $"Agent did not respond within {ProbeTimeout.TotalSeconds:F0} s.", data: data);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "Agent health probe failed.");
            return new HealthCheckResult(failureStatus, ex.Message, ex, data);
        }
    }

    private static string Truncate(string value, int maxLength) =>
        value.Length <= maxLength ? value : value[..maxLength] + "…";
}
