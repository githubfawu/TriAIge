using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using TicketTriage.Core.Abstractions;
using TicketTriage.Infrastructure.Pipeline;
using TicketTriage.Infrastructure.Retrieval;
using TicketTriage.Infrastructure.Sources;

namespace TicketTriage.Infrastructure.Tests;

public class RegistrationTests
{
    private static ServiceProvider Build(Dictionary<string, string?>? extra = null)
    {
        Dictionary<string, string?> values = new() { ["ConnectionStrings:triage-db"] = "Data Source=:memory:" };
        foreach (var (k, v) in extra ?? [])
        {
            values[k] = v;
        }

        var config = new ConfigurationBuilder().AddInMemoryCollection(values).Build();
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddTriageInfrastructure(config);
        return services.BuildServiceProvider();
    }

    [Fact]
    public void Pipeline_ResolvesAsTriagePipeline_WithDbSimilarSource_PerAC8()
    {
        using var provider = Build();
        using var scope = provider.CreateScope();

        scope.ServiceProvider.GetRequiredService<ITriagePipeline>().Should().BeOfType<TriagePipeline>();
        scope.ServiceProvider.GetRequiredService<ISimilarTicketSource>().Should().BeOfType<DbSimilarTicketSource>();
    }

    [Fact]
    public void TicketSource_ResolvesAsDbTicketSource_PerAC8()
    {
        using var provider = Build();

        provider.GetRequiredService<ITicketSource>().Should().BeOfType<DbTicketSource>();
    }

    [Fact]
    public void SimilarTicketIndexProvider_IsSingletonAcrossScopes()
    {
        using var provider = Build();
        using var scope1 = provider.CreateScope();
        using var scope2 = provider.CreateScope();

        scope1.ServiceProvider.GetRequiredService<SimilarTicketIndexProvider>()
            .Should().BeSameAs(scope2.ServiceProvider.GetRequiredService<SimilarTicketIndexProvider>());
    }

    [Fact]
    public void TriageOptions_BindFromConfiguration()
    {
        using var provider = Build(new()
        {
            ["Triage:RetryCount"] = "5",
            ["Triage:StopSystemOnFailure"] = "true",
            ["Triage:TicketTimeoutSeconds"] = "30",
            ["Triage:SimilarTicketCount"] = "4",
            ["Triage:RetryDelayMilliseconds"] = "0",
        });

        var options = provider.GetRequiredService<IOptions<TriageOptions>>().Value;

        options.Should().BeEquivalentTo(new TriageOptions
        {
            RetryCount = 5,
            StopSystemOnFailure = true,
            TicketTimeoutSeconds = 30,
            SimilarTicketCount = 4,
            RetryDelayMilliseconds = 0,
        });
    }

    [Fact]
    public void TriageOptions_InvalidValue_FailsValidation()
    {
        using var provider = Build(new() { ["Triage:SimilarTicketCount"] = "0" });

        var act = () => provider.GetRequiredService<IOptions<TriageOptions>>().Value;

        act.Should().Throw<OptionsValidationException>();
    }
}
