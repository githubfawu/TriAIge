using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Forms;
using MudBlazor;
using TicketTriage.Web.Triage;

namespace TicketTriage.Web.Components.Pages;

/// <summary>Code-behind for <c>/upload</c> (FR1-FR5). No pipeline/LLM call happens here (NFR2): this page only
/// parses, previews and saves; the <see cref="TriageWorker"/> is the only thing that analyses tickets.</summary>
public partial class Upload : IDisposable
{
    private readonly CancellationTokenSource _cts = new();

    private UploadPreview? _preview;
    private string? _fileError;
    private bool _confirmWithoutTrainingData;
    private bool _loadingPreview;
    private bool _saving;

    [Inject]
    private IUploadIngestService IngestService { get; set; } = null!;

    [Inject]
    private ISnackbar Snackbar { get; set; } = null!;

    [Inject]
    private NavigationManager Navigation { get; set; } = null!;

    private bool CanSave =>
        _preview is { FileError: null, ValidCount: > 0 } preview
        && (preview.TrainingDataPresent || _confirmWithoutTrainingData);

    private async Task OnFilePickedAsync(IBrowserFile? file)
    {
        _preview = null;
        _fileError = null;
        _confirmWithoutTrainingData = false;

        if (file is null)
        {
            return;
        }

        _loadingPreview = true;
        try
        {
            await using var stream = file.OpenReadStream(UploadParser.MaxFileSizeBytes, _cts.Token);
            using var buffer = new MemoryStream();
            await stream.CopyToAsync(buffer, _cts.Token);

            _preview = await IngestService.PreviewAsync(buffer.ToArray(), _cts.Token);
        }
        catch (IOException)
        {
            // MudFileUpload/OpenReadStream throws when the file exceeds maxAllowedSize (Leitplanke 9).
            _fileError = $"File is larger than {UploadParser.MaxFileSizeBytes / 1_000_000} MB.";
        }
        finally
        {
            _loadingPreview = false;
        }
    }

    private async Task SaveAndAnalyseAsync()
    {
        if (_preview is null || _saving)
        {
            return;
        }

        _saving = true;
        try
        {
            var result = await IngestService.SaveAndEnqueueAsync(_preview, _confirmWithoutTrainingData, _cts.Token);
            switch (result.Outcome)
            {
                case SaveOutcome.Saved:
                    Snackbar.Add($"Saved {result.SavedCount} ticket(s) for triage.", Severity.Success);
                    Navigation.NavigateTo($"/tickets?uploadId={result.UploadId}");
                    break;
                case SaveOutcome.ConfirmationRequired:
                    Snackbar.Add("Confirm the missing training-data warning before saving.", Severity.Warning);
                    break;
                case SaveOutcome.NoValidEntries:
                    Snackbar.Add("No valid tickets to save.", Severity.Warning);
                    break;
            }
        }
        finally
        {
            _saving = false;
        }
    }

    public void Dispose()
    {
        _cts.Cancel();
        _cts.Dispose();
    }
}
