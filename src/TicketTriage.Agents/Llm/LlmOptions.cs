namespace TicketTriage.Agents.Llm;

public enum LlmProvider
{
    AzureOpenAI,
    Ollama,
}

/// <summary>Bound from the <c>Llm</c> configuration section.</summary>
public sealed class LlmOptions
{
    public const string SectionName = "Llm";

    public LlmProvider Provider { get; set; } = LlmProvider.AzureOpenAI;

    public AzureOpenAIOptions AzureOpenAI { get; set; } = new();

    public OllamaOptions Ollama { get; set; } = new();
}

public sealed class AzureOpenAIOptions
{
    /// <summary>Resource endpoint, e.g. <c>https://my-resource.openai.azure.com/</c>.</summary>
    public string? Endpoint { get; set; }

    /// <summary>Model deployment name.</summary>
    public string? Deployment { get; set; }

    /// <summary>API key. Never commit it; supply it via AppHost user-secrets (or Web user-secrets when running standalone).</summary>
    public string? ApiKey { get; set; }
}

public sealed class OllamaOptions
{
    public string Endpoint { get; set; } = "http://localhost:11434";

    public string Model { get; set; } = "qwen2.5:1.5b";
}
