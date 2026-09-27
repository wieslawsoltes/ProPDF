using ProPDF.Core;
using SkiaSharp;

namespace ProPDF.Rendering.Skia;

/// <summary>Single-worker renderer: PDF parser state and native display lists are never accessed concurrently.</summary>
public sealed class SkiaPdfRenderer : IAsyncDisposable
{
    private readonly ISkiaPdfDocumentFactory _factory;
    private readonly SkiaRendererOptions _options;
    private readonly SemaphoreSlim _worker = new(1, 1);
    private readonly Dictionary<(Guid, SkiaTileRequest), TileEntry> _tiles = [];
    private readonly LinkedList<(Guid, SkiaTileRequest)> _tileLru = [];
    private readonly Dictionary<(Guid, int), PictureEntry> _pictures = [];
    private readonly LinkedList<(Guid, int)> _pictureLru = [];
    private readonly Dictionary<Guid, DocumentEntry> _documents = [];
    private readonly LinkedList<Guid> _documentLru = [];
    private bool _disposed;
    private long _bytes;
    private long _pictureBytes;
    private long _hits;
    private long _misses;

    private sealed record TileEntry(SharedSkiaImage Image, LinkedListNode<(Guid, SkiaTileRequest)> Node);
    private sealed record PictureEntry(SKPicture Picture, LinkedListNode<(Guid, int)> Node, long Bytes);
    private sealed record DocumentEntry(ISkiaPdfDocument Document, LinkedListNode<Guid> Node);

    public SkiaPdfRenderer(ISkiaPdfDocumentFactory factory, SkiaRendererOptions? options = null)
    {
        _factory = factory ?? throw new ArgumentNullException(nameof(factory));
        _options = options ?? new SkiaRendererOptions();
        if (_options.MaximumTileCacheBytes < 0 || _options.MaximumDisplayLists < 1 ||
            _options.MaximumOpenDocuments < 1 || _options.MaximumDisplayListBytes < 0 || _options.MaximumTileEdge is < 16 or > 8192)
            throw new ArgumentOutOfRangeException(nameof(options));
    }

