namespace TicketTriage.Web.Tests.Support;

/// <summary>Deterministic <see cref="TimeProvider"/> for tests that need to assert on elapsed durations.</summary>
public sealed class ManualTimeProvider(DateTimeOffset start) : TimeProvider
{
    private DateTimeOffset _now = start;

    public override DateTimeOffset GetUtcNow() => _now;

    public void Advance(TimeSpan delta) => _now += delta;
}
