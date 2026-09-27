using ProPDF.Core;
using SkiaSharp;

namespace ProPDF.Rendering.Skia;

public enum PdfRasterFormat { Png, Jpeg, Webp }
public sealed record PdfRasterExportOptions(double Dpi = 144, PdfRasterFormat Format = PdfRasterFormat.Png,
    int Quality = 90, long MaximumPixels = 32L * 1024 * 1024, int MaximumEncodedBytes = 128 * 1024 * 1024);

/// <summary>Bounded full-page/region image export assembled from the shared renderer's tiles. Returned images belong to the caller.</summary>
public sealed class PdfRasterExporter(SkiaPdfRenderer renderer)
{
    private readonly SkiaPdfRenderer _renderer = renderer ?? throw new ArgumentNullException(nameof(renderer));

    public Task<SKImage> RenderPageAsync(PdfSnapshot source, int pageNumber, PdfRasterExportOptions? options = null,
        PdfRect? region = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(source);
        var page = source.GetPage(pageNumber);
        var settings = options ?? new PdfRasterExportOptions();
        if (!double.IsFinite(settings.Dpi) || settings.Dpi is < 18 or > 1200 || !Enum.IsDefined(settings.Format) ||
            settings.Quality is < 0 or > 100 || settings.MaximumPixels is < 1 or > 128L * 1024 * 1024 || settings.MaximumEncodedBytes < 1)
            throw new ArgumentOutOfRangeException(nameof(options));
        var bounds = region ?? new PdfRect(0, 0, page.Size.Width, page.Size.Height);
        if (bounds.IsEmpty || bounds.X < 0 || bounds.Y < 0 || bounds.Right > page.Size.Width || bounds.Bottom > page.Size.Height)
            throw new ArgumentOutOfRangeException(nameof(region));
        var scale = settings.Dpi / 72;
        var w = Math.Ceiling(bounds.Width * scale);
        var h = Math.Ceiling(bounds.Height * scale);
        if (w > 65_536 || h > 65_536 || w * h > settings.MaximumPixels)
            throw new InvalidOperationException("Raster export exceeds the pixel budget. Lower DPI, reduce the region, or explicitly raise the budget.");
        var width = checked((int)w);
        var height = checked((int)h);
        return Task.Run(async () =>
        {
            using var surface = SKSurface.Create(new SKImageInfo(width, height, SKColorType.Bgra8888, SKAlphaType.Premul))
                ?? throw new InvalidOperationException("Unable to allocate the bounded export surface.");
            surface.Canvas.Clear(SKColors.White);
            const int edge = 512;
            for (var y = 0; y < height; y += edge)
                for (var x = 0; x < width; x += edge)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var tileBounds = new PdfRect(bounds.X + x / scale, bounds.Y + y / scale,
                        Math.Min(edge / scale, bounds.Width - x / scale), Math.Min(edge / scale, bounds.Height - y / scale));
                    using var tile = await _renderer.RenderTileAsync(source, new SkiaTileRequest(pageNumber, tileBounds, scale), cancellationToken).ConfigureAwait(false);
                    surface.Canvas.DrawImage(tile.Image, x, y);
                }
            cancellationToken.ThrowIfCancellationRequested();
            return surface.Snapshot();
        }, cancellationToken);
    }

    public async Task<PdfBinaryAsset> ExportPageAsync(PdfSnapshot source, int pageNumber, PdfRasterExportOptions? options = null,
        PdfRect? region = null, CancellationToken cancellationToken = default)
    {
        var settings = options ?? new PdfRasterExportOptions();
        using var image = await RenderPageAsync(source, pageNumber, settings, region, cancellationToken).ConfigureAwait(false);
        return await Task.Run(() =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            var format = settings.Format switch
            {
                PdfRasterFormat.Png => SKEncodedImageFormat.Png,
                PdfRasterFormat.Jpeg => SKEncodedImageFormat.Jpeg,
                PdfRasterFormat.Webp => SKEncodedImageFormat.Webp,
                _ => throw new ArgumentOutOfRangeException(nameof(options))
            };
            using var encoded = image.Encode(format, settings.Quality) ?? throw new NotSupportedException("This Skia build cannot encode the requested image format.");
            if (encoded.Size > settings.MaximumEncodedBytes) throw new InvalidDataException("Encoded image exceeds the byte budget.");
            cancellationToken.ThrowIfCancellationRequested();
            return new PdfBinaryAsset(encoded.ToArray(), settings.MaximumEncodedBytes);
        }, cancellationToken).ConfigureAwait(false);
    }

    public async Task SavePageAsync(PdfSnapshot source, int pageNumber, string path, PdfRasterExportOptions? options = null,
        PdfRect? region = null, CancellationToken cancellationToken = default)
    {
        var asset = await ExportPageAsync(source, pageNumber, options, region, cancellationToken).ConfigureAwait(false);
        await PdfFileOutput.SaveAssetAsync(path, asset, cancellationToken).ConfigureAwait(false);
    }
}
