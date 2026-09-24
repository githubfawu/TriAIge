using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using TicketTriage.Core.Abstractions;
using TicketTriage.Infrastructure.Import;
using TicketTriage.Infrastructure.Persistence;
using TicketTriage.Infrastructure.Stubs;

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
        services.Configure<TrainingDataOptions>(configuration.GetSection(TrainingDataOptions.SectionName));
        services.AddScoped<TrainingDataImporter>();

        // TODO: implement - replace the stubs with real implementations (Agents project for LLM-backed ones).
        services.TryAddScoped<ISimilarTicketRetriever, StubSimilarTicketRetriever>();
        services.TryAddScoped<ITicketClassifier, StubTicketClassifier>();
        services.TryAddScoped<IRoutingResolver, StubRoutingResolver>();
        services.TryAddScoped<IResolutionDrafter, StubResolutionDrafter>();
        services.TryAddScoped<ITriagePipeline, StubTriagePipeline>();

        return services;
    }
}
