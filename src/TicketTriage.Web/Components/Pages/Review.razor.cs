using Microsoft.AspNetCore.Components;
using MudBlazor;
using TicketTriage.Core.Abstractions;
using TicketTriage.Core.Domain;
using TicketTriage.Web.Components.Shared;
using TicketTriage.Web.Triage;

namespace TicketTriage.Web.Components.Pages;

/// <summary>Code-behind for <c>/review/{Id:int}</c> (FR15-FR21). Opening this page never analyses a ticket (NFR2):
/// it only reads via <see cref="IReviewService"/>, which the <c>AnalysisWorker</c>/<c>ITriagePipeline</c> never see.
/// A light timer refreshes the page while the ticket is still being analysed, since main's backend has no
/// ticket-changed event to subscribe to.</summary>
public partial class Review : IAsyncDisposable
{
    private static readonly TimeSpan RefreshInterval = TimeSpan.FromSeconds(2);

    private readonly CancellationTokenSource _cts = new();

    private TicketReview? _review;
    private ReviewFormModel? _form;
    private TicketDisplayState? _state;
    private string? _failureReason;
    private bool _loading = true;
    private bool _busy;
    private PeriodicTimer? _timer;
    private Task? _refreshLoop;

    [Parameter]
    public int Id { get; set; }

    [Inject]
    private IReviewService ReviewService { get; set; } = null!;

    [Inject]
    private ITicketBoardQuery BoardQuery { get; set; } = null!;

    [Inject]
    private ISnackbar Snackbar { get; set; } = null!;

    [Inject]
    private NavigationManager Navigation { get; set; } = null!;

    [Inject]
    private IDialogService DialogService { get; set; } = null!;

    private bool CanAccept => !_busy && _form is not null && !_form.HasChanges;

    private bool CanSave => !_busy && _form is not null && _form.HasChanges;

    /// <summary>Whether the AI suggestion itself differs from the original ticket at all (independent of whatever
    /// the analyst is currently editing) - the "No changes suggested" case.</summary>
    private bool BaselineHasNoChanges =>
        _review is { Suggestion: { } suggestion, Ticket: { } ticket }
        && !ValueDiffers(TriageVocabulary.ToJsonName(suggestion.WorkType), ticket.WorkType)
        && !DiffersServices(suggestion.AffectedServices, ticket.AffectedServices)
        && !ValueDiffers(suggestion.ServiceTeams.FirstOrDefault(), OriginalServiceTeam)
        && !ValueDiffers(suggestion.Assignee, ticket.Assignee)
        && !ValueDiffers(TriageVocabulary.ToJsonName(suggestion.Urgency), ticket.Urgency)
        && !ValueDiffers(TriageVocabulary.ToJsonName(suggestion.Impact), ticket.Impact)
        && !ValueDiffers(suggestion.ResolutionStatus is { } r ? TriageVocabulary.ToJsonName(r) : null, ticket.Resolution);

    private string? OriginalServiceTeam =>
        (_review?.Ticket.ServiceTeams.Count ?? 0) > 0 ? string.Join(", ", _review!.Ticket.ServiceTeams) : null;

    private string? OriginalAffectedServices =>
        (_review?.Ticket.AffectedServices.Count ?? 0) > 0 ? string.Join(", ", _review!.Ticket.AffectedServices) : null;

    /// <summary>Whether the currently displayed value for <paramref name="field"/> differs from the ticket's
    /// original (true both for an untouched AI suggestion and for a live analyst edit).</summary>
    private bool Differs(SuggestionField field)
    {
        if (_form is null || _review is null)
        {
            return false;
        }

        var ticket = _review.Ticket;
        return field switch
        {
            SuggestionField.WorkType => ValueDiffers(TriageVocabulary.ToJsonName(_form.WorkType), ticket.WorkType),
            SuggestionField.AffectedServices => DiffersServices(_form.AffectedServices, ticket.AffectedServices),
            SuggestionField.ServiceTeams => ValueDiffers(_form.ServiceTeam, OriginalServiceTeam),
            SuggestionField.Assignee => ValueDiffers(_form.Assignee, ticket.Assignee),
            SuggestionField.Urgency => ValueDiffers(TriageVocabulary.ToJsonName(_form.Urgency), ticket.Urgency),
            SuggestionField.Impact => ValueDiffers(TriageVocabulary.ToJsonName(_form.Impact), ticket.Impact),
            SuggestionField.ResolutionStatus => ValueDiffers(_form.Resolution is { } r ? TriageVocabulary.ToJsonName(r) : null, ticket.Resolution),
            _ => false,
        };
    }

    private static bool ValueDiffers(string? current, string? original) =>
        !string.Equals(current, original, StringComparison.OrdinalIgnoreCase);

    private static bool DiffersServices(IReadOnlyCollection<string> current, IReadOnlyCollection<string> original) =>
        !current.ToHashSet(StringComparer.OrdinalIgnoreCase).SetEquals(original);

    private string FieldClass(SuggestionField field) => Differs(field) ? "tt-field-changed" : "";

    // An empty original is the common case for challenge tickets; "was: —" there is noise.
    private string WasText(SuggestionField field) =>
        OriginalDisplay(field) is "—" ? "not set in original" : $"was: {OriginalDisplay(field)}";

