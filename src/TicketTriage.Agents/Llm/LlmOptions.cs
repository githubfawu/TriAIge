namespace TicketTriage.Agents.Llm;

public enum LlmProvider
{
    AzureOpenAI,
    OpenAI,
    Apertus,
    Ollama,
}

/// <summary>Bound from the <c>Llm</c> configuration section.</summary>
public sealed class LlmOptions
{
    public const string SectionName = "Llm";

    public LlmProvider Provider { get; set; } = LlmProvider.AzureOpenAI;

    public AzureOpenAIOptions AzureOpenAI { get; set; } = new();

    public OpenAIOptions OpenAI { get; set; } = new();

    public ApertusOptions Apertus { get; set; } = new();

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

/// <summary>Plain OpenAI (api.openai.com), as opposed to <see cref="AzureOpenAIOptions"/>.</summary>
public sealed class OpenAIOptions
{
    /// <summary>API key. Never commit it; supply it via AppHost user-secrets (or Web user-secrets when running standalone).</summary>
    public string? ApiKey { get; set; }

    /// <summary>Model name, e.g. <c>gpt-4o-mini</c>.</summary>
    public string Model { get; set; } = "gpt-4o-mini";
}

/// <summary>
/// Swisscom-hosted Apertus (Swiss AI Weeks), an OpenAI-compatible endpoint.
/// See https://zh.ai-weeks.ch/tools/swisscom-hacker-guide — key from "The Keymaker", expires after 60 minutes.
/// </summary>
public sealed class ApertusOptions
{
    public string Endpoint { get; set; } = "https://api.swisscom.com/products/swiss-ai-weeks/apertus-1.5-70b/v1";

    /// <summary>Bearer token from The Keymaker. Never commit it; supply it via AppHost user-secrets. Short-lived (60 min) — expect to rotate it.</summary>
    public string? ApiKey { get; set; }

    public string Model { get; set; } = "swiss-ai/Apertus-v1.5-70B";
}

public sealed class OllamaOptions
{
    public string Endpoint { get; set; } = "http://localhost:11434";

    public string Model { get; set; } = "qwen2.5:1.5b";
}
