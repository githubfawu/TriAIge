using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace TicketTriage.Web.Triage;

public static class TriageUiServiceCollectionExtensions
{
    /// <summary>Registers the Web-local triage application layer (upload, RAM queue/store, worker, board query)
    /// on top of <c>AddTriageInfrastructure</c>/<c>AddTriageAgents</c>.</summary>
    public static IServiceCollection AddTriageUi(this IServiceCollection services, IConfiguration configuration)
    {
        services.AddSingleton<LookupCatalog>();
        services.AddSingleton<TriageSessionStore>();

        services.AddScoped<ISuggestionWriter, SuggestionWriter>();
        services.AddScoped<IUploadIngestService, UploadIngestService>();
        services.AddScoped<ITriageBoardQuery, TriageBoardQuery>();

        services.Configure<TriageWorkerOptions>(configuration.GetSection(TriageWorkerOptions.SectionName));
        services.AddHostedService<TriageWorker>();

        return services;
    }
}
