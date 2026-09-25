using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using TicketTriage.Core.Abstractions;
using TicketTriage.Core.Domain;
using TicketTriage.Infrastructure;
using TicketTriage.Infrastructure.Persistence;

namespace TicketTriage.Agents.Tests;

/// <summary>AC4: an invalid model status goes through retry and fallback; the fallback status is the similar-ticket majority.</summary>
public sealed class ResolutionStatusFallbackTests : IDisposable
{
    private const string InvalidStatusDraft = """{"resolutionStatus":"fixed","language":"English","comment":"Restarted the service."}""";

    private readonly string _dbFile = Path.Combine(Path.GetTempPath(), "status-fallback-" + Guid.NewGuid().ToString("N") + ".db");

    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        foreach (var file in Directory.GetFiles(Path.GetDirectoryName(_dbFile)!, Path.GetFileName(_dbFile) + "*"))
        {
            File.Delete(file);
        }
    }

    private static SimilarTicket Similar(string status, double score) =>
        new(new Ticket { Key = "DB-" + status, Summary = "s", Resolution = status }, score);

    private async Task<(TriageSuggestion Suggestion, RecordingStore Store)> RunAsync(params SimilarTicket[] similar)
    {
        var ct = TestContext.Current.CancellationToken;
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["ConnectionStrings:triage-db"] = "Data Source=" + _dbFile,
            ["Triage:RetryCount"] = "2",
            ["Triage:RetryDelayMilliseconds"] = "0",
        }).Build();
        var store = new RecordingStore();

        var services = new ServiceCollection();
        services.AddLogging();
        services.AddTriageInfrastructure(config);
        services.AddTriageAgents(config);
        services.AddSingleton<IChatClient>(new FakeChatClient(Samples.ValidClassification, InvalidStatusDraft));
        services.AddSingleton<ISimilarTicketSource>(new FixedSource(similar));
        services.AddSingleton<ITriageFailureStore>(store);

        await using var provider = services.BuildServiceProvider();
        await using (var db = await provider.GetRequiredService<IDbContextFactory<TriageDbContext>>().CreateDbContextAsync(ct))
        {
            await db.Database.EnsureCreatedAsync(ct);
        }

        await using var scope = provider.CreateAsyncScope();
        var suggestion = await scope.ServiceProvider.GetRequiredService<ITriagePipeline>()
            .TriageAsync(Samples.Ticket(), ct);
        return (suggestion, store);
    }

    [Fact]
    public async Task InvalidStatus_ExhaustsRetries_FallsBackToSimilarMajority_PerAC4()
    {
        var (suggestion, store) = await RunAsync(
            Similar("clarification", 0.9), Similar("clarification", 0.5), Similar("done", 0.4));

        suggestion.DraftComment.Should().BeNull("a fallback carries no comment");
        suggestion.ResolutionStatus.Should().Be(ResolutionStatus.Clarification);
        store.Reasons.Should().Equal("Draft:Exception", "Draft:Exception");
    }

    [Fact]
    public async Task InvalidStatus_NoSimilarTickets_FallsBackToDone_PerAC4()
    {
        var (suggestion, _) = await RunAsync();

        suggestion.DraftComment.Should().BeNull();
        suggestion.ResolutionStatus.Should().Be(ResolutionStatus.Done);
    }

    private sealed class FixedSource(SimilarTicket[] similar) : ISimilarTicketSource
    {
        public Task<IReadOnlyList<SimilarTicket>> FindSimilarAsync(Ticket ticket, int top, CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<SimilarTicket>>(similar);
    }

    internal sealed class RecordingStore : ITriageFailureStore
    {
        public List<string> Reasons { get; } = [];

        public Task<int?> RecordFailureAsync(TriageFailure failure, CancellationToken cancellationToken)
        {
            Reasons.Add(failure.Reason);
            return Task.FromResult<int?>(null);
        }

        public Task ResetRetriesAsync(int ticketId, CancellationToken cancellationToken) => Task.CompletedTask;
    }
}
