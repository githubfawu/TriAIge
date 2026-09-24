using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using TicketTriage.Agents.Health;
using TicketTriage.Agents.Llm;

namespace TicketTriage.Agents;

public static class AgentsServiceCollectionExtensions
{
    /// <summary>
    /// Registers the <see cref="IChatClient"/> for the configured <c>Llm:Provider</c> and the <see cref="TriageAgent"/>.
    /// Missing LLM configuration does not fail startup; it is reported by <see cref="AgentFrameworkHealthCheck"/>.
    /// </summary>
    public static IServiceCollection AddTriageAgents(this IServiceCollection services, IConfiguration configuration)
    {
        services.Configure<LlmOptions>(configuration.GetSection(LlmOptions.SectionName));
        services.TryAddSingleton(TimeProvider.System);

        services
            .AddChatClient(sp => ChatClientFactory.Create(sp.GetRequiredService<IOptions<LlmOptions>>().Value))
            .UseOpenTelemetry()
            .UseLogging();

        services.AddKeyedSingleton<AIAgent>(TriageAgent.Name, (sp, _) =>
            sp.GetRequiredService<IChatClient>().AsAIAgent(
                instructions: TriageAgent.Instructions,
                name: TriageAgent.Name,
                loggerFactory: sp.GetService<ILoggerFactory>(),
                services: sp));

        // Singleton so the probe cache is shared across health-check runs.
        services.AddSingleton<AgentFrameworkHealthCheck>();

        return services;
    }

    /// <summary>Adds the agent readiness check (tag <c>ready</c>, not <c>live</c>).</summary>
    public static IHealthChecksBuilder AddAgentFrameworkCheck(this IHealthChecksBuilder builder) =>
        builder.AddCheck<AgentFrameworkHealthCheck>(
            AgentFrameworkHealthCheck.Name,
            failureStatus: HealthStatus.Unhealthy,
            tags: ["ready"]);
}
