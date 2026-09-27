using ProPDF.Core;
using ProPDF.Rendering.Skia;
using SkiaSharp;

namespace ProPDF.Presentation;

public enum PdfOverlayKind { Selection, Search, Redaction }
public sealed record PdfPageVisual(int PageNumber, PdfRect Bounds);
public sealed record PdfOverlay(PdfRect Bounds, PdfOverlayKind Kind);
internal sealed record PdfTileVisual(SkiaTileLease Tile, PdfRect Bounds);

/// <summary>An immutable native-resource snapshot. Owns leases and remains valid after controller/cache disposal.</summary>
public sealed class PdfScene : IDisposable
{
    private readonly PdfTileVisual[] _tiles;
    private readonly PdfPageVisual[] _pages;
    private readonly PdfOverlay[] _overlays;
    private int _disposed;
    internal PdfScene(PdfSize viewport, PdfPageVisual[] pages, PdfTileVisual[] tiles, PdfOverlay[] overlays)
    {
        Viewport = viewport;
        _pages = pages;
        _tiles = tiles;
        _overlays = overlays;
    }
    public PdfSize Viewport { get; }
    public int TileCount => _tiles.Length;
    public int PageCount => _pages.Length;

    /// <summary>Creates an independent lifetime for deferred composition. Call before disposing this scene.</summary>
    public PdfScene Retain()
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        return new PdfScene(Viewport, _pages, _tiles.Select(tile => new PdfTileVisual(tile.Tile.Retain(), tile.Bounds)).ToArray(), _overlays);
    }

    public void Draw(SKCanvas canvas)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref _disposed) != 0, this);
        canvas.Save();
        try
        {
            canvas.ClipRect(new SKRect(0, 0, (float)Viewport.Width, (float)Viewport.Height));
            using var paint = new SKPaint { IsAntialias = true, Color = new SKColor(235, 239, 246) };
            canvas.DrawRect(0, 0, (float)Viewport.Width, (float)Viewport.Height, paint);
            foreach (var page in _pages)
            {
                var rect = Rect(page.Bounds);
                paint.Style = SKPaintStyle.Fill;
                paint.Color = new SKColor(28, 40, 64, 22);
                canvas.DrawRect(new SKRect(rect.Left + 3, rect.Top + 4, rect.Right + 3, rect.Bottom + 4), paint);
                paint.Color = SKColors.White;
                canvas.DrawRect(rect, paint);
                paint.Style = SKPaintStyle.Stroke;
                paint.StrokeWidth = 1;
                paint.Color = new SKColor(203, 211, 224);
                canvas.DrawRect(rect, paint);
            }
            paint.Style = SKPaintStyle.Fill;
            foreach (var tile in _tiles)
                canvas.DrawImage(tile.Tile.Image, Rect(tile.Bounds), new SKSamplingOptions(SKFilterMode.Linear, SKMipmapMode.None));
            foreach (var overlay in _overlays)
            {
                paint.Color = overlay.Kind switch
                {
                    PdfOverlayKind.Redaction => new SKColor(225, 55, 70, 60),
                    PdfOverlayKind.Search => new SKColor(255, 196, 35, 95),
                    _ => new SKColor(55, 110, 235, 58)
                };
                paint.Style = SKPaintStyle.Fill;
                canvas.DrawRect(Rect(overlay.Bounds), paint);
                if (overlay.Kind != PdfOverlayKind.Search)
                {
                    paint.Style = SKPaintStyle.Stroke;
                    paint.StrokeWidth = 1.5f;
                    paint.Color = overlay.Kind == PdfOverlayKind.Redaction ? new SKColor(205, 40, 60) : new SKColor(45, 100, 220);
                    canvas.DrawRect(Rect(overlay.Bounds), paint);
                }
            }
            if (_pages.Length == 0)
            {
                using var font = new SKFont(SKTypeface.Default, 18);
                paint.Style = SKPaintStyle.Fill;
                paint.Color = new SKColor(93, 109, 135);
                canvas.DrawText("Open a PDF to begin", 32, 56, font, paint);
            }
        }
        finally { canvas.Restore(); }
    }

    private static SKRect Rect(PdfRect rectangle) => new((float)rectangle.X, (float)rectangle.Y, (float)rectangle.Right, (float)rectangle.Bottom);
    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        foreach (var tile in _tiles) tile.Tile.Dispose();
    }
}
