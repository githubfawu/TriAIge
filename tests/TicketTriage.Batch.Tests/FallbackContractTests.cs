using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using TicketTriage.Core.Abstractions;
using TicketTriage.Core.Domain;
using TicketTriage.Infrastructure;
using TicketTriage.Infrastructure.Persistence;

namespace TicketTriage.Batch.Tests;

/// <summary>Guards the assumption behind <see cref="BatchRunner.IsFallback"/> against the real pipeline fallback.</summary>
public sealed class FallbackContractTests : IDisposable
{
    private readonly TempDir _dir = new();

    public void Dispose()
    {
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        _dir.Dispose();
    }

    [Fact]
    public async Task IsFallback_RealPipelineFallbackSuggestion_IsTrue()
    {
        var ct = TestContext.Current.CancellationToken;
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["ConnectionStrings:triage-db"] = $"Data Source={_dir.Combine("t.db")}",
            ["Triage:RetryCount"] = "1",
            ["Triage:RetryDelayMilliseconds"] = "0",
        }).Build();
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddTriageInfrastructure(config);
        services.AddScoped<ITicketClassifier, ThrowingClassifier>();
        await using var provider = services.BuildServiceProvider();
        await using (var db = await provider.GetRequiredService<IDbContextFactory<TriageDbContext>>().CreateDbContextAsync(ct))
        {
            await db.Database.EnsureCreatedAsync(ct);
        }

        await using var scope = provider.CreateAsyncScope();
        var ticket = JsonSerializer.Deserialize<Ticket>(
            """{"Issue key":"X-1","Summary":"VPN drops","Description":"Cannot connect"}""",
            new JsonSerializerOptions(JsonSerializerDefaults.Web))!;

        var suggestions = await scope.ServiceProvider.GetRequiredService<ITriagePipeline>()
            .TriageAsync(ToAsync(ticket), ct).ToListAsync(ct);

        suggestions.Should().ContainSingle();
        BatchRunner.IsFallback(suggestions[0]).Should().BeTrue();
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void IsFallback_BlankDraft_IsTrueAndResultHasNoComment(string? draft)
    {
        var suggestion = new TriageSuggestion
        {
            TicketKey = "A-1",
            WorkType = WorkType.Incident,
            AffectedServices = [],
            ServiceTeams = [],
            Assignee = "x",
            Urgency = Urgency.Low,
            Impact = Impact.Minor,
            DraftComment = draft,
        };

        BatchRunner.IsFallback(suggestion).Should().BeTrue();
        TriageResult.From(suggestion).Comments.Should().BeEmpty();
    }

    private static async IAsyncEnumerable<Ticket> ToAsync(Ticket ticket)
    {
        yield return ticket;
        await Task.CompletedTask;
    }

    private sealed class ThrowingClassifier : ITicketClassifier
    {
        public Task<TicketClassification> ClassifyAsync(
            Ticket ticket, IReadOnlyList<SimilarTicket> similar, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("boom");
    }
}
