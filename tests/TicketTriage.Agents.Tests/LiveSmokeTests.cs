using System.Diagnostics;
using Microsoft.Extensions.Logging.Abstractions;
using TicketTriage.Agents.Classification;
using TicketTriage.Agents.Drafting;
using TicketTriage.Agents.Llm;
using TicketTriage.Core.Domain;

namespace TicketTriage.Agents.Tests;

/// <summary>
/// Opt-in live runs against a real provider (AC8, NFR-06). Set <c>Llm__Apertus__ApiKey</c> or <c>Llm__OpenAI__ApiKey</c>
/// in the environment; without a key the tests are skipped.
/// </summary>
[Trait("Category", "Integration")]
public class LiveSmokeTests
{
    [Theory]
    [InlineData(LlmProvider.Apertus, "Llm__Apertus__ApiKey")]
    [InlineData(LlmProvider.OpenAI, "Llm__OpenAI__ApiKey")]
    public async Task One_ticket_is_classified_and_drafted_in_under_15_seconds(LlmProvider provider, string keyVariable)
    {
        var key = Environment.GetEnvironmentVariable(keyVariable);
        Assert.SkipWhen(string.IsNullOrWhiteSpace(key), $"{keyVariable} is not set.");

        var options = new LlmOptions { Provider = provider };
        options.Apertus.ApiKey = key;
        options.OpenAI.ApiKey = key;
        using var chatClient = ChatClientFactory.Create(options);
        var classifier = new LlmTicketClassifier(chatClient, new TestCatalog(), NullLogger<LlmTicketClassifier>.Instance);
        var drafter = new LlmResolutionDrafter(chatClient, NullLogger<LlmResolutionDrafter>.Instance);
        var ticket = Samples.Ticket("Trading screen login fails", "Since 08:00 nobody on the desk can log in to the trading platform.");

        var watch = Stopwatch.StartNew();
        var classification = await classifier.ClassifyAsync(ticket, [], TestContext.Current.CancellationToken);
        var draft = await drafter.DraftAsync(ticket, classification, new RoutingDecision([], null), [], TestContext.Current.CancellationToken);
        watch.Stop();

        classification.AffectedServices.Should().NotBeEmpty();
        draft.Comment.Should().NotBeNullOrWhiteSpace();
        watch.Elapsed.Should().BeLessThan(TimeSpan.FromSeconds(15));
    }
}
