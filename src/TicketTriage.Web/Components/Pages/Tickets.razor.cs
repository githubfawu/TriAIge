using Microsoft.AspNetCore.Components;
using MudBlazor;
using TicketTriage.Web.Triage;

namespace TicketTriage.Web.Components.Pages;

/// <summary>Code-behind for <c>/tickets</c> (FR13/FR14). Loads via <see cref="ITriageBoardQuery"/> in
/// <c>OnInitializedAsync</c> (DB-only, safe under prerender - Leitplanke 7) and subscribes to
/// <see cref="TriageSessionStore.TicketChanged"/> only after the first render, unsubscribing on dispose (NFR7).</summary>
public partial class Tickets : IDisposable
{
    private readonly CancellationTokenSource _cts = new();
    private IReadOnlyList<TicketBoardRow> _rows = [];
    private TicketDisplayState? _filter;
    private bool _reloading;
    private bool _dirty;

    [Inject]
    private ITriageBoardQuery BoardQuery { get; set; } = null!;

    [Inject]
    private TriageSessionStore Store { get; set; } = null!;

    [Inject]
    private NavigationManager Navigation { get; set; } = null!;

    [SupplyParameterFromQuery(Name = "uploadId")]
    [Parameter]
    public int? UploadId { get; set; }

    /// <summary>Preselects the state filter chip (FR22/AC13: dashboard cards link here as <c>/tickets?state=…</c>).</summary>
    [SupplyParameterFromQuery(Name = "state")]
    [Parameter]
    public string? State { get; set; }

    private IEnumerable<TicketBoardRow> FilteredRows => _filter is null ? _rows : _rows.Where(r => r.State == _filter);

    private UploadProgress? Progress => UploadId is { } uploadId ? Store.GetUploadProgress(uploadId) : null;

    protected override async Task OnInitializedAsync()
    {
        _filter = Enum.TryParse<TicketDisplayState>(State, ignoreCase: true, out var parsed) ? parsed : null;
        _rows = await BoardQuery.GetRowsAsync(_cts.Token);
    }

    protected override void OnAfterRender(bool firstRender)
    {
        if (firstRender)
        {
            Store.TicketChanged += OnTicketChanged;
        }
    }

    private void OnTicketChanged(int ticketId) => _ = InvokeAsync(ReloadAsync);

    private async Task ReloadAsync()
    {
        if (_reloading)
        {
            _dirty = true;
            return;
        }

        _reloading = true;
        try
        {
            do
            {
                _dirty = false;
                _rows = await BoardQuery.GetRowsAsync(_cts.Token);
                StateHasChanged();
            }
            while (_dirty);
        }
        finally
        {
            _reloading = false;
        }
    }

    private void SetFilter(TicketDisplayState? state) => _filter = state;

    private void OpenReview(DataGridRowClickEventArgs<TicketBoardRow> args) =>
        Navigation.NavigateTo($"review/{args.Item.Id}");

    private async Task RequeueAsync(int ticketId)
    {
        Store.Requeue(ticketId);
        await ReloadAsync();
    }

    public void Dispose()
    {
        Store.TicketChanged -= OnTicketChanged;
        _cts.Cancel();
        _cts.Dispose();
    }
}
