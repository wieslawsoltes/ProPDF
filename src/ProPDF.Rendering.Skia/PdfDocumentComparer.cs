using ProPDF.Core;
using SkiaSharp;

namespace ProPDF.Rendering.Skia;

public sealed record PdfComparisonOptions(double Dpi = 72, byte ChannelTolerance = 8, bool CompareText = true,
    int MaximumPages = 1000, long MaximumPixelsPerPage = 4L * 1024 * 1024);
public enum PdfPageChangeKind { Unchanged, Changed, Resized, Added, Removed }
public sealed record PdfPageComparison(int PageNumber, PdfPageChangeKind Kind, long ChangedPixels,
    long ComparedPixels, PdfRect? ChangedBounds, bool TextChanged)
{
    public double ChangedFraction => ComparedPixels == 0 ? (Kind == PdfPageChangeKind.Unchanged ? 0 : 1) : (double)ChangedPixels / ComparedPixels;
    public override string ToString() => $"Page {PageNumber}: {Kind}";
}
public sealed record PdfComparisonResult(Guid LeftRevision, Guid RightRevision, IReadOnlyList<PdfPageComparison> Pages)
{
    public int ChangedPageCount => Pages.Count(page => page.Kind != PdfPageChangeKind.Unchanged);
    public bool AreEqual => ChangedPageCount == 0;
}

/// <summary>Page-number-aligned visual/text comparison. It does not claim semantic equivalence or automatically align inserted pages.</summary>
public sealed class PdfDocumentComparer(SkiaPdfRenderer renderer, IPdfTextService textService)
{
    private readonly PdfRasterExporter _raster = new(renderer);
    private readonly IPdfTextService _text = textService ?? throw new ArgumentNullException(nameof(textService));

    public Task<PdfComparisonResult> CompareAsync(PdfSnapshot left, PdfSnapshot right, PdfComparisonOptions? options = null,
        IProgress<int>? progress = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(left);
        ArgumentNullException.ThrowIfNull(right);
        var settings = options ?? new PdfComparisonOptions();
        if (!double.IsFinite(settings.Dpi) || settings.Dpi is < 18 or > 300 || settings.MaximumPages is < 1 or > 100_000 ||
            settings.MaximumPixelsPerPage is < 1 or > 32L * 1024 * 1024)
            throw new ArgumentOutOfRangeException(nameof(options));
        var count = Math.Max(left.Pages.Count, right.Pages.Count);
        if (count > settings.MaximumPages) throw new ArgumentOutOfRangeException(nameof(options), "The document comparison exceeds the configured page limit.");
        return Task.Run(async () =>
        {
            var pages = new List<PdfPageComparison>(count);
            var export = new PdfRasterExportOptions(settings.Dpi, MaximumPixels: settings.MaximumPixelsPerPage);
            for (var number = 1; number <= count; number++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (number > left.Pages.Count || number > right.Pages.Count)
                    pages.Add(new PdfPageComparison(number, number > left.Pages.Count ? PdfPageChangeKind.Added : PdfPageChangeKind.Removed, 0, 0, null, true));
                else if (left.Id == right.Id)
                    pages.Add(new PdfPageComparison(number, PdfPageChangeKind.Unchanged, 0, 0, null, false));
                else
                {
                    using var first = await _raster.RenderPageAsync(left, number, export, cancellationToken: cancellationToken).ConfigureAwait(false);
                    using var second = await _raster.RenderPageAsync(right, number, export, cancellationToken: cancellationToken).ConfigureAwait(false);
                    var width = Math.Max(first.Width, second.Width);
                    var height = Math.Max(first.Height, second.Height);
                    if ((long)width * height > settings.MaximumPixelsPerPage) throw new InvalidOperationException("The comparison canvas exceeds its pixel budget.");
                    using var a = SKBitmap.FromImage(first);
                    using var b = SKBitmap.FromImage(second);
                    var bytesA = a.Bytes;
                    var bytesB = b.Bytes;
                    long changed = 0;
                    var minX = width;
                    var minY = height;
                    var maxX = -1;
                    var maxY = -1;
                    for (var y = 0; y < height; y++)
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        for (var x = 0; x < width; x++)
                        {
                            var ia = y * a.RowBytes + x * 4;
                            var ib = y * b.RowBytes + x * 4;
                            var insideA = x < a.Width && y < a.Height;
                            var insideB = x < b.Width && y < b.Height;
                            var different = false;
                            for (var channel = 0; channel < 3; channel++)
                            {
                                var va = insideA ? bytesA[ia + channel] : 255;
                                var vb = insideB ? bytesB[ib + channel] : 255;
                                if (Math.Abs(va - vb) > settings.ChannelTolerance) { different = true; break; }
                            }
                            if (!different) continue;
                            changed++;
                            minX = Math.Min(minX, x); minY = Math.Min(minY, y);
                            maxX = Math.Max(maxX, x); maxY = Math.Max(maxY, y);
                        }
                    }
                    var textChanged = false;
                    if (settings.CompareText)
                    {
                        var ta = await _text.GetPageTextAsync(left, number, cancellationToken).ConfigureAwait(false);
                        var tb = await _text.GetPageTextAsync(right, number, cancellationToken).ConfigureAwait(false);
                        textChanged = !string.Equals(ta.Text, tb.Text, StringComparison.Ordinal);
                    }
                    var scale = settings.Dpi / 72;
                    PdfRect? bounds = changed == 0 ? null : new PdfRect(minX / scale, minY / scale, (maxX - minX + 1) / scale, (maxY - minY + 1) / scale);
                    var resized = left.GetPage(number).Size != right.GetPage(number).Size;
                    var kind = resized ? PdfPageChangeKind.Resized : changed != 0 || textChanged ? PdfPageChangeKind.Changed : PdfPageChangeKind.Unchanged;
                    pages.Add(new PdfPageComparison(number, kind, changed, (long)width * height, bounds, textChanged));
                }
                progress?.Report(number);
            }
            return new PdfComparisonResult(left.Id, right.Id, pages.AsReadOnly());
        }, cancellationToken);
    }
}
