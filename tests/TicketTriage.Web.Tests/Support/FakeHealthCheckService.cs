using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace TicketTriage.Web.Tests.Support;

/// <summary>Minimal fake for the abstract <see cref="HealthCheckService"/> (agent-framework skill: never call a
/// real health/LLM probe in unit tests). Counts calls so tests can assert the dashboard's "no re-probe on
/// TicketChanged" rule (Slice 3 Technical Constraints).</summary>
public sealed class FakeHealthCheckService : HealthCheckService
{
    public int CallCount { get; private set; }

    public HealthReport Report { get; set; } = new(new Dictionary<string, HealthReportEntry>(), TimeSpan.Zero);

    public override Task<HealthReport> CheckHealthAsync(Func<HealthCheckRegistration, bool>? predicate, CancellationToken cancellationToken = default)
    {
        CallCount++;
        return Task.FromResult(Report);
    }
}
