using Microsoft.Data.Sqlite;

namespace TicketTriage.Infrastructure.Tests;

public class DatabaseInitializerRetryTests
{
    private static readonly TimeSpan NoDelay = TimeSpan.Zero;

    private static SqliteException Locked() => new("database is locked", 5);

    [Fact]
    public async Task RetryOnContention_LockedThenSucceeds_RetriesAndCompletes()
    {
        var calls = 0;

        await DatabaseInitializer.RetryOnContentionAsync(
            _ => ++calls < 3 ? throw Locked() : Task.CompletedTask,
            attempts: 5, NoDelay, TestContext.Current.CancellationToken);

        calls.Should().Be(3);
    }

    [Fact]
    public async Task RetryOnContention_AlreadyExistsWrappedInInvalidOperation_IsRetried()
    {
        var calls = 0;

        await DatabaseInitializer.RetryOnContentionAsync(
            _ => ++calls < 2
                ? throw new InvalidOperationException("outer", new SqliteException("table \"Ticket\" already exists", 1))
                : Task.CompletedTask,
            attempts: 3, NoDelay, TestContext.Current.CancellationToken);

        calls.Should().Be(2);
    }

    [Fact]
    public async Task RetryOnContention_AlwaysLocked_ThrowsAfterAllAttempts()
    {
        var calls = 0;

        var act = () => DatabaseInitializer.RetryOnContentionAsync(
            _ =>
            {
                calls++;
                throw Locked();
            },
            attempts: 3, NoDelay, TestContext.Current.CancellationToken);

        await act.Should().ThrowAsync<SqliteException>();
        calls.Should().Be(3);
    }

    [Fact]
    public async Task RetryOnContention_OtherError_IsNotRetried()
    {
        var calls = 0;

        var act = () => DatabaseInitializer.RetryOnContentionAsync(
            _ =>
            {
                calls++;
                throw new InvalidOperationException("boom");
            },
            attempts: 3, NoDelay, TestContext.Current.CancellationToken);

        await act.Should().ThrowAsync<InvalidOperationException>();
        calls.Should().Be(1);
    }
}
