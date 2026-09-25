using Microsoft.AspNetCore.Components;
using MudBlazor;
using TicketTriage.Core.Abstractions;
using TicketTriage.Web.Triage;

namespace TicketTriage.Web.Components.Pages;

/// <summary>Code-behind for <c>/tickets</c> (FR13/FR14). Loads via <see cref="ITicketBoardQuery"/> in
/// <c>OnInitializedAsync</c> (DB-only, safe under prerender) and refreshes on a fixed interval since main's backend
/// has no ticket-changed event (blazor-server skill: timers/background updates always go through
/// <c>InvokeAsync(StateHasChanged)</c>).</summary>
public partial class Tickets : IAsyncDisposable
{
    private static readonly TimeSpan RefreshInterval = TimeSpan.FromSeconds(2);

    private readonly CancellationTokenSource _cts = new();
    private IReadOnlyList<TicketBoardRow> _rows = [];
    private TicketDisplayState? _filter;
    private string _search = "";
    private PeriodicTimer? _timer;
    private Task? _refreshLoop;

    [Inject]
    private ITicketBoardQuery BoardQuery { get; set; } = null!;

    [Inject]
    private IReviewService ReviewService { get; set; } = null!;

    [Inject]
    private NavigationManager Navigation { get; set; } = null!;

    /// <summary>Preselects the state filter chip (dashboard cards link here as <c>/tickets?state=…</c>).</summary>
    [SupplyParameterFromQuery(Name = "state")]
    [Parameter]
    public string? State { get; set; }

    private IEnumerable<TicketBoardRow> FilteredRows => _rows
        .Where(r => _filter is null || r.State == _filter)
        .Where(r => string.IsNullOrWhiteSpace(_search)
            || r.Summary.Contains(_search.Trim(), StringComparison.OrdinalIgnoreCase)
            || r.IssueKey.Contains(_search.Trim(), StringComparison.OrdinalIgnoreCase));

    protected override async Task OnInitializedAsync()
    {
        _filter = Enum.TryParse<TicketDisplayState>(State, ignoreCase: true, out var parsed) ? parsed : null;
        _rows = await BoardQuery.GetRowsAsync(_cts.Token);
    }

    protected override void OnAfterRender(bool firstRender)
    {
        if (firstRender)
        {
            _timer = new PeriodicTimer(RefreshInterval);
            _refreshLoop = RefreshLoopAsync();
        }
    }

    private async Task RefreshLoopAsync()
    {
        try
        {
            while (await _timer!.WaitForNextTickAsync(_cts.Token))
            {
                _rows = await BoardQuery.GetRowsAsync(_cts.Token);
                await InvokeAsync(StateHasChanged);
            }
        }
        catch (OperationCanceledException)
        {
            // Component disposed.
        }
    }

    private void SetFilter(TicketDisplayState? state) => _filter = state;

    private void OpenReview(DataGridRowClickEventArgs<TicketBoardRow> args) =>
        Navigation.NavigateTo($"review/{args.Item.Id}");

    private async Task RequeueAsync(TicketBoardRow row)
    {
        await ReviewService.RequeueFailedAsync(row.Id, row.Version, _cts.Token);
        _rows = await BoardQuery.GetRowsAsync(_cts.Token);
    }

    public async ValueTask DisposeAsync()
    {
        await _cts.CancelAsync();
        _timer?.Dispose();
        if (_refreshLoop is not null)
        {
            try
            {
                await _refreshLoop;
            }
            catch (OperationCanceledException)
            {
                // Expected on dispose.
            }
        }

        _cts.Dispose();
    }

    private static string PrioClass(string? priority) =>
        priority is null ? "tt-prio-none" : "tt-prio-" + priority.ToLowerInvariant();

    // The list shows one value per column (suggested, or final once approved); the original stays in the tooltip.
    private static string? ShownPriority(TicketBoardRow row) =>
        row.SuggestedPriority ?? (row.Priority == "—" ? null : row.Priority);

    private static string ShownWorkType(TicketBoardRow row) => row.SuggestedWorkType ?? row.WorkType;

    private static string Origin(TicketBoardRow row) => row.IsFinal ? "Final (analyst decision)" : "Suggested by the agent";

    private static string PriorityTooltip(TicketBoardRow row) =>
        row.SuggestedPriority is null ? "Original priority" : $"{Origin(row)} · original: {row.Priority}";

    private static string WorkTypeTooltip(TicketBoardRow row) =>
        row.SuggestedWorkType is null ? "Original work type" : $"{Origin(row)} · original: {row.WorkType}";

    private static int PriorityRank(string? priority) => priority switch
    {
        "Highest" => 0,
        "High" => 1,
        "Medium" => 2,
        "Low" => 3,
        "Lowest" => 4,
        _ => 5,
    };

    private static string TypeIcon(string workType) => workType switch
    {
        "Incident" => Icons.Material.Outlined.ReportProblem,
        "Service Request" => Icons.Material.Outlined.RoomService,
        _ => Icons.Material.Outlined.HelpOutline,
    };
}
