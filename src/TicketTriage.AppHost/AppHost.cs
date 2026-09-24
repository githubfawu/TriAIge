var builder = DistributedApplication.CreateBuilder(args);

// data/ at the repository root holds the SQLite file plus the (gitignored) training / challenge JSON.
var dataDirectory = Path.GetFullPath(Path.Combine(builder.AppHostDirectory, "..", "..", "data"));

var sqlite = builder.AddSqlite("triage-db", dataDirectory, "triage.db");

// LLM configuration. Values come from AppHost user-secrets ("Parameters:<name>"), never from code or appsettings.
// Missing values resolve to "" so the app still starts; the agent health check then reports Unhealthy.
var llm = new LlmParameters(
    Provider: AddOptionalParameter("llm-provider", fallback: "AzureOpenAI"),
    AzureOpenAIEndpoint: AddOptionalParameter("azure-openai-endpoint"),
    AzureOpenAIDeployment: AddOptionalParameter("azure-openai-deployment"),
    AzureOpenAIApiKey: AddOptionalParameter("azure-openai-apikey", secret: true),
    OllamaEndpoint: AddOptionalParameter("ollama-endpoint", fallback: "http://localhost:11434"),
    OllamaModel: AddOptionalParameter("ollama-model", fallback: "qwen2.5:1.5b"));

builder.AddProject<Projects.TicketTriage_Web>("web")
    .WithReference(sqlite)
    .WaitFor(sqlite)
    .WithEnvironment("TrainingData__Path", Path.Combine(dataDirectory, "training.json"))
    .WithLlmConfiguration(llm)
    .WithExternalHttpEndpoints()
    .WithHttpHealthCheck("/health");

// Started manually from the dashboard; writes data/result.json.
builder.AddProject<Projects.TicketTriage_Batch>("batch")
    .WithReference(sqlite)
    .WaitFor(sqlite)
    .WithArgs(
        "--input", Path.Combine(dataDirectory, "challenge.json"),
        "--output", Path.Combine(dataDirectory, "result.json"))
    .WithEnvironment("TrainingData__Path", Path.Combine(dataDirectory, "training.json"))
    .WithLlmConfiguration(llm)
    .WithExplicitStart();

builder.Build().Run();

IResourceBuilder<ParameterResource> AddOptionalParameter(string name, string fallback = "", bool secret = false) =>
    builder.AddParameter(name, () => builder.Configuration[$"Parameters:{name}"] ?? fallback, secret: secret);

internal sealed record LlmParameters(
    IResourceBuilder<ParameterResource> Provider,
    IResourceBuilder<ParameterResource> AzureOpenAIEndpoint,
    IResourceBuilder<ParameterResource> AzureOpenAIDeployment,
    IResourceBuilder<ParameterResource> AzureOpenAIApiKey,
    IResourceBuilder<ParameterResource> OllamaEndpoint,
    IResourceBuilder<ParameterResource> OllamaModel);

internal static class LlmResourceBuilderExtensions
{
    /// <summary>Maps the AppHost LLM parameters onto the <c>Llm</c> configuration section of a project.</summary>
    public static IResourceBuilder<ProjectResource> WithLlmConfiguration(
        this IResourceBuilder<ProjectResource> project,
        LlmParameters llm) =>
        project
            .WithEnvironment("Llm__Provider", llm.Provider)
            .WithEnvironment("Llm__AzureOpenAI__Endpoint", llm.AzureOpenAIEndpoint)
            .WithEnvironment("Llm__AzureOpenAI__Deployment", llm.AzureOpenAIDeployment)
            .WithEnvironment("Llm__AzureOpenAI__ApiKey", llm.AzureOpenAIApiKey)
            .WithEnvironment("Llm__Ollama__Endpoint", llm.OllamaEndpoint)
            .WithEnvironment("Llm__Ollama__Model", llm.OllamaModel);
}
