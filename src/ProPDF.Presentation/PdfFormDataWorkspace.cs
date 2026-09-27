using ProPDF.Core;

namespace ProPDF.Presentation;

public sealed partial class PdfWorkspace
{
    private PdfUiCommand? _importXfdf, _exportXfdf, _importFormJson, _exportFormJson, _resetFormData, _checkFormData, _captureDefaults;
    private IReadOnlyList<PdfFormValidationIssue> _formIssues = Array.Empty<PdfFormValidationIssue>();
    private Guid? _formValidationRevision;
    public bool HasFormDataService => _context.Inspector is IPdfFormDataService;
    public IReadOnlyList<PdfFormValidationIssue> FormValidationIssues => _formValidationRevision == Document?.Id ? _formIssues : Array.Empty<PdfFormValidationIssue>();
    public string FormValidationSummary => _formValidationRevision is null ? "Check required values and text-length limits" :
        _formValidationRevision != Document?.Id ? "Document changed — check constraints again" :
        _formIssues.Count == 0 ? "No required-value or text-length issues" : $"{_formIssues.Count} required-value or text-length issues";
    public PdfUiCommand ImportXfdfCommand => _importXfdf ??= Command(token => ImportDataAsync(PdfFormDataFormat.Xfdf, token), CanImportFormData);
    public PdfUiCommand ExportXfdfCommand => _exportXfdf ??= Command(token => ExportDataAsync(PdfFormDataFormat.Xfdf, token), () => HasDocument() && HasFormDataService);
    public PdfUiCommand ImportFormJsonCommand => _importFormJson ??= Command(token => ImportDataAsync(PdfFormDataFormat.Json, token), CanImportFormData);
    public PdfUiCommand ExportFormJsonCommand => _exportFormJson ??= Command(token => ExportDataAsync(PdfFormDataFormat.Json, token), () => HasDocument() && HasFormDataService);
    public PdfUiCommand ResetFormDataCommand => _resetFormData ??= Command(ResetDataAsync, CanImportFormData);
    public PdfUiCommand CheckFormDataCommand => _checkFormData ??= Command(CheckDataAsync, () => HasDocument() && HasFormDataService);
    public PdfUiCommand CaptureFormDefaultsCommand => _captureDefaults ??= Command(CaptureDefaultsAsync, CanImportFormData);
    private bool CanImportFormData() => Can(PdfCapability.Forms) && HasFormDataService;

    private async Task ImportDataAsync(PdfFormDataFormat format, CancellationToken token)
    {
        var revision = Document!.Id;
        var path = await _dialogs.PickOpenPathAsync(PdfFileKind.Attachment, token);
        if (path is null) return;
        await using var input = File.OpenRead(path);
        var data = await PdfFormDataSerializer.ReadAsync(input, format, cancellationToken: token);
        await Session.ApplyAsync(new ImportFormData(data), revision, token);
    }
    private async Task ExportDataAsync(PdfFormDataFormat format, CancellationToken token)
    {
        EnsureNoPendingRedactions();
        var document = Document!;
        var path = await _dialogs.PickSavePathAsync(format == PdfFormDataFormat.Xfdf ? "fields.xfdf" : "fields.json", token);
        if (path is null) return;
        var service = (IPdfFormDataService)_context.Inspector!;
        // Password fields are excluded by default; NoExport and signature fields are always omitted.
        var data = await service.ReadFormDataAsync(document, cancellationToken: token);
        EnsureNoPendingRedactions();
        await PdfFormDataSerializer.SaveAsync(path, data, format, cancellationToken: token);
    }
    private async Task ResetDataAsync(CancellationToken token)
    {
        var revision = Document!.Id;
        if (await _dialogs.ConfirmAsync("Reset form values", "Restore editable fields to their PDF defaults? Fields without a default are cleared. This is one undoable transaction.", token))
            await Session.ApplyAsync(new ResetFormData(), revision, token);
    }
    private async Task CaptureDefaultsAsync(CancellationToken token)
    {
        var document = Document!;
        if (!await _dialogs.ConfirmAsync("Set form defaults", "Use the current exportable field values as defaults for future resets? Password and NoExport fields are excluded.", token)) return;
        var data = await ((IPdfFormDataService)_context.Inspector!).ReadFormDataAsync(document, cancellationToken: token);
        await Session.ApplyAsync(new SetFormDefaults(data), document.Id, token);
    }
    private async Task CheckDataAsync(CancellationToken token)
    {
        var document = Document!;
        var issues = await ((IPdfFormDataService)_context.Inspector!).ValidateFormDataAsync(document, token);
        _dispatch(() =>
        {
            if (_disposed || Document?.Id != document.Id) return;
            _formIssues = issues;
            _formValidationRevision = document.Id;
            Changed(nameof(FormValidationIssues));
            Changed(nameof(FormValidationSummary));
        });
    }
}
