using Microsoft.AspNetCore.Components;
using MudBlazor;
using TicketTriage.Core.Domain;
using TicketTriage.Web.Components.Shared;
using TicketTriage.Web.Triage;

namespace TicketTriage.Web.Components.Pages;

/// <summary>Code-behind for <c>/review/{Id:int}</c> (FR15-FR21, Slice 2). No component injects
/// <see cref="Core.Abstractions.ITriagePipeline"/> directly (NFR2): opening this page only ever reads via
/// <see cref="ITriageBoardQuery"/>; <see cref="TriageWorker"/> is the only thing that analyses tickets.</summary>
public partial class Review : IDisposable
{
    private readonly CancellationTokenSource _cts = new();

    private TicketReviewData? _data;
    private ReviewFormModel? _form;
    private bool _loading = true;
    private bool _busy;
    private int? _effectsForId;

    [Parameter]
    public int Id { get; set; }

    [Inject]
    private ITriageBoardQuery BoardQuery { get; set; } = null!;

    [Inject]
    private IUploadIngestService IngestService { get; set; } = null!;

    [Inject]
    private IReviewDecisionService DecisionService { get; set; } = null!;

    [Inject]
    private TriageSessionStore Store { get; set; } = null!;

    [Inject]
    private ISnackbar Snackbar { get; set; } = null!;

    [Inject]
    private NavigationManager Navigation { get; set; } = null!;

    [Inject]
    private IDialogService DialogService { get; set; } = null!;

    private bool CanAccept => !_busy && _form is not null && !_form.HasChanges;

    private bool CanSave => !_busy && _form is not null && _form.HasChanges;

    /// <summary>Whether the suggestion (the form's baseline) differs from the original at all - independent of
    /// whatever the analyst is currently editing (FR15's "No changes suggested" case).</summary>
    private bool BaselineHasNoChanges =>
        _data is { FormBaseline: { } baseline } data
        && TriageVocabulary.ToJsonName(baseline.WorkType) == data.OriginalWorkType
        && string.Equals(baseline.AffectedService, data.OriginalAffectedService, StringComparison.Ordinal)
        && string.Equals(baseline.ServiceTeam, data.OriginalServiceTeam, StringComparison.Ordinal)
        && string.Equals(baseline.Assignee, data.OriginalAssignee, StringComparison.Ordinal)
        && TriageVocabulary.ToJsonName(baseline.Urgency) == data.OriginalUrgency
        && TriageVocabulary.DbImpactNameFor(baseline.Impact) == data.OriginalImpact
        && string.Equals(baseline.Resolution is { } r ? TriageVocabulary.ToJsonName(r) : null, data.OriginalResolution, StringComparison.Ordinal);

    /// <summary>Whether the currently displayed value for <paramref name="field"/> differs from the ticket's
    /// original (FR15) - true both for an untouched AI suggestion and for a live analyst edit.</summary>
    private bool Differs(ReviewField field)
    {
        if (_form is null || _data is null)
        {
            return false;
        }

        return field switch
        {
            ReviewField.WorkType => TriageVocabulary.ToJsonName(_form.WorkType) != _data.OriginalWorkType,
            ReviewField.AffectedService => !string.Equals(_form.AffectedService, _data.OriginalAffectedService, StringComparison.Ordinal),
            ReviewField.ServiceTeam => !string.Equals(_form.ServiceTeam, _data.OriginalServiceTeam, StringComparison.Ordinal),
            ReviewField.Assignee => !string.Equals(_form.Assignee, _data.OriginalAssignee, StringComparison.Ordinal),
            ReviewField.Urgency => TriageVocabulary.ToJsonName(_form.Urgency) != _data.OriginalUrgency,
            ReviewField.Impact => TriageVocabulary.DbImpactNameFor(_form.Impact) != _data.OriginalImpact,
            ReviewField.Resolution => !string.Equals(_form.Resolution is { } r ? TriageVocabulary.ToJsonName(r) : null, _data.OriginalResolution, StringComparison.Ordinal),
            _ => false,
        };
    }

