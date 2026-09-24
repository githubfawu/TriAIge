using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using MudBlazor;
using TicketTriage.Web.Triage;

namespace TicketTriage.Web.Components.Pages;

/// <summary>Code-behind for <c>/</c> (FR22): state counters (linking to <c>/tickets?state=…</c>), the persistent
/// Approval rate and session-only metrics (<see cref="SessionMetrics"/>), plus the pre-existing health block.
/// <see cref="OnTicketChanged"/> only reloads the board/metrics - the health check (which probes the LLM) only
/// runs on load or an explicit Refresh (Technical Constraints), so live ticket updates never spend extra tokens.</summary>
public partial class Home : IDisposable
{
    private static readonly string[] DisplayedHealthChecks = ["sqlite", "agent-framework"];

    private readonly CancellationTokenSource _cts = new();

    private IReadOnlyList<TicketBoardRow> _rows = [];
    private DecisionTotals _decisionTotals = new(0, 0);
    private SessionMetricsSnapshot _metrics = SessionMetricsSnapshot.Empty;
    private HealthReport? _report;
    private DateTimeOffset _checkedAt;
    private bool _loadingHealth;

    [Inject]
    private HealthCheckService HealthChecks { get; set; } = null!;

    [Inject]
    private ITriageBoardQuery BoardQuery { get; set; } = null!;

    [Inject]
    private TriageSessionStore Store { get; set; } = null!;

    protected override async Task OnInitializedAsync()
    {
        await LoadDashboardAsync();
        await RefreshHealthAsync();
    }

    protected override void OnAfterRender(bool firstRender)
    {
        if (firstRender)
        {
            Store.TicketChanged += OnTicketChanged;
        }
    }

    private void OnTicketChanged(int ticketId) => _ = InvokeAsync(LoadDashboardAsync);

    private async Task LoadDashboardAsync()
    {
        _rows = await BoardQuery.GetRowsAsync(_cts.Token);
        _decisionTotals = await BoardQuery.GetDecisionTotalsAsync(_cts.Token);
        _metrics = SessionMetrics.Compute(Store.Snapshot());
        StateHasChanged();
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

    private static Color ColorFor(HealthStatus? status) => status switch
    {
        HealthStatus.Healthy => Color.Success,
        HealthStatus.Degraded => Color.Warning,
        HealthStatus.Unhealthy => Color.Error,
        _ => Color.Default,
    };

    public void Dispose()
    {
        Store.TicketChanged -= OnTicketChanged;
        _cts.Cancel();
        _cts.Dispose();
    }
}
