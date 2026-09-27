using System.Text.Json;
using ProPDF.Core;
using ProPDF.Rendering.Skia;

namespace ProPDF.Presentation;

public sealed partial class PdfViewportController
{
    public Task<PdfBinaryAsset> ExportPageImageAsync(PdfSnapshot document, int pageNumber, PdfRasterExportOptions? options = null,
        PdfRect? region = null, CancellationToken cancellationToken = default) =>
        new PdfRasterExporter(_renderer).ExportPageAsync(document, pageNumber, options, region, cancellationToken);

    public Task ExportTextAsync(PdfSnapshot document, string path, IEnumerable<int>? pages = null, CancellationToken cancellationToken = default) =>
        new PdfTextExporter(_textService).SaveAsync(document, path, pages, cancellationToken: cancellationToken);

    public Task<PdfComparisonResult> CompareAsync(PdfSnapshot left, PdfSnapshot right, PdfComparisonOptions? options = null,
        IProgress<int>? progress = null, CancellationToken cancellationToken = default) =>
        new PdfDocumentComparer(_renderer, _textService).CompareAsync(left, right, options, progress, cancellationToken);
}

public sealed partial class PdfWorkspace
{
    private PdfUiCommand? _exportPng;
    private PdfUiCommand? _exportJpeg;
    private PdfUiCommand? _exportText;
    private PdfUiCommand? _compare;
    private PdfUiCommand? _saveComparison;
    private PdfUiCommand? _extractRange;
    private PdfComparisonResult? _comparison;
    private PdfPageComparison? _selectedComparisonPage;
    private string _pageRange = "all";
    private string _exportDpi = "144";

    public string PageRange { get => _pageRange; set => Set(ref _pageRange, value ?? "all"); }
    public string ExportDpi { get => _exportDpi; set => Set(ref _exportDpi, value ?? "144"); }
    public string ComparisonSummary => _comparison is null ? "" : _comparison.LeftRevision != Document?.Id ?
        "Comparison is stale — compare the edited document again" : $"{_comparison.ChangedPageCount} / {_comparison.Pages.Count} pages differ";
    public IReadOnlyList<PdfPageComparison> ComparisonPages => _comparison?.Pages ?? Array.Empty<PdfPageComparison>();
    public PdfPageComparison? SelectedComparisonPage
    {
        get => _selectedComparisonPage;
        set
        {
            if (Set(ref _selectedComparisonPage, value) && value is not null && _comparison?.LeftRevision == Document?.Id && value.PageNumber <= Viewport.PageCount)
                Viewport.GoToPage(value.PageNumber);
        }
    }
    public PdfUiCommand ExportPngCommand => _exportPng ??= Command(token => ExportImageAsync(PdfRasterFormat.Png, token), HasDocument);
    public PdfUiCommand ExportJpegCommand => _exportJpeg ??= Command(token => ExportImageAsync(PdfRasterFormat.Jpeg, token), HasDocument);
    public PdfUiCommand ExportTextCommand => _exportText ??= Command(ExportTextFileAsync, HasDocument);
    public PdfUiCommand CompareCommand => _compare ??= Command(CompareFileAsync, () => HasDocument() && _context.DocumentLoader is not null);
    public PdfUiCommand SaveComparisonCommand => _saveComparison ??= Command(SaveComparisonAsync, () => _comparison is not null);
    public PdfUiCommand ExtractRangeCommand => _extractRange ??= Command(ExtractRangeAsync, () => HasDocument() && _context.PageExtractor is not null);

    private void EnsureNoPendingRedactions()
    {
        if (Viewport.PendingRedactions != 0) throw new InvalidOperationException("Apply or clear pending redaction marks before exporting. Marks are not content removal.");
    }
    private async Task ExportImageAsync(PdfRasterFormat format, CancellationToken token)
    {
        EnsureNoPendingRedactions();
        var document = Document!;
        var page = Viewport.CurrentPage;
        if (!double.TryParse(ExportDpi, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var dpi) || !double.IsFinite(dpi) || dpi is < 18 or > 1200)
            throw new ArgumentException("Enter a DPI between 18 and 1200.");
        var path = await _dialogs.PickSavePathAsync($"page-{page}.{(format == PdfRasterFormat.Png ? "png" : "jpg")}", token);
        if (path is null) return;
        var image = await Viewport.ExportPageImageAsync(document, page, new PdfRasterExportOptions(dpi, format), cancellationToken: token);
        EnsureNoPendingRedactions();
        await PdfFileOutput.SaveAssetAsync(path, image, token);
    }
    private async Task ExportTextFileAsync(CancellationToken token)
    {
        EnsureNoPendingRedactions();
        var document = Document!;
        var pages = PdfPageSelection.Parse(PageRange, document.Pages.Count);
        var path = await _dialogs.PickSavePathAsync("document.txt", token);
        if (path is null) return;
        EnsureNoPendingRedactions();
        await Viewport.ExportTextAsync(document, path, pages, token);
    }
    private async Task ExtractRangeAsync(CancellationToken token)
    {
        EnsureNoPendingRedactions();
        var document = Document!;
        var pages = PdfPageSelection.Parse(PageRange, document.Pages.Count);
        var path = await _dialogs.PickSavePathAsync("selected-pages.pdf", token);
        if (path is null) return;
        if (!await _dialogs.ConfirmAsync("Extract pages", "The exported copy will be unsigned and unencrypted. Continue?", token)) return;
        var result = await _context.PageExtractor!.ExtractPagesAsync(document, pages, token);
        EnsureNoPendingRedactions();
        await PdfStreams.SaveAtomicAsync(result, path, token);
    }
    private async Task CompareFileAsync(CancellationToken token)
    {
        var document = Document!;
        var path = await _dialogs.PickOpenPathAsync(PdfFileKind.Pdf, token);
        if (path is null) return;
        await using var input = File.OpenRead(path);
        var other = await _context.DocumentLoader!.OpenAsync(input, cancellationToken: token);
        var comparison = await Viewport.CompareAsync(document, other, cancellationToken: token);
        _dispatch(() =>
        {
            if (_disposed || Document?.Id != document.Id) return;
            _comparison = comparison;
            _selectedComparisonPage = null;
            Changed(null);
            RefreshCommands();
        });
    }
    private async Task SaveComparisonAsync(CancellationToken token)
    {
        var comparison = _comparison ?? throw new InvalidOperationException("Compare two documents first.");
        var path = await _dialogs.PickSavePathAsync("comparison.json", token);
        if (path is null) return;
        await PdfFileOutput.WriteAtomicAsync(path,
            (stream, cancellation) => JsonSerializer.SerializeAsync(stream, comparison, new JsonSerializerOptions { WriteIndented = true }, cancellation), token);
    }
}
