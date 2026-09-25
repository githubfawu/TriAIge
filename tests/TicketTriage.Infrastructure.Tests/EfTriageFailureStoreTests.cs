using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using TicketTriage.Core.Domain;
using TicketTriage.Infrastructure.Persistence;

namespace TicketTriage.Infrastructure.Tests;

public sealed class EfTriageFailureStoreTests : IAsyncLifetime
{
    private static readonly DateTimeOffset Now = new(2026, 1, 2, 3, 4, 5, TimeSpan.Zero);

    private readonly SqliteConnection _connection = new("DataSource=:memory:");
    private ServiceProvider _provider = null!;
    private EfTriageFailureStore _store = null!;

    public async ValueTask InitializeAsync()
    {
        var ct = TestContext.Current.CancellationToken;
        await _connection.OpenAsync(ct);
        _provider = new ServiceCollection()
            .AddDbContextFactory<TriageDbContext>(o => o.UseSqlite(_connection))
            .BuildServiceProvider();

        var factory = _provider.GetRequiredService<IDbContextFactory<TriageDbContext>>();
        await using (var db = await factory.CreateDbContextAsync(ct))
        {
            await db.Database.EnsureCreatedAsync(ct);
            db.Tickets.Add(new TicketEntity { Id = 1, WorkTypeId = 0, StatusId = 0, Summary = "s" });
            await db.SaveChangesAsync(ct);
        }

        _store = new EfTriageFailureStore(factory, new FixedTimeProvider(Now));
    }

    public async ValueTask DisposeAsync()
    {
        await _provider.DisposeAsync();
        await _connection.DisposeAsync();
    }

    private async Task<TriageDbContext> OpenAsync() =>
        await _provider.GetRequiredService<IDbContextFactory<TriageDbContext>>()
            .CreateDbContextAsync(TestContext.Current.CancellationToken);

    [Fact]
    public async Task NewTicket_StartsWithZeroRetries_PerAC9()
    {
        await using var db = await OpenAsync();

        (await db.Tickets.SingleAsync(TestContext.Current.CancellationToken)).Retries.Should().Be(0);
    }

    [Fact]
    public async Task RecordFailure_TwiceForTicket_IncrementsRetriesAndWritesRows_PerAC9()
    {
        var ct = TestContext.Current.CancellationToken;

        var first = await _store.RecordFailureAsync(new TriageFailure(1, "K-1", 1, "boom1", "System.InvalidOperationException", "trace1"), ct);
        var second = await _store.RecordFailureAsync(new TriageFailure(1, "K-1", 2, "boom2", "System.TimeoutException", null), ct);

        first.Should().Be(1);
        second.Should().Be(2);
        await using var db = await OpenAsync();
        (await db.Tickets.SingleAsync(ct)).Retries.Should().Be(2);
        var rows = await db.TriageFailures.OrderBy(f => f.Id).ToListAsync(ct);
        rows.Should().HaveCount(2);
        rows[0].Should().BeEquivalentTo(new
        {
            TicketId = (int?)1,
            TicketKey = "K-1",
            Attempt = 1,
            Reason = "boom1",
            ExceptionType = "System.InvalidOperationException",
            StackTrace = "trace1",
            OccurredAtUtc = Now.UtcDateTime,
        });
        rows[1].Attempt.Should().Be(2);
        rows[1].StackTrace.Should().BeNull();
        rows[1].OccurredAtUtc.Should().Be(Now.UtcDateTime);
    }

    [Fact]
    public async Task RecordFailure_NullTicketId_WritesRowAndReturnsNull()
    {
        var ct = TestContext.Current.CancellationToken;

        var result = await _store.RecordFailureAsync(new TriageFailure(null, "K-X", 1, "r", "E", null), ct);

        result.Should().BeNull();
        await using var db = await OpenAsync();
        (await db.TriageFailures.SingleAsync(ct)).TicketId.Should().BeNull();
        (await db.Tickets.SingleAsync(ct)).Retries.Should().Be(0);
    }

    [Fact]
    public async Task RecordFailure_UnknownTicketId_WritesRowAndReturnsNull()
    {
        var ct = TestContext.Current.CancellationToken;

        var result = await _store.RecordFailureAsync(new TriageFailure(999, "K-999", 1, "r", "E", null), ct);

        result.Should().BeNull();
        await using var db = await OpenAsync();
        (await db.TriageFailures.SingleAsync(ct)).TicketId.Should().Be(999);
    }

    [Fact]
    public async Task RecordFailure_OverlongStackTrace_IsTruncatedTo4000()
    {
        var ct = TestContext.Current.CancellationToken;

        await _store.RecordFailureAsync(new TriageFailure(1, "K-1", 1, "r", "E", new string('x', 9000)), ct);

        await using var db = await OpenAsync();
        (await db.TriageFailures.SingleAsync(ct)).StackTrace.Should().HaveLength(4000);
    }

    [Fact]
    public async Task ResetRetries_SetsRetriesToZero_AndIsNoOpForUnknownTicket()
    {
        var ct = TestContext.Current.CancellationToken;
        await _store.RecordFailureAsync(new TriageFailure(1, "K-1", 1, "r", "E", null), ct);

        await _store.ResetRetriesAsync(1, ct);
        await _store.ResetRetriesAsync(999, ct);

        await using var db = await OpenAsync();
        (await db.Tickets.SingleAsync(ct)).Retries.Should().Be(0);
    }

    [Fact]
    public void PipelineTypes_DoNotDependOnEntityFrameworkCore()
    {
        var pipelineTypes = typeof(TicketTriage.Infrastructure.Pipeline.TriagePipeline).Assembly.GetTypes()
            .Where(t => t.Namespace == "TicketTriage.Infrastructure.Pipeline");

        var offending = pipelineTypes
            .SelectMany(t => t.GetFields(System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public
                    | System.Reflection.BindingFlags.NonPublic).Select(f => f.FieldType)
                .Concat(t.GetConstructors().SelectMany(c => c.GetParameters()).Select(p => p.ParameterType)))
            .Where(ReferencesEf)
            .ToList();

        offending.Should().BeEmpty();

        static bool ReferencesEf(Type type) =>
            (type.Namespace?.StartsWith("Microsoft.EntityFrameworkCore", StringComparison.Ordinal) ?? false)
            || (type.IsGenericType && type.GetGenericArguments().Any(ReferencesEf));
    }

    private sealed class FixedTimeProvider(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }
}
