using TicketTriage.Infrastructure.Analysis;

namespace TicketTriage.Infrastructure.Tests;

public sealed class WorkerLivenessTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 25, 12, 0, 0, TimeSpan.Zero);
    private static readonly TimeSpan MaxAge = TimeSpan.FromSeconds(90);

    [Fact]
    public void IsAlive_NoHeartbeat_IsFalse()
    {
        WorkerLiveness.IsAlive(null, Now, MaxAge).Should().BeFalse();
    }

    [Fact]
    public void IsAlive_RecentHeartbeat_IsTrue()
    {
        WorkerLiveness.IsAlive(Now.UtcDateTime.AddSeconds(-10), Now, MaxAge).Should().BeTrue();
    }

    [Fact]
    public void IsAlive_HeartbeatExactlyAtMaxAge_IsTrue()
    {
        WorkerLiveness.IsAlive(Now.UtcDateTime - MaxAge, Now, MaxAge).Should().BeTrue();
    }

    [Fact]
    public void IsAlive_StaleHeartbeat_IsFalse()
    {
        WorkerLiveness.IsAlive(Now.UtcDateTime.AddSeconds(-91), Now, MaxAge).Should().BeFalse();
    }

    [Fact]
    public void IsAlive_NonUtcNow_ComparesInUtc()
    {
        var local = Now.ToOffset(TimeSpan.FromHours(2));

        WorkerLiveness.IsAlive(Now.UtcDateTime.AddSeconds(-30), local, MaxAge).Should().BeTrue();
    }
}
