using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;
using TicketTriage.Core.Abstractions;
using TicketTriage.Infrastructure.Analysis;
using TicketTriage.Infrastructure.Import;
using TicketTriage.Infrastructure.Ingestion;
using TicketTriage.Infrastructure.Persistence;
using TicketTriage.Infrastructure.Pipeline;
using TicketTriage.Infrastructure.Retrieval;
using TicketTriage.Infrastructure.Routing;
using TicketTriage.Infrastructure.Sources;

namespace TicketTriage.Infrastructure;

public static class InfrastructureServiceCollectionExtensions
{
    /// <summary>Name of the connection string; matches the SQLite resource name in the AppHost.</summary>
    public const string ConnectionStringName = "triage-db";

    public static IServiceCollection AddTriageInfrastructure(this IServiceCollection services, IConfiguration configuration)
    {
        var connectionString = configuration.GetConnectionString(ConnectionStringName)
            ?? throw new InvalidOperationException($"Connection string '{ConnectionStringName}' is not configured.");

        // The factory suits Blazor Server (short-lived contexts per operation); it also registers a scoped TriageDbContext.
        services.AddDbContextFactory<TriageDbContext>(options => options.UseSqlite(connectionString));

        services.TryAddSingleton(TimeProvider.System);
        services.TryAddSingleton<ITriageFailureStore, EfTriageFailureStore>();
        services.Configure<TrainingDataOptions>(configuration.GetSection(TrainingDataOptions.SectionName));
        services.AddScoped<TrainingDataImporter>();
        services.TryAddScoped<ITicketIngestor, EfTicketIngestor>();
        services.TryAddScoped<IReviewService, TicketTriage.Infrastructure.Review.EfReviewService>();
        services.TryAddScoped<ITriageMetricsService, TicketTriage.Infrastructure.Review.EfTriageMetricsService>();

        services.TryAddSingleton<LookupNamesProvider>();
        services.TryAddSingleton<SimilarTicketIndexProvider>();
        services.TryAddSingleton<ISimilarTicketSource, DbSimilarTicketSource>();
        services.TryAddSingleton<ITicketSource, DbTicketSource>();
        services.TryAddSingleton<IRoutingStatisticsSource, RoutingStatisticsProvider>();
        services.TryAddScoped<IRoutingResolver, StatisticsRoutingResolver>();

        services.AddOptions<TriageOptions>()
            .Bind(configuration.GetSection(TriageOptions.SectionName))
            .Validate(TriageOptions.IsValid, "Triage settings are out of range.")
            .ValidateOnStart();
        services.AddOptions<AnalysisOptions>()
            .Bind(configuration.GetSection(AnalysisOptions.SectionName))
            .ValidateOnStart();
        services.TryAddEnumerable(ServiceDescriptor.Singleton<IValidateOptions<AnalysisOptions>, AnalysisOptionsValidator>());
        services.TryAddScoped<TicketClaimStore>();
        services.TryAddSingleton<IAnalysisMonitor, EfAnalysisMonitor>();
        services.TryAddSingleton<IFallbackSuggestionProvider, DeterministicFallbackProvider>();
        services.TryAddSingleton<IAnalysisCycle, AnalysisCycle>();
        services.TryAddSingleton<TicketNormalizer>();
        services.AddScoped<ITriagePipeline, TriagePipeline>();

        return services;
    }
}
