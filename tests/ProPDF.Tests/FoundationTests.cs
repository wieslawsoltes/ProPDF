using System.Text;
using ProPDF.Core;
using ProPDF.Rendering.Skia;
using SkiaSharp;
using Xunit;

namespace ProPDF.Tests;

public sealed class FoundationTests
{
    internal static PdfSnapshot Snapshot(int pages = 1) => new(Encoding.ASCII.GetBytes("%PDF-1.7\nfixture"),
        Enumerable.Range(1, pages).Select(n => new PdfPageInfo(n, new PdfSize(200, 300))));

    [Theory]
    [InlineData(0)] [InlineData(90)] [InlineData(180)] [InlineData(270)] [InlineData(-90)] [InlineData(450)]
    public void CoordinatesRoundTripWithCropAndUserUnit(int rotation)
    {
        var transform = new PdfPageTransform(new PdfRect(17, 23, 200, 300), rotation, 2.5);
        foreach (var point in new[] { new PdfPoint(17, 23), new PdfPoint(100, 150), new PdfPoint(217, 323) })
        {
            var result = transform.ToPdf(transform.ToView(point));
            Assert.Equal(point.X, result.X, 8);
            Assert.Equal(point.Y, result.Y, 8);
        }
        var rectangle = new PdfRect(30, 40, 50, 60);
        var actual = transform.ToPdf(transform.ToView(rectangle));
        Assert.Equal(rectangle.X, actual.X, 8);
        Assert.Equal(rectangle.Y, actual.Y, 8);
        Assert.Equal(rectangle.Width, actual.Width, 8);
        Assert.Equal(rectangle.Height, actual.Height, 8);
    }

    [Theory]
    [InlineData(double.NaN)] [InlineData(double.PositiveInfinity)] [InlineData(-1)] [InlineData(0)]
    public void InvalidPageSizesFail(double width) => Assert.Throws<ArgumentOutOfRangeException>(() => new PdfSize(width, 100));

    [Fact]
    public void SnapshotDefensivelyCopiesInput()
    {
        var bytes = Encoding.ASCII.GetBytes("%PDF-original");
        var pages = new[] { new PdfPageInfo(1, new PdfSize(100, 100)) };
        var snapshot = new PdfSnapshot(bytes, pages, password: "secret");
        bytes[0] = 0;
        pages[0] = new PdfPageInfo(1, new PdfSize(10, 10));
        using var stream = snapshot.OpenRead();
        Assert.Equal((int)'%', stream.ReadByte());
        Assert.False(stream.CanWrite);
        Assert.Equal(100, snapshot.Pages[0].Size.Width);
        Assert.DoesNotContain("secret", snapshot.ToString());
    }

    [Theory]
    [InlineData(PdfLayoutMode.Continuous)] [InlineData(PdfLayoutMode.Facing)]
    public void LargeLayoutsReturnOnlyVisiblePages(PdfLayoutMode mode)
    {
        var pages = Enumerable.Range(1, 10_000).Select(n => new PdfPageInfo(n, new PdfSize(612, n % 2 == 0 ? 400 : 792))).ToArray();
        var layout = PdfPageLayout.Create(pages, 1000, mode: mode);
        var visible = layout.GetVisible(new PdfRect(0, 500_000, 1000, 800)).ToArray();
        Assert.NotEmpty(visible);
        Assert.InRange(visible.Length, 1, 6);
        foreach (var page in visible)
        {
            var hit = layout.HitTest(new PdfPoint(page.Bounds.X + 20, page.Bounds.Y + 20));
            Assert.Equal(page.PageNumber, hit!.Value.PageNumber);
            Assert.Equal(15, hit.Value.Point.X, 8);
        }
    }

    [Fact]
    public async Task BoundedReadRejectsOversizeInput()
    {
        using var stream = new MemoryStream(new byte[100]);
        await Assert.ThrowsAsync<InvalidDataException>(() => PdfStreams.ReadBoundedAsync(stream, 99));
    }

