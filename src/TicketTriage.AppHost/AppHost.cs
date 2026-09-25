var builder = DistributedApplication.CreateBuilder(args);

// data/ at the repository root holds the SQLite file plus the (gitignored) training / challenge JSON.
var dataDirectory = Path.GetFullPath(Path.Combine(builder.AppHostDirectory, "..", "..", "data"));

// File names inside data/ come from Data:TrainingFile / Data:ChallengeFile (appsettings.json), so a new challenge export needs only a config change.
var trainingFile = RequirePlainFileName("Data:TrainingFile", builder.Configuration["Data:TrainingFile"] ?? "jira_first_20000_requested_fields_synthetic.json");
var challengeFile = RequirePlainFileName("Data:ChallengeFile", builder.Configuration["Data:ChallengeFile"] ?? "jira_hackathon_blind_eval_challenge_20260923083915-1141.json");

var sqlite = builder.AddSqlite("triage-db", dataDirectory, "triage.db");

// LLM configuration. Values come from AppHost user-secrets ("Parameters:<name>"), never from code or appsettings.
// Missing values resolve to "" so the app still starts; the agent health check then reports Unhealthy.
var llm = new LlmParameters(
    Provider: AddOptionalParameter("llm-provider", fallback: "AzureOpenAI"),
    AzureOpenAIEndpoint: AddOptionalParameter("azure-openai-endpoint"),
    AzureOpenAIDeployment: AddOptionalParameter("azure-openai-deployment"),
    AzureOpenAIApiKey: AddOptionalParameter("azure-openai-apikey", secret: true),
    OpenAIApiKey: AddOptionalParameter("openai-apikey", secret: true),
    OpenAIModel: AddOptionalParameter("openai-model", fallback: "gpt-4o-mini"),
    ApertusEndpoint: AddOptionalParameter("apertus-endpoint", fallback: "https://api.swisscom.com/products/swiss-ai-weeks/apertus-1.5-70b/v1"),
    ApertusApiKey: AddOptionalParameter("apertus-apikey", secret: true),
    ApertusModel: AddOptionalParameter("apertus-model", fallback: "swiss-ai/Apertus-v1.5-70B"),
    OllamaEndpoint: AddOptionalParameter("ollama-endpoint", fallback: "http://localhost:11434"),
    OllamaModel: AddOptionalParameter("ollama-model", fallback: "qwen2.5:1.5b"));

var web = builder.AddProject<Projects.TicketTriage_Web>("web")
    .WithReference(sqlite)
    .WaitFor(sqlite)
    .WithEnvironment("TrainingData__Path", Path.Combine(dataDirectory, trainingFile))
    .WithLlmConfiguration(llm)
    .WithExternalHttpEndpoints()
    .WithHttpHealthCheck("/health");

// Started manually from the dashboard (explicit start; WaitFor(web) then waits for Web to be healthy, i.e. the analysis worker
// is up); ingests the challenge tickets and exports data/result.json from the worker's suggestions. No LLM configuration needed.
builder.AddProject<Projects.TicketTriage_Batch>("batch")
    .WithReference(sqlite)
    .WaitFor(sqlite)
    .WaitFor(web)
    .WithArgs(
        "--input", Path.Combine(dataDirectory, challengeFile),
        "--output", Path.Combine(dataDirectory, "result.json"))
    .WithExplicitStart();

builder.Build().Run();

// Names are joined onto data/; anything with a directory part could point outside it.
static string RequirePlainFileName(string key, string value) =>
    !string.IsNullOrWhiteSpace(value) && Path.GetFileName(value) == value
        ? value
        : throw new InvalidOperationException($"Configuration value {key} must be a plain file name inside the data directory (no path separators).");

IResourceBuilder<ParameterResource> AddOptionalParameter(string name, string fallback = "", bool secret = false) =>
    builder.AddParameter(name, () => builder.Configuration[$"Parameters:{name}"] ?? fallback, secret: secret);

internal sealed record LlmParameters(
    IResourceBuilder<ParameterResource> Provider,
    IResourceBuilder<ParameterResource> AzureOpenAIEndpoint,
    IResourceBuilder<ParameterResource> AzureOpenAIDeployment,
    IResourceBuilder<ParameterResource> AzureOpenAIApiKey,
    IResourceBuilder<ParameterResource> OpenAIApiKey,
    IResourceBuilder<ParameterResource> OpenAIModel,
    IResourceBuilder<ParameterResource> ApertusEndpoint,
    IResourceBuilder<ParameterResource> ApertusApiKey,
    IResourceBuilder<ParameterResource> ApertusModel,
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
            .WithEnvironment("Llm__OpenAI__ApiKey", llm.OpenAIApiKey)
            .WithEnvironment("Llm__OpenAI__Model", llm.OpenAIModel)
            .WithEnvironment("Llm__Apertus__Endpoint", llm.ApertusEndpoint)
            .WithEnvironment("Llm__Apertus__ApiKey", llm.ApertusApiKey)
            .WithEnvironment("Llm__Apertus__Model", llm.ApertusModel)
            .WithEnvironment("Llm__Ollama__Endpoint", llm.OllamaEndpoint)
            .WithEnvironment("Llm__Ollama__Model", llm.OllamaModel);
}
