using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using TicketTriage.Agents.Classification;
using TicketTriage.Agents.Drafting;
using TicketTriage.Core.Abstractions;

namespace TicketTriage.Agents.Tests;

public class RegistrationTests
{
    [Fact]
    public void Llm_implementations_are_resolved_from_the_core_ports()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddTriageAgents(new ConfigurationBuilder().Build());

        using var provider = services.BuildServiceProvider();
        using var scope = provider.CreateScope();

        scope.ServiceProvider.GetRequiredService<ITicketClassifier>().Should().BeOfType<LlmTicketClassifier>();
        scope.ServiceProvider.GetRequiredService<IResolutionDrafter>().Should().BeOfType<LlmResolutionDrafter>();
        scope.ServiceProvider.GetRequiredService<IResolutionDraftAgent>().Should().BeOfType<LlmResolutionDrafter>();
    }
}