    [Fact]
    public async Task CancellationDoesNotReplaceDestination()
    {
        var path = Path.Combine(Path.GetTempPath(), $"propdf-{Guid.NewGuid():N}.pdf");
        try
        {
            await File.WriteAllTextAsync(path, "original");
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => PdfStreams.SaveAtomicAsync(Snapshot(), path, new CancellationToken(true)));
            Assert.Equal("original", await File.ReadAllTextAsync(path));
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public async Task TransactionsSupportRollbackUndoRedoAndRevisionChecks()
    {
        var backend = new FakeBackend();
        var session = new PdfSession(backend, backend);
        await session.OpenAsync(Stream.Null);
        var initial = session.Current!;
        await session.ApplyAsync(new FakeEdit());
        var edited = session.Current!;
        Assert.NotEqual(initial.Id, edited.Id);
        Assert.True(session.IsDirty);
        await Assert.ThrowsAsync<PdfRevisionConflictException>(() => session.ApplyAsync(new FakeEdit(), initial.Id));
        await Assert.ThrowsAsync<InvalidOperationException>(() => session.ApplyAsync(new FakeEdit(Fail: true)));
        Assert.Same(edited, session.Current);
        Assert.True(await session.UndoAsync());
        Assert.Same(initial, session.Current);
        Assert.False(session.IsDirty);
        Assert.True(await session.RedoAsync());
        Assert.Same(edited, session.Current);
    }

    [Fact]
    public async Task HistoryBudgetIsEnforced()
    {
        var backend = new FakeBackend();
        var session = new PdfSession(backend, backend, maximumHistoryEntries: 1);
        await session.OpenAsync(Stream.Null);
        await session.ApplyAsync(new FakeEdit());
        await session.ApplyAsync(new FakeEdit());
        Assert.True(await session.UndoAsync());
        Assert.False(await session.UndoAsync());
        Assert.True(await session.RedoAsync());
    }

    [Fact]
    public async Task SkiaTilesAreClippedCachedAndRemainValidAfterEviction()
    {
        var factory = new FakeSkiaFactory();
        var renderer = new SkiaPdfRenderer(factory, new SkiaRendererOptions(MaximumTileCacheBytes: 40_000));
        var snapshot = Snapshot();
        var request = new SkiaTileRequest(1, new PdfRect(0, 0, 100, 100), 1);
        using var first = await renderer.RenderTileAsync(snapshot, request);
        using var second = await renderer.RenderTileAsync(snapshot, request);
        Assert.Equal(1, factory.RecordCount);
        using var third = await renderer.RenderTileAsync(snapshot, new SkiaTileRequest(1, new PdfRect(100, 0, 100, 100), 1));
        await renderer.DisposeAsync();
        using var bitmap = SKBitmap.FromImage(first.Image);
        Assert.Equal(SKColors.Red, bitmap.GetPixel(10, 10));
        Assert.Equal(1, factory.DisposeCount);
        await Assert.ThrowsAsync<ObjectDisposedException>(() => renderer.RenderTileAsync(snapshot, request));
    }

    [Fact]
    public async Task OversizedTilesFailBeforeOpeningParser()
    {
        var factory = new FakeSkiaFactory();
        await using var renderer = new SkiaPdfRenderer(factory, new SkiaRendererOptions(MaximumTileEdge: 128));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => renderer.RenderTileAsync(Snapshot(), new SkiaTileRequest(1, new PdfRect(0, 0, 200, 200), 1)));
        Assert.Equal(0, factory.OpenCount);
    }

    private sealed record FakeEdit(bool Fail = false) : IPdfEditOperation
    {
        public string Description => "Test edit";
        public PdfCapability Capability => PdfCapability.Metadata;
    }

    private sealed class FakeBackend : IPdfDocumentLoader, IPdfEditor
    {
        public IReadOnlySet<PdfCapability> Capabilities { get; } = new HashSet<PdfCapability> { PdfCapability.Metadata };
        public Task<PdfSnapshot> OpenAsync(Stream source, PdfOpenOptions? options = null, CancellationToken cancellationToken = default) => Task.FromResult(Snapshot());
        public Task<PdfSnapshot> ApplyAsync(PdfSnapshot source, IReadOnlyList<IPdfEditOperation> operations, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (operations.OfType<FakeEdit>().Any(op => op.Fail)) throw new InvalidOperationException("Deliberate failed transaction");
            return Task.FromResult(Snapshot());
        }
    }

    private sealed class FakeSkiaFactory : ISkiaPdfDocumentFactory
    {
        public int OpenCount;
        public int RecordCount;
        public int DisposeCount;
        public ISkiaPdfDocument Open(PdfSnapshot snapshot) { OpenCount++; return new Document(this); }
        private sealed class Document(FakeSkiaFactory owner) : ISkiaPdfDocument
        {
            public SKPicture RecordPage(int pageNumber, CancellationToken cancellationToken = default)
            {
                owner.RecordCount++;
                using var recorder = new SKPictureRecorder();
                var canvas = recorder.BeginRecording(new SKRect(0, 0, 200, 300));
                using var paint = new SKPaint { Color = SKColors.Red };
                canvas.DrawRect(new SKRect(0, 0, 100, 100), paint);
                return recorder.EndRecording();
            }
            public void Dispose() => owner.DisposeCount++;
        }
    }
}