    public async Task<SkiaTileLease> RenderTileAsync(PdfSnapshot snapshot, SkiaTileRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        request.Validate(snapshot, _options.MaximumTileEdge);
        await _worker.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            cancellationToken.ThrowIfCancellationRequested();
            var key = (snapshot.Id, request);
            if (_tiles.TryGetValue(key, out var cached))
            {
                _tileLru.Remove(cached.Node);
                _tileLru.AddLast(cached.Node);
                _hits++;
                return new SkiaTileLease(cached.Image, request);
            }
            _misses++;
            // Task.Run is deliberately inside the asynchronous semaphore: queued requests do not occupy thread-pool threads.
            var image = await Task.Run(() => Rasterize(snapshot, request, cancellationToken), cancellationToken).ConfigureAwait(false);
            var shared = new SharedSkiaImage(image);
            SkiaTileLease? lease = null;
            try
            {
                cancellationToken.ThrowIfCancellationRequested();
                lease = new SkiaTileLease(shared, request);
                _tiles.Add(key, new TileEntry(shared, _tileLru.AddLast(key)));
                _bytes += shared.ByteLength;
                TrimTiles();
                return lease;
            }
            catch
            {
                lease?.Dispose();
                if (!_tiles.ContainsKey(key)) shared.Release();
                throw;
            }
        }
        finally { _worker.Release(); }
    }

    private SKImage Rasterize(PdfSnapshot snapshot, SkiaTileRequest request, CancellationToken cancellationToken)
    {
        var picture = GetPicture(snapshot, request.PageNumber, cancellationToken, out var transient);
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            var width = request.PixelWidth;
            var height = request.PixelHeight;
            // A small device-space gutter prevents tile-edge clipping from truncating antialiasing/filter support.
            const int gutter = 2;
            using var surface = SKSurface.Create(new SKImageInfo(width + 2 * gutter, height + 2 * gutter, SKColorType.Bgra8888, SKAlphaType.Premul))
                ?? throw new InvalidOperationException("Skia could not allocate a tile surface.");
            var canvas = surface.Canvas;
            canvas.Clear(SKColors.White);
            var scale = (float)request.PixelsPerPoint;
            // Multiply in double precision before converting to Skia's floats. Scale then Translate would
            // round each tile's PDF-point origin separately, changing the page-wide image sampling phase.
            var originX = request.Clip.X * request.PixelsPerPoint;
            var originY = request.Clip.Y * request.PixelsPerPoint;
            if (Math.Abs(originX - Math.Round(originX)) < 1e-7) originX = Math.Round(originX);
            if (Math.Abs(originY - Math.Round(originY)) < 1e-7) originY = Math.Round(originY);
            canvas.SetMatrix(new SKMatrix(scale, 0, (float)(gutter - originX), 0, scale, (float)(gutter - originY), 0, 0, 1));
            var page = snapshot.GetPage(request.PageNumber);
            canvas.ClipRect(new SKRect(0, 0, (float)page.Size.Width, (float)page.Size.Height));
            canvas.DrawPicture(picture);
            cancellationToken.ThrowIfCancellationRequested();
            return surface.Snapshot(new SKRectI(gutter, gutter, gutter + width, gutter + height));
        }
        finally { if (transient) picture.Dispose(); }
    }

    private SKPicture GetPicture(PdfSnapshot snapshot, int pageNumber, CancellationToken cancellationToken, out bool transient)
    {
        transient = false;
        var key = (snapshot.Id, pageNumber);
        if (_pictures.TryGetValue(key, out var cached))
        {
            _pictureLru.Remove(cached.Node);
            _pictureLru.AddLast(cached.Node);
            return cached.Picture;
        }
        var document = GetDocument(snapshot);
        var picture = document.RecordPage(pageNumber, cancellationToken);
        try { cancellationToken.ThrowIfCancellationRequested(); }
        catch { picture.Dispose(); throw; }
        var bytes = Math.Max(0, picture.ApproximateBytesUsed);
        if (bytes > _options.MaximumDisplayListBytes || _options.MaximumDisplayListBytes == 0)
        {
            transient = true;
            return picture; // Oversized pictures are used once and disposed after rasterization.
        }
        _pictures.Add(key, new PictureEntry(picture, _pictureLru.AddLast(key), bytes));
        _pictureBytes += bytes;
        while (_pictures.Count > _options.MaximumDisplayLists || _pictureBytes > _options.MaximumDisplayListBytes)
        {
            var oldest = _pictureLru.First!;
            _pictureLru.RemoveFirst();
            _pictures.Remove(oldest.Value, out var removed);
            _pictureBytes -= removed!.Bytes;
            removed.Picture.Dispose();
        }
        return picture;
    }

    private ISkiaPdfDocument GetDocument(PdfSnapshot snapshot)
    {
        if (_documents.TryGetValue(snapshot.Id, out var cached))
        {
            _documentLru.Remove(cached.Node);
            _documentLru.AddLast(cached.Node);
            return cached.Document;
        }
        var document = _factory.Open(snapshot);
        _documents.Add(snapshot.Id, new DocumentEntry(document, _documentLru.AddLast(snapshot.Id)));
        while (_documents.Count > _options.MaximumOpenDocuments)
        {
            var oldest = _documentLru.First!;
            _documentLru.RemoveFirst();
            _documents.Remove(oldest.Value, out var removed);
            removed!.Document.Dispose();
        }
        return document;
    }

    private void TrimTiles()
    {
        while (_bytes > _options.MaximumTileCacheBytes && _tileLru.First is { } oldest)
        {
            _tileLru.RemoveFirst();
            _tiles.Remove(oldest.Value, out var removed);
            _bytes -= removed!.Image.ByteLength;
            removed.Image.Release();
        }
    }

    public async Task<SkiaRendererStatistics> GetStatisticsAsync(CancellationToken cancellationToken = default)
    {
        await _worker.WaitAsync(cancellationToken).ConfigureAwait(false);
        try { return new SkiaRendererStatistics(_hits, _misses, _bytes, _tiles.Count, _pictures.Count, _documents.Count, _pictureBytes); }
        finally { _worker.Release(); }
    }

    public async ValueTask DisposeAsync()
    {
        await _worker.WaitAsync().ConfigureAwait(false);
        try
        {
            if (_disposed) return;
            _disposed = true;
            foreach (var entry in _tiles.Values) entry.Image.Release();
            foreach (var entry in _pictures.Values) entry.Picture.Dispose();
            foreach (var entry in _documents.Values) entry.Document.Dispose();
            _tiles.Clear();
            _pictures.Clear();
            _documents.Clear();
            _tileLru.Clear();
            _pictureLru.Clear();
            _documentLru.Clear();
            _bytes = 0;
            _pictureBytes = 0;
        }
        finally { _worker.Release(); }
        // Do not dispose the semaphore: already queued callers must wake and observe ObjectDisposedException.
    }
}
