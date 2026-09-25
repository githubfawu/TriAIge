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

    private IEnumerable<TicketBoardRow> FilteredRows => _filter is null ? _rows : _rows.Where(r => r.State == _filter);

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
}
