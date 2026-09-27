using ProPDF.Core;
using ProPDF.Rendering.Skia;
using SkiaSharp;
using Xunit;

namespace ProPDF.Tests;

public sealed class RenderingContinuityTests
{
    [Theory]
    [InlineData(.75)] [InlineData(1)] [InlineData(1.25)] [InlineData(1.333333333333)] [InlineData(2.1)]
    public async Task TiledRasterKeepsThePageWideSamplingPhase(double scale)
    {
        var factory = new PatternFactory(); var source = Snapshot();
        await using var renderer = new SkiaPdfRenderer(factory, new(MaximumTileEdge: 4096));
        using var full = await renderer.RenderTileAsync(source, new(1, new(0, 0, 900, 600), scale));
        using var tiled = await new PdfRasterExporter(renderer).RenderPageAsync(source, 1, new(Dpi: scale * 72));
        using var a = SKBitmap.FromImage(full.Image); using var b = SKBitmap.FromImage(tiled);
        Assert.Equal(a.Info, b.Info); var differences = 0; var severe = 0;
        for (var y = 0; y < a.Height; y++) for (var x = 0; x < a.Width; x++)
        {
            var p = a.GetPixel(x, y); var q = b.GetPixel(x, y);
            var difference = Math.Max(Math.Abs(p.Red - q.Red), Math.Max(Math.Abs(p.Green - q.Green), Math.Abs(p.Blue - q.Blue)));
            if (difference > 2) differences++; if (difference > 24) severe++;
        }
        if (severe > 0 && Environment.GetEnvironmentVariable("PROPDF_RENDER_DIAGNOSTICS") is { } directory)
        {
            Directory.CreateDirectory(directory);
            using var original = full.Image.Encode(SKEncodedImageFormat.Png, 100); using var assembled = tiled.Encode(SKEncodedImageFormat.Png, 100);
            File.WriteAllBytes(Path.Combine(directory, $"full-{scale}.png"), original.ToArray()); File.WriteAllBytes(Path.Combine(directory, $"tiled-{scale}.png"), assembled.ToArray());
        }
        // Tiny floating-point edge variations are allowed, not discontinuities along tile borders.
        Assert.True(severe == 0 && differences < a.Width * a.Height / 500, $"Scale {scale}: {differences} changed pixels, {severe} severe errors");
    }
    [Theory]
    [InlineData(.75)] [InlineData(1.25)] [InlineData(1.333333333333)] [InlineData(2.1)]
    public async Task VectorAntialiasingDifferencesStaySmallIncludingTileEdges(double scale)
    {
        await using var renderer = new SkiaPdfRenderer(new PatternFactory(vectors: true), new(MaximumTileEdge: 4096));
        var source = Snapshot();
        using var full = await renderer.RenderTileAsync(source, new(1, new(0, 0, 900, 600), scale));
        using var tiled = await new PdfRasterExporter(renderer).RenderPageAsync(source, 1, new(Dpi: scale * 72));
        using var a = SKBitmap.FromImage(full.Image); using var b = SKBitmap.FromImage(tiled);
        Assert.Equal(a.Info, b.Info);
        double squares = 0, edgeAbsoluteError = 0; var changed = 0; long edgeSamples = 0;
        for (var y = 0; y < a.Height; y++) for (var x = 0; x < a.Width; x++)
        {
            var p = a.GetPixel(x, y); var q = b.GetPixel(x, y);
            var red = Math.Abs(p.Red - q.Red); var green = Math.Abs(p.Green - q.Green); var blue = Math.Abs(p.Blue - q.Blue);
            squares += red * red + green * green + blue * blue;
            if (Math.Max(red, Math.Max(green, blue)) > 2) changed++;
            if (x > 509 && (x % 512 < 2 || x % 512 >= 510) || y > 509 && (y % 512 < 2 || y % 512 >= 510))
            { edgeAbsoluteError += red + green + blue; edgeSamples += 3; }
        }
        // Skia's coverage rasterizer can change edge AA when clipping a vector path into tiles.
        // Keep a separate coverage/RMS gate instead of falsely claiming pixel-identical vector AA.
        var pixels = (long)a.Width * a.Height;
        Assert.True(Math.Sqrt(squares / (3 * pixels)) < 1.5, "Excessive whole-page vector raster error.");
        Assert.True(changed < pixels / 100, "Vector differences escaped the narrow antialiased edges.");
        Assert.True(edgeSamples == 0 || edgeAbsoluteError / edgeSamples < .5, "Visible discontinuity at a tile border.");
    }
    [Fact]
    public async Task OversizedDisplayListsAreNotRetainedAndLiveTilesSurvive()
    {
        var factory = new PatternFactory(); var source = Snapshot();
        var renderer = new SkiaPdfRenderer(factory, new(MaximumDisplayListBytes: 1));
        using var first = await renderer.RenderTileAsync(source, new(1, new(0, 0, 100, 100), 1));
        using var second = await renderer.RenderTileAsync(source, new(1, new(100, 0, 100, 100), 1));
        Assert.Equal(2, factory.Recordings);
        var stats = await renderer.GetStatisticsAsync(); Assert.Equal(0, stats.DisplayListCount); Assert.Equal(0, stats.ApproximateDisplayListBytes);
        await renderer.DisposeAsync(); using var bitmap = SKBitmap.FromImage(first.Image); Assert.Equal(100, bitmap.Width);
    }
    [Fact]
    public async Task DisplayListStatisticsAndZeroCacheModeAreBounded()
    {
        var factory = new PatternFactory(); await using var renderer = new SkiaPdfRenderer(factory);
        using var tile = await renderer.RenderTileAsync(Snapshot(), new(1, new(0, 0, 20, 20), 1));
        var stats = await renderer.GetStatisticsAsync(); Assert.Equal(1, stats.DisplayListCount); Assert.True(stats.ApproximateDisplayListBytes > 0);
        Assert.Throws<ArgumentOutOfRangeException>(() => new SkiaPdfRenderer(factory, new(MaximumDisplayListBytes: -1)));
    }
    [Fact]
    public void FloatingPointTileExtentsDoNotAddPhantomPixelColumns()
    {
        var request = new SkiaTileRequest(1, new(0, 0, 512.00000000001 / 1.1, 50), 1.1);
        request.Validate(Snapshot(), 512); Assert.Equal(512, request.PixelWidth);
    }
    private static PdfSnapshot Snapshot() => new("%PDF-fixture"u8, [new(1, new(900, 600))]);
    private sealed class PatternFactory(bool vectors = false) : ISkiaPdfDocumentFactory
    {
        public int Recordings;
        public ISkiaPdfDocument Open(PdfSnapshot snapshot) => new Document(this, vectors);
        private sealed class Document(PatternFactory owner, bool vectors) : ISkiaPdfDocument
        {
            public SKPicture RecordPage(int pageNumber, CancellationToken cancellationToken = default)
            {
                owner.Recordings++;
                using var recorder = new SKPictureRecorder(); var canvas = recorder.BeginRecording(new(0, 0, 900, 600));
                using var bitmap = new SKBitmap(157, 89);
                for (var y = 0; y < bitmap.Height; y++) for (var x = 0; x < bitmap.Width; x++) bitmap.SetPixel(x, y, new((byte)(x * 11 % 256), (byte)(y * 23 % 256), (byte)((x + y) * 7 % 256)));
                using var image = SKImage.FromBitmap(bitmap);
                canvas.DrawImage(image, new SKRect(13.3f, 7.7f, 887.5f, 593.25f), new SKSamplingOptions(SKFilterMode.Linear, SKMipmapMode.None));
                if (vectors) DrawVectors(canvas);
                return recorder.EndRecording();
            }
            private static void DrawVectors(SKCanvas canvas)
            {
                using var paint = new SKPaint { Color = SKColors.Blue, IsAntialias = true, Style = SKPaintStyle.Stroke, StrokeWidth = .7f };
                for (var i = 0; i < 8; i++) canvas.DrawCircle(500 + i * .31f, 290, 45 + i * 17.3f, paint);
                canvas.DrawLine(0, 0, 900, 600, paint);
            }
            public void Dispose() { }
        }
    }
}
