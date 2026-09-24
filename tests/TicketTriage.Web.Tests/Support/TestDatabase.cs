using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using TicketTriage.Infrastructure.Persistence;

namespace TicketTriage.Web.Tests.Support;

/// <summary>
/// A SQLite in-memory database reachable from multiple connections/contexts at once, via a named shared-cache
/// database kept alive for the test's lifetime by one open connection (sqlite-efcore skill: <c>DataSource=:memory:</c>
/// alone is single-connection only, but the <see cref="TriageWorker"/> runs on a different thread/connection
/// than the test).
/// </summary>
public sealed class TestDatabase : IAsyncDisposable
{
    private readonly SqliteConnection _keepAlive;

    private TestDatabase(string connectionString, SqliteConnection keepAlive)
    {
        ConnectionString = connectionString;
        _keepAlive = keepAlive;
    }

    public string ConnectionString { get; }

    public static async Task<TestDatabase> CreateAsync(CancellationToken cancellationToken)
    {
        var connectionString = $"Data Source=tt-{Guid.NewGuid():N};Mode=Memory;Cache=Shared";
        var keepAlive = new SqliteConnection(connectionString);
        await keepAlive.OpenAsync(cancellationToken);

        var database = new TestDatabase(connectionString, keepAlive);
        await using (var db = database.CreateContext())
        {
            await db.Database.EnsureCreatedAsync(cancellationToken);
        }

        return database;
    }

    public TriageDbContext CreateContext() => new(Options(ConnectionString));

    public IDbContextFactory<TriageDbContext> CreateFactory() => new Factory(ConnectionString);

    public async ValueTask DisposeAsync()
    {
        await _keepAlive.CloseAsync();
        await _keepAlive.DisposeAsync();
    }

    private static DbContextOptions<TriageDbContext> Options(string connectionString) =>
        new DbContextOptionsBuilder<TriageDbContext>().UseSqlite(connectionString).Options;

    private sealed class Factory(string connectionString) : IDbContextFactory<TriageDbContext>
    {
        public TriageDbContext CreateDbContext() => new(Options(connectionString));

        public Task<TriageDbContext> CreateDbContextAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(CreateDbContext());
    }
}
