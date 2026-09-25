using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using MudBlazor;
using TicketTriage.Core.Abstractions;
using TicketTriage.Core.Domain;
using TicketTriage.Web.Triage;

namespace TicketTriage.Web.Components.Pages;

/// <summary>Code-behind for <c>/</c>: state counters (linking to <c>/tickets?state=…</c>), review metrics from
/// <see cref="ITriageMetricsService"/> (FR25) and the pre-existing health block. The health check (which probes the
/// LLM) only runs on load or an explicit Refresh, so this page never spends extra tokens on its own.</summary>
public partial class Home : IDisposable
{
    private static readonly string[] DisplayedHealthChecks = ["sqlite", "agent-framework"];

    private readonly CancellationTokenSource _cts = new();

    private IReadOnlyList<TicketBoardRow> _rows = [];
    private TriageMetrics _metrics = new(0, 0, 0, 0, 0, null, new Dictionary<SuggestionField, int>(), null, null);
    private HealthReport? _report;
    private DateTimeOffset _checkedAt;
    private bool _loadingHealth;

    [Inject]
    private HealthCheckService HealthChecks { get; set; } = null!;

    [Inject]
    private ITicketBoardQuery BoardQuery { get; set; } = null!;

    [Inject]
    private ITriageMetricsService MetricsService { get; set; } = null!;

    protected override async Task OnInitializedAsync()
    {
        await LoadDashboardAsync();
        await RefreshHealthAsync();
    }

    private async Task LoadDashboardAsync()
    {
        _rows = await BoardQuery.GetRowsAsync(_cts.Token);
        _metrics = await MetricsService.GetAsync(_cts.Token);
    }

    private async Task RefreshAsync()
    {
        await LoadDashboardAsync();
        await RefreshHealthAsync();
    }

    private async Task RefreshHealthAsync()
    {
        _loadingHealth = true;
        try
        {
            _report = await HealthChecks.CheckHealthAsync(r => r.Tags.Contains(Extensions.ReadyTag), CancellationToken.None);
            _checkedAt = DateTimeOffset.UtcNow;
        }
        finally
        {
            _loadingHealth = false;
        }
    }

    private int CountFor(TicketDisplayState state) => _rows.Count(r => r.State == state);

    private static string FormatRate(double? rate) => rate is { } value ? value.ToString("P0") : "—";

    private static string FormatDuration(TimeSpan? span) => span is { } value ? $"{value.TotalSeconds:F0}s" : "—";

    // "AffectedServices" -> "Affected services" for display; the raw name stays in the tooltip.
    private static string Humanize(string pascal) =>
        string.Concat(pascal.Select((c, i) => i > 0 && char.IsUpper(c) ? " " + char.ToLowerInvariant(c) : c.ToString()));

    private static string HealthClass(HealthStatus? status) => status switch
    {
        HealthStatus.Healthy => "tt-health-ok",
        HealthStatus.Degraded => "tt-health-warn",
        HealthStatus.Unhealthy => "tt-health-bad",
        _ => "tt-health-unknown",
    };

    public void Dispose()
    {
        _cts.Cancel();
        _cts.Dispose();
    }
}
