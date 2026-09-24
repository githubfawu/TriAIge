using System.ClientModel;
using Microsoft.Extensions.AI;
using OllamaSharp;
using OpenAI;
using OpenAI.Chat;

namespace TicketTriage.Agents.Llm;

/// <summary>Creates the provider-specific <see cref="IChatClient"/> selected by <see cref="LlmOptions.Provider"/>.</summary>
internal static class ChatClientFactory
{
    public static IChatClient Create(LlmOptions options) => options.Provider switch
    {
        LlmProvider.AzureOpenAI => CreateAzureOpenAI(options.AzureOpenAI),
        LlmProvider.Ollama => new OllamaApiClient(new Uri(options.Ollama.Endpoint), options.Ollama.Model),
        _ => new UnconfiguredChatClient($"Unknown LLM provider '{options.Provider}'."),
    };

    private static IChatClient CreateAzureOpenAI(AzureOpenAIOptions azure)
    {
        var missing = new (string Key, string? Value)[]
            {
                ("Llm:AzureOpenAI:Endpoint", azure.Endpoint),
                ("Llm:AzureOpenAI:Deployment", azure.Deployment),
                ("Llm:AzureOpenAI:ApiKey", azure.ApiKey),
            }
            .Where(setting => string.IsNullOrWhiteSpace(setting.Value))
            .Select(setting => setting.Key)
            .ToList();

        if (missing.Count > 0)
        {
            // Do not fail startup: the app must run without LLM config; the agent health check reports it instead.
            return new UnconfiguredChatClient(
                $"Azure OpenAI is not configured (missing {string.Join(", ", missing)}).");
        }

        // Azure OpenAI "v1" API: the plain OpenAI SDK talks to <endpoint>/openai/v1/ (no api-version needed).
        var baseUri = azure.Endpoint!.TrimEnd('/');
        if (!baseUri.EndsWith("/openai/v1", StringComparison.OrdinalIgnoreCase))
        {
            baseUri += "/openai/v1";
        }

        var chatClient = new ChatClient(
            azure.Deployment,
            new ApiKeyCredential(azure.ApiKey!),
            new OpenAIClientOptions { Endpoint = new Uri(baseUri + "/") });

        return chatClient.AsIChatClient();
    }
}
