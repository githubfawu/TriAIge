using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using TicketTriage.Infrastructure;
using TicketTriage.Infrastructure.Persistence;
using TicketTriage.Web.Tests.Support;
using TicketTriage.Web.Triage;

namespace TicketTriage.Web.Tests;

/// <summary>
/// Real DI (AddTriageInfrastructure + AddTriageUi, ValidateScopes/ValidateOnBuild) with the real
/// <c>StubTriagePipeline</c> - no fakes below the wiring. Exercises the Slice 1 entry point end-to-end
/// (upload -&gt; worker -&gt; board), asserting AC2's shape and timing.
/// </summary>
public sealed class TriageEndToEndTests : IAsyncLifetime
{
    private static readonly string FixturePath = Path.Combine(AppContext.BaseDirectory, "Fixtures", "challenge-sample.json");

    private TestDatabase _database = null!;
    private ServiceProvider _provider = null!;

    public async ValueTask InitializeAsync()
    {
        var cancellationToken = Xunit.TestContext.Current.CancellationToken;
        _database = await TestDatabase.CreateAsync(cancellationToken);

        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["ConnectionStrings:triage-db"] = _database.ConnectionString })
            .Build();

        var services = new ServiceCollection();
        services.AddLogging();
        services.AddTriageInfrastructure(configuration);
        services.AddTriageUi(configuration);

        _provider = services.BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true, ValidateOnBuild = true });

        var dbFactory = _provider.GetRequiredService<IDbContextFactory<TriageDbContext>>();
        await using var db = await dbFactory.CreateDbContextAsync(cancellationToken);
        await db.Database.EnsureCreatedAsync(cancellationToken);
    }

    public async ValueTask DisposeAsync()
    {
        await _provider.DisposeAsync();
        await _database.DisposeAsync();
    }

    [Fact]
    [Trait("Category", "Smoke")]
    public async Task Upload_FixtureFile_AllFiveTicketsBecomePendingWithinTenSeconds_PerAC2()
    {
        var cancellationToken = Xunit.TestContext.Current.CancellationToken;

        await using (var scope = _provider.CreateAsyncScope())
        {
            var ingest = scope.ServiceProvider.GetRequiredService<IUploadIngestService>();
            var bytes = await File.ReadAllBytesAsync(FixturePath, cancellationToken);
            var preview = await ingest.PreviewAsync(bytes, cancellationToken);
            preview.ValidCount.Should().Be(5);

            var result = await ingest.SaveAndEnqueueAsync(preview, confirmedWithoutTrainingData: true, cancellationToken);
            result.Outcome.Should().Be(SaveOutcome.Saved);
            result.SavedCount.Should().Be(5);
        }

        var worker = _provider.GetServices<IHostedService>().OfType<TriageWorker>().Single();
        await worker.StartAsync(cancellationToken);
        try
        {
            await using var scope = _provider.CreateAsyncScope();
            var boardQuery = scope.ServiceProvider.GetRequiredService<ITriageBoardQuery>();

            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(10));

            IReadOnlyList<TicketBoardRow> rows;
            while (true)
            {
                rows = await boardQuery.GetRowsAsync(timeout.Token);
                if (rows.Count == 5 && rows.All(r => r.State == TicketDisplayState.Pending))
                {
                    break;
                }

                timeout.Token.ThrowIfCancellationRequested();
                await Task.Delay(50, timeout.Token);
            }

            rows.Should().HaveCount(5);
            rows.Should().OnlyContain(r => r.State == TicketDisplayState.Pending);
        }
        finally
        {
            await worker.StopAsync(CancellationToken.None);
        }
    }
}