    private string FieldClass(ReviewField field) => Differs(field) ? "tt-field-changed" : "";

    private string OriginalDisplay(ReviewField field) => (_data is null ? null : field switch
    {
        ReviewField.WorkType => _data.OriginalWorkType,
        ReviewField.AffectedService => _data.OriginalAffectedService,
        ReviewField.ServiceTeam => _data.OriginalServiceTeam,
        ReviewField.Assignee => _data.OriginalAssignee,
        ReviewField.Urgency => _data.OriginalUrgency,
        ReviewField.Impact => _data.OriginalImpact,
        ReviewField.Resolution => _data.OriginalResolution,
        _ => null,
    }) ?? "—";

    private string? Hint(ReviewField field) => _data?.Hints.GetValueOrDefault(field);

    protected override async Task OnParametersSetAsync()
    {
        await LoadAsync();
        _loading = false;
    }

    protected override void OnAfterRender(bool firstRender)
    {
        if (firstRender)
        {
            Store.TicketChanged += OnTicketChanged;
        }

        if (_effectsForId != Id)
        {
            _effectsForId = Id;
            // Opening the review page jumps a still-queued ticket to the front (FR10) and records the first-open
            // timestamp for later metrics - never a pipeline/LLM call (NFR2/Leitplanke 7).
            Store.Prioritize(Id);
            Store.MarkOpened(Id);
        }
    }

    private async Task LoadAsync()
    {
        _data = await BoardQuery.GetReviewAsync(Id, _cts.Token);
        _form = _data?.FormBaseline is { } baseline ? new ReviewFormModel(baseline) : null;
    }

    private void OnTicketChanged(int ticketId)
    {
        if (ticketId != Id)
        {
            return;
        }

        // Only reason a Pending ticket's TicketChanged fires for its own id is another tab deciding it (AC10) -
        // reloading here never discards in-progress edits for a still-Pending ticket, since that combination
        // cannot occur (Leitplanke 8).
        _ = InvokeAsync(async () =>
        {
            await LoadAsync();
            StateHasChanged();
        });
    }

    private Task AcceptAsync() => DecideAsync(() => DecisionService.ApproveAsync(Id, _form!, _cts.Token), $"Ticket #{Id} accepted.");

    private Task SaveAsync() => DecideAsync(() => DecisionService.ApproveAsync(Id, _form!, _cts.Token), $"Ticket #{Id} saved.");

    private void ResetForm() => _form?.Reset();

    private async Task DecideAsync(Func<Task<DecisionResult>> action, string successMessage)
    {
        if (_busy || _form is null)
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
        if (_busy)
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
            var decision = await DecisionService.RejectAsync(Id, reason, _cts.Token);
            await AfterDecisionAsync(decision, $"Ticket #{Id} rejected.");
        }
        finally
        {
            _busy = false;
        }
    }

    private async Task AfterDecisionAsync(DecisionResult result, string successMessage)
    {
        if (result.Outcome == DecisionOutcome.AlreadyDecided)
        {
            Snackbar.Add($"Ticket #{Id} was already decided.", Severity.Warning);
            await LoadAsync();
            return;
        }

        Snackbar.Add(successMessage, Severity.Success);
        var nextId = await BoardQuery.GetNextPendingIdAsync(Id, _cts.Token);
        Navigation.NavigateTo(nextId is { } next ? $"/review/{next}" : "/tickets");
    }

    private async Task AnalyseNowAsync()
    {
        await IngestService.EnqueueExistingAsync(Id, _cts.Token);
        await LoadAsync();
    }

    private async Task RequeueAsync()
    {
        Store.Requeue(Id);
        await LoadAsync();
    }

    public void Dispose()
    {
        Store.TicketChanged -= OnTicketChanged;
        _cts.Cancel();
        _cts.Dispose();
    }
}
