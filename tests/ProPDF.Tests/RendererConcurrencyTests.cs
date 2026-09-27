using ProPDF.Core;
using ProPDF.Rendering.Skia;
using SkiaSharp;
using Xunit;

namespace ProPDF.Tests;

public sealed class RendererConcurrencyTests
{
    private static PdfSnapshot Snapshot() => new("%PDF-renderer-fixture"u8,
        [new(1, new(100, 100)), new(2, new(100, 100))]);
    private static SkiaTileRequest Request(int page = 1, double x = 0) => new(page, new(x, 0, 10, 10), 1);

    [Fact]
    public async Task WarmTileCompletesWhileAnotherPageIsBlockedInTheInterpreter()
    {
        using var factory = new BlockingFactory(2);
        await using var renderer = new SkiaPdfRenderer(factory);
        var document = Snapshot();
        using var initial = await renderer.RenderTileAsync(document, Request());
        var cold = renderer.RenderTileAsync(document, Request(2));
        try
        {
            await factory.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            var warm = renderer.RenderTileAsync(document, Request());
            // This is a deterministic nonblocking gate, not a machine-speed assertion.
            Assert.True(warm.IsCompletedSuccessfully);
            using var hit = await warm;
            Assert.Same(initial.Image, hit.Image);
            Assert.False(cold.IsCompleted);
        }
        finally { factory.Release.Set(); using var tile = await cold; }
        var stats = await renderer.GetStatisticsAsync();
        Assert.Equal(1, stats.CacheHits); Assert.Equal(2, stats.CacheMisses);
        Assert.Equal(1, factory.MaximumConcurrentRecordings);
    }

    [Fact]
    public async Task ConcurrentIdenticalMissesProduceOneRasterAndIndependentLeases()
    {
        using var factory = new BlockingFactory(1);
        var renderer = new SkiaPdfRenderer(factory);
        var document = Snapshot();
        var pending = Enumerable.Range(0, 32).Select(_ => renderer.RenderTileAsync(document, Request())).ToArray();
        await factory.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        factory.Release.Set();
        var tiles = await Task.WhenAll(pending);
        try
        {
            var stats = await renderer.GetStatisticsAsync();
            Assert.Equal(1, stats.CacheMisses); Assert.Equal(31, stats.CacheHits);
            Assert.Equal(1, factory.Recordings);
            await renderer.DisposeAsync();
            foreach (var tile in tiles) { using var pixels = SKBitmap.FromImage(tile.Image); Assert.Equal(SKColors.Red, pixels.GetPixel(2, 2)); }
        }
        finally { foreach (var tile in tiles) tile.Dispose(); await renderer.DisposeAsync(); }
    }

    [Fact]
    public async Task CancelledWarmRequestsDoNotAcquireALeaseOrChangeCounters()
    {
        using var factory = new BlockingFactory(0);
        await using var renderer = new SkiaPdfRenderer(factory);
        var document = Snapshot(); using var tile = await renderer.RenderTileAsync(document, Request());
        using var cancellation = new CancellationTokenSource(); cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => renderer.RenderTileAsync(document, Request(), cancellation.Token));
        Assert.Equal(0, (await renderer.GetStatisticsAsync()).CacheHits);
    }

    [Fact]
    public async Task FastHitsUpdateLruAndEvictionDoesNotInvalidateHeldImages()
    {
        using var factory = new BlockingFactory(0);
        await using var renderer = new SkiaPdfRenderer(factory, new(MaximumTileCacheBytes: 800));
        var document = Snapshot();
        using var a = await renderer.RenderTileAsync(document, Request(x: 0));
        using var b = await renderer.RenderTileAsync(document, Request(x: 10));
        using var aHit = await renderer.RenderTileAsync(document, Request(x: 0));
        using var c = await renderer.RenderTileAsync(document, Request(x: 20));
        using var aAgain = await renderer.RenderTileAsync(document, Request(x: 0));
        Assert.Same(a.Image, aAgain.Image);
        using var bAgain = await renderer.RenderTileAsync(document, Request(x: 10));
        Assert.NotSame(b.Image, bAgain.Image);
        using var pixels = SKBitmap.FromImage(b.Image); Assert.Equal(SKColors.Red, pixels.GetPixel(0, 0));
        var stats = await renderer.GetStatisticsAsync(); Assert.Equal(800, stats.TileBytes); Assert.Equal(2, stats.TileCount);
    }

    [Fact]
    public async Task DisposedRendererRejectsWarmRequestsButRetainedLeaseStillDraws()
    {
        using var factory = new BlockingFactory(0);
        var renderer = new SkiaPdfRenderer(factory); var document = Snapshot();
        using var tile = await renderer.RenderTileAsync(document, Request());
        using var held = tile.Retain();
        await renderer.DisposeAsync();
        await Assert.ThrowsAsync<ObjectDisposedException>(() => renderer.RenderTileAsync(document, Request()));
        using var pixels = SKBitmap.FromImage(held.Image); Assert.Equal(SKColors.Red, pixels.GetPixel(0, 0));
        var stats = await renderer.GetStatisticsAsync(); Assert.Equal(0, stats.TileBytes); Assert.Equal(0, stats.TileCount);
    }

    [Theory]
    [InlineData(0)] [InlineData(1)]
    public async Task ZeroOrUndersizedCacheReleasesOwnershipWithoutLosingTheReturnedTile(long budget)
    {
        using var factory = new BlockingFactory(0);
        await using var renderer = new SkiaPdfRenderer(factory, new(MaximumTileCacheBytes: budget));
        var document = Snapshot();
        using var a = await renderer.RenderTileAsync(document, Request());
        using var b = await renderer.RenderTileAsync(document, Request());
        Assert.NotSame(a.Image, b.Image);
        var stats = await renderer.GetStatisticsAsync(); Assert.Equal(0, stats.TileCount); Assert.Equal(2, stats.CacheMisses);
        using var pixels = SKBitmap.FromImage(a.Image); Assert.Equal(SKColors.Red, pixels.GetPixel(0, 0));
    }

    private sealed class BlockingFactory(int blockedPage) : ISkiaPdfDocumentFactory, IDisposable
    {
        private readonly int _blockedPage = blockedPage;
        public readonly TaskCompletionSource Entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public readonly ManualResetEventSlim Release = new();
        public int Recordings, MaximumConcurrentRecordings;
        private int _active;
        public ISkiaPdfDocument Open(PdfSnapshot snapshot) => new Document(this);
        public void Dispose() { Release.Set(); Release.Dispose(); }
        private sealed class Document(BlockingFactory owner) : ISkiaPdfDocument
        {
            public SKPicture RecordPage(int pageNumber, CancellationToken cancellationToken = default)
            {
                var active = Interlocked.Increment(ref owner._active);
                owner.MaximumConcurrentRecordings = Math.Max(owner.MaximumConcurrentRecordings, active);
                Interlocked.Increment(ref owner.Recordings);
                try
                {
                    if (pageNumber == owner._blockedPage)
                    {
                        owner.Entered.TrySetResult();
                        if (!owner.Release.Wait(TimeSpan.FromSeconds(10), cancellationToken)) throw new TimeoutException("Test interpreter was not released.");
                    }
                    using var recorder = new SKPictureRecorder();
                    var canvas = recorder.BeginRecording(new(0, 0, 100, 100));
                    canvas.Clear(SKColors.Red); return recorder.EndRecording();
                }
                finally { Interlocked.Decrement(ref owner._active); }
            }
            public void Dispose() { }
        }
    }
}
