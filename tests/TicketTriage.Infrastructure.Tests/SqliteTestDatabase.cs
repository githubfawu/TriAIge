using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using TicketTriage.Core.Domain;
using TicketTriage.Infrastructure.Persistence;

namespace TicketTriage.Infrastructure.Tests;

/// <summary>SQLite in-memory database with the real schema and seeded lookups; lives as long as its open connection.</summary>
internal sealed class SqliteTestDatabase : IAsyncDisposable
{
    private readonly SqliteConnection _connection = new("DataSource=:memory:");
    private ServiceProvider? _provider;

    public IDbContextFactory<TriageDbContext> Factory =>
        _provider!.GetRequiredService<IDbContextFactory<TriageDbContext>>();

    public SqliteConnection Connection => _connection;

    public TicketOrigin DefaultOrigin { get; private init; } = TicketOrigin.Training;

    public static Task<SqliteTestDatabase> CreateAsync(CancellationToken cancellationToken) =>
        CreateAsync(TicketOrigin.Training, cancellationToken);

    public static async Task<SqliteTestDatabase> CreateAsync(TicketOrigin defaultOrigin, CancellationToken cancellationToken)
    {
        var database = new SqliteTestDatabase { DefaultOrigin = defaultOrigin };
        await database._connection.OpenAsync(cancellationToken);
        database._provider = new ServiceCollection()
            .AddDbContextFactory<TriageDbContext>(o => o.UseSqlite(database._connection))
            .BuildServiceProvider();
        await using var db = await database.Factory.CreateDbContextAsync(cancellationToken);
        await db.Database.EnsureCreatedAsync(cancellationToken);
        return database;
    }

    public async Task AddTicketAsync(
        int id,
        string? description,
        string[]? comments = null,
        int workTypeId = 0,
        int? serviceId = null,
        int? teamId = null,
        int statusId = 4,
        DateTime? created = null,
        string? assignee = null,
        string? resolution = null,
        TicketOrigin? origin = null,
        string? sourceKey = null,
        CancellationToken cancellationToken = default)
    {
        await using var db = await Factory.CreateDbContextAsync(cancellationToken);
        db.Tickets.Add(new TicketEntity
        {
            Id = id,
            Summary = $"Summary {id}",
            Origin = origin ?? DefaultOrigin,
            SourceKey = sourceKey,
            Description = description,
            WorkTypeId = workTypeId,
            AffectedBusinessOrITServiceId = serviceId,
            ServiceTeamId = teamId,
            StatusId = statusId,
            CreatedDate = created ?? new DateTime(2025, 1, 1, 0, 0, 0, DateTimeKind.Utc),
            Assignee = assignee,
            Resolution = resolution,
            UrgencyId = 0,
            ImpactId = 0,
            PriorityId = 0,
            Comments = [.. (comments ?? []).Select(c => new CommentEntity { CommentText = c })],
        });
        await db.SaveChangesAsync(cancellationToken);
    }

    public async ValueTask DisposeAsync()
    {
        if (_provider is not null)
        {
            await _provider.DisposeAsync();
        }

        await _connection.DisposeAsync();
    }
}