    private string OriginalDisplay(SuggestionField field) => (_review is null ? null : field switch
    {
        SuggestionField.WorkType => _review.Ticket.WorkType,
        SuggestionField.AffectedServices => OriginalAffectedServices,
        SuggestionField.ServiceTeams => OriginalServiceTeam,
        SuggestionField.Assignee => _review.Ticket.Assignee,
        SuggestionField.Urgency => _review.Ticket.Urgency,
        SuggestionField.Impact => _review.Ticket.Impact,
        SuggestionField.ResolutionStatus => _review.Ticket.Resolution,
        _ => null,
    }) ?? "—";

    protected override async Task OnParametersSetAsync()
    {
        await LoadAsync();
        _loading = false;
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
                if (_state is TicketDisplayState.Approved or TicketDisplayState.Rejected)
                {
                    continue;
                }

                // Runs on the renderer's context so the form isn't swapped mid-render; keeps the analyst's unsaved edits.
                await InvokeAsync(async () =>
                {
                    await LoadAsync(preserveForm: true);
                    StateHasChanged();
                });
            }
        }
        catch (OperationCanceledException)
        {
            // Component disposed.
        }
    }

    private async Task LoadAsync(bool preserveForm = false)
    {
        var wasPending = _state == TicketDisplayState.Pending;
        _review = await ReviewService.OpenAsync(Id, _cts.Token);
        _state = DeriveState(_review);
        _failureReason = _review is { IsFailed: true } ? await BoardQuery.GetLatestFailureReasonAsync(Id, _cts.Token) : null;

        // A background refresh must never discard edits in progress; the form is only rebuilt when the ticket
        // changes state (e.g. analysis finished, or it was decided in another tab).
        if (preserveForm && wasPending && _state == TicketDisplayState.Pending && _form is not null)
        {
            return;
        }

        _form = _review?.EffectiveSuggestion is { } effective ? new ReviewFormModel(ReviewFormSnapshot.From(effective)) : null;
    }

    private static TicketDisplayState? DeriveState(TicketReview? review) => review switch
    {
        null => null,
        { Decision.Decision: ReviewDecision.Approved } => TicketDisplayState.Approved,
        { Decision.Decision: ReviewDecision.Rejected } => TicketDisplayState.Rejected,
        { IsFailed: true } => TicketDisplayState.Failed,
        { IsAnalysing: true } => TicketDisplayState.Analysing,
        { Suggestion: null } => TicketDisplayState.Queued,
        _ => TicketDisplayState.Pending,
    };

    private Task AcceptAsync() => DecideAsync(() => ReviewService.ApproveAsync(Id, _review!.Version, edits: null, _cts.Token), $"Ticket #{Id} accepted.");

    private Task SaveAsync() => DecideAsync(() => ReviewService.ApproveAsync(Id, _review!.Version, _form!.ToEdits(), _cts.Token), $"Ticket #{Id} saved.");

    private void ResetForm() => _form?.Reset();

    private void OnAffectedServicesChanged(IEnumerable<string> values)
    {
        if (_form is not null)
        {
            _form.AffectedServices = [.. values];
        }
    }

    private async Task DecideAsync(Func<Task<ReviewResult>> action, string successMessage)
    {
        if (_busy || _form is null || _review is null)
        {
            return;
        }

        _busy = true;
        try
        {
            var result = await action();
            await AfterDecisionAsync(result, successMessage);
        }
        finally
        {
            _busy = false;
        }
    }

    private async Task RejectAsync()
    {
        if (_busy || _review is null)
        {
            return;
        }

        var dialogRef = await DialogService.ShowAsync<RejectDialog>("Reject ticket");
        var result = await dialogRef.Result;
        if (result is null || result.Canceled || result.Data is not string reason || string.IsNullOrWhiteSpace(reason))
        {
            return;
        }

        _busy = true;
        try
        {
            var decision = await ReviewService.RejectAsync(Id, _review.Version, reason, _cts.Token);
            await AfterDecisionAsync(decision, $"Ticket #{Id} rejected.");
        }
        finally
        {
            _busy = false;
        }
    }

    private async Task AfterDecisionAsync(ReviewResult result, string successMessage)
    {
        if (!result.IsSuccess)
        {
            Snackbar.Add(result.Message ?? $"Ticket #{Id} could not be decided.", Severity.Warning);
            await LoadAsync();
            return;
        }

        Snackbar.Add(successMessage, Severity.Success);
        var nextId = await BoardQuery.GetNextPendingIdAsync(Id, _cts.Token);
        Navigation.NavigateTo(nextId is { } next ? $"/review/{next}" : "/tickets");
    }

    private async Task RequeueAsync()
    {
        if (_busy || _review is null)
        {
            return;
        }

        _busy = true;
        try
        {
            var result = await ReviewService.RequeueFailedAsync(Id, _review.Version, _cts.Token);
            if (!result.IsSuccess)
            {
                Snackbar.Add(result.Message ?? "Could not re-queue this ticket.", Severity.Warning);
            }

            await LoadAsync();
        }
        finally
        {
            _busy = false;
        }
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
}
