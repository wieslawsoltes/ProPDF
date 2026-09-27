using ProPDF.Core;
using SkiaSharp;

namespace ProPDF.Rendering.Skia;

public interface ISkiaPdfDocument : IDisposable
{
    /// <summary>Records a normalized top-left-origin page. The caller owns the returned picture.</summary>
    SKPicture RecordPage(int pageNumber, CancellationToken cancellationToken = default);
}

public interface ISkiaPdfDocumentFactory
{
    ISkiaPdfDocument Open(PdfSnapshot snapshot);
}

public sealed record SkiaRendererOptions(long MaximumTileCacheBytes = 128L * 1024 * 1024,
    int MaximumDisplayLists = 24, int MaximumOpenDocuments = 2, int MaximumTileEdge = 2048);

public readonly record struct SkiaTileRequest(int PageNumber, PdfRect Clip, double PixelsPerPoint)
{
    public void Validate(PdfSnapshot snapshot, int maximumEdge)
    {
        var page = snapshot.GetPage(PageNumber);
        if (!double.IsFinite(PixelsPerPoint) || PixelsPerPoint is <= 0 or > 64 || Clip.IsEmpty ||
            Clip.X < 0 || Clip.Y < 0 || Clip.Right > page.Size.Width + 0.001 || Clip.Bottom > page.Size.Height + 0.001)
            throw new ArgumentOutOfRangeException(nameof(Clip));
        var width = Math.Ceiling(Clip.Width * PixelsPerPoint);
        var height = Math.Ceiling(Clip.Height * PixelsPerPoint);
        if (width > maximumEdge || height > maximumEdge || width < 1 || height < 1)
            throw new ArgumentOutOfRangeException(nameof(Clip), "Tile pixel dimensions exceed the configured bound.");
    }
}

internal sealed class SharedSkiaImage(SKImage image)
{
    private int _references = 1;
    public SKImage Image { get; } = image;
    public long ByteLength => (long)Image.Width * Image.Height * 4;

    public void Retain()
    {
        while (true)
        {
            var count = Volatile.Read(ref _references);
            ObjectDisposedException.ThrowIf(count == 0, this);
            if (Interlocked.CompareExchange(ref _references, count + 1, count) == count) return;
        }
    }

    public void Release()
    {
        if (Interlocked.Decrement(ref _references) == 0) Image.Dispose();
    }
}

/// <summary>An owned image lease. Cache eviction and renderer disposal do not invalidate live leases.</summary>
public sealed class SkiaTileLease : IDisposable
{
    private SharedSkiaImage? _owner;
    internal SkiaTileLease(SharedSkiaImage owner, SkiaTileRequest request)
    {
        owner.Retain();
        _owner = owner;
        Request = request;
    }

    public SkiaTileRequest Request { get; }
    public SKImage Image => (_owner ?? throw new ObjectDisposedException(nameof(SkiaTileLease))).Image;

    public SkiaTileLease Retain()
    {
        var owner = _owner ?? throw new ObjectDisposedException(nameof(SkiaTileLease));
        return new SkiaTileLease(owner, Request);
    }

    public void Dispose() => Interlocked.Exchange(ref _owner, null)?.Release();
}

public sealed record SkiaRendererStatistics(long CacheHits, long CacheMisses, long TileBytes, int TileCount, int DisplayListCount, int OpenDocumentCount);
