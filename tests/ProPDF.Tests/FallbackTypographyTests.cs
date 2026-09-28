using System.Text;
using ProPDF.Core;
using ProPDF.Editing;
using ProPDF.Engine.PdfPig;
using ProPDF.Kernel;
using ProPDF.Rendering.Skia;
using SkiaSharp;
using Xunit;
using static ProPDF.Kernel.PdfValues;

namespace ProPDF.Tests;

public sealed class FallbackTypographyTests
{
    private static SKTypeface Synthetic()
    {
        using var data = SKData.CreateCopy(FontEmbeddingTests.TinyTrueType.Create());
        return SKTypeface.FromData(data) ?? throw new InvalidOperationException("Synthetic test font did not load.");
    }

    [Fact]
    public void ShapesAtDesignPrecisionAndCachesRepeatedUnicodeMappings()
    {
        using var face = Synthetic(); using var cache = new PdfFallbackGlyphCache(face);
        for (var i = 0; i < 1000; i++)
        {
            using var glyph = cache.Acquire("A");
            Assert.InRange(Math.Abs(glyph.Advance - .6), 0, 1d / 512);
            var bounds = glyph.Path!.Bounds;
            Assert.InRange(Math.Abs(bounds.Left - .05), 0, .0001); Assert.InRange(Math.Abs(bounds.Right - .5), 0, .0001);
            Assert.InRange(Math.Abs(bounds.Top), 0, .0001); Assert.InRange(Math.Abs(bounds.Bottom - .7), 0, .0001);
        }
        Assert.Equal((1, 999L, 1L), (cache.Statistics.Entries, cache.Statistics.Hits, cache.Statistics.Misses));
    }

    [Theory]
    [InlineData("AΩ", 1.2)] [InlineData("😀", .6)] [InlineData("A😀A", 1.8)]
    public void CompleteUnicodeMappingsHaveOneCachedOutline(string value, double advance)
    {
        using var face = Synthetic(); using var cache = new PdfFallbackGlyphCache(face);
        using var glyph = cache.Acquire(value);
        Assert.InRange(Math.Abs(glyph.Advance - advance), 0, value.EnumerateRunes().Count() / 512d); Assert.NotNull(glyph.Path); Assert.False(glyph.Path!.IsEmpty);
    }

    [Fact]
    public void LruEvictionAndCacheDisposalCannotInvalidateAnActiveLease()
    {
        using var face = Synthetic(); var cache = new PdfFallbackGlyphCache(face, 1);
        using var a = cache.Acquire("A");
        using (var second = cache.Acquire("Ω")) Assert.NotSame(a.Path, second.Path);
        Assert.Equal(1, cache.Statistics.Entries);
        cache.Dispose(); cache.Dispose();
        Assert.False(a.Path!.IsEmpty); Assert.InRange(Math.Abs(a.Advance - .6), 0, 1d / 512);
        Assert.Throws<ObjectDisposedException>(() => cache.Acquire("A"));
        a.Dispose(); a.Dispose(); Assert.Throws<ObjectDisposedException>(() => a.Path);
    }

    [Fact]
    public void OversizedOutlinesAreTransientAndDoNotExceedTheBudget()
    {
        using var face = Synthetic(); using var cache = new PdfFallbackGlyphCache(face, 16, 1);
        using var glyph = cache.Acquire("A"); Assert.False(glyph.Path!.IsEmpty);
        Assert.Equal(0, cache.Statistics.Entries); Assert.Equal(0, cache.Statistics.Bytes);
    }

    [Fact]
    public async Task ConcurrentAcquisitionBuildsTheSameGlyphOnlyOnce()
    {
        using var face = Synthetic(); using var cache = new PdfFallbackGlyphCache(face);
        await Task.WhenAll(Enumerable.Range(0, 64).Select(_ => Task.Run(() =>
        { using var glyph = cache.Acquire("A"); Assert.InRange(Math.Abs(glyph.Advance - .6), 0, 1d / 512); })));
        Assert.Equal(1, cache.Statistics.Misses); Assert.Equal(63, cache.Statistics.Hits);
    }

    [Fact]
    public void CacheBoundsInputsAndSeparatesVerticalFromHorizontalShaping()
    {
        using var face = Synthetic(); using var cache = new PdfFallbackGlyphCache(face);
        using var horizontal = cache.Acquire("A"); using var vertical = cache.Acquire("A", true);
        Assert.NotSame(horizontal.Path, vertical.Path); Assert.Equal(2, cache.Statistics.Entries);
        Assert.Throws<NotSupportedException>(() => cache.Acquire(new string('A', 1025)));
        Assert.Throws<ArgumentOutOfRangeException>(() => new PdfFallbackGlyphCache(face, -1));
        Assert.Throws<ArgumentOutOfRangeException>(() => new PdfFallbackGlyphCache(face, 1, -1));
    }

    [Theory]
    [InlineData(.1, .6f, false, 1d/6)] [InlineData(.9, .6f, false, 1.5)]
    [InlineData(0, .6f, false, 1)] [InlineData(-1, .6f, false, 1)]
    [InlineData(.6, 0f, false, 1)] [InlineData(.1, .6f, true, 1)]
    [InlineData(double.NaN, .6f, false, 1)]
    public void WidthFittingDoesNotModifyVerticalOrZeroAdvanceGlyphs(double pdfWidth, float substituteWidth, bool vertical, double expected)
        => Assert.Equal(expected, PdfFallbackGlyphCache.HorizontalScale(pdfWidth, substituteWidth, vertical), 5);

    [Fact]
    public void MalformedWidthScaleIsRejectedInsteadOfAllocatingUnboundedGeometry()
        => Assert.Throws<InvalidDataException>(() => PdfFallbackGlyphCache.HorizontalScale(1e20, 1, false));

    [Theory]
    [InlineData(0)] [InlineData(1)] [InlineData(2)]
    public async Task FallbackInkUsesPdfWidthsForFillStrokeAndCombinedModes(int mode)
    {
        var source = await Fixture($"{mode} Tr 0.4 w BT /N 40 Tf 1 0 0 1 20 130 Tm (M) Tj ET BT /W 40 Tf 1 0 0 1 20 55 Tm (M) Tj ET");
        using var bitmap = await Raster(source);
        var narrow = Ink(bitmap, new(0, 0, 200, 140)); var wide = Ink(bitmap, new(0, 150, 200, 330));
        Assert.InRange(narrow.Width, 4, 25); Assert.InRange(wide.Width, 30, 80);
        Assert.InRange((double)wide.Width / narrow.Width, 3, 5);
        Assert.InRange(Math.Abs(wide.Height - narrow.Height), 0, 2);
    }

    [Fact]
    public async Task CharacterSpacingAndTjAdjustmentsMoveThePenWithoutRescalingInk()
    {
        using var bitmap = await Raster(await Fixture("BT /N 40 Tf 2 Tc 1 0 0 1 20 130 Tm [(M) -100 (M)] TJ ET"));
        var occupied = new List<int>();
        for (var x = 0; x < bitmap.Width; x++)
            if (Enumerable.Range(0, 140).Any(y => bitmap.GetPixel(x, y).Red < 100)) occupied.Add(x);
        var starts = occupied.Where((x, i) => i == 0 || x != occupied[i-1] + 1).ToArray();
        Assert.Equal(2, starts.Length);
        // (0.2 * 40 + 2 character spacing + 0.1 * 40 TJ) * two pixels per point.
        Assert.Equal(28, starts[1] - starts[0]);
    }

    [Fact]
    public async Task TextClipUsesTheSameFittedOutlineAsVisibleText()
    {
        using var painted = await Raster(await Fixture("BT /N 40 Tf 1 0 0 1 20 130 Tm (M) Tj ET"));
        using var clipped = await Raster(await Fixture("q BT /N 40 Tf 7 Tr 1 0 0 1 20 130 Tm (M) Tj ET 0 g 0 0 240 180 re f Q"));
        var a = Ink(painted, new(0,0,200,140)); var b = Ink(clipped, new(0,0,200,140));
        Assert.InRange(Math.Abs(a.Left-b.Left),0,1); Assert.InRange(Math.Abs(a.Right-b.Right),0,1);
        Assert.InRange(Math.Abs(a.Top-b.Top),0,1); Assert.InRange(Math.Abs(a.Bottom-b.Bottom),0,1);
    }

    [Fact]
    public async Task FallbackRenderingNeverRewritesDocumentBytesOrExtraction()
    {
        var source = await Fixture("BT /N 40 Tf 1 0 0 1 20 130 Tm (M) Tj ET");
        var before = source.OpenRead(); using var buffer = new MemoryStream(); await before.CopyToAsync(buffer); before.Dispose();
        using var raster = await Raster(source);
        using var after = source.OpenRead(); using var copy = new MemoryStream(); await after.CopyToAsync(copy);
        Assert.Equal(buffer.ToArray(), copy.ToArray()); Assert.Equal("M", (await new PdfPigBackend().GetPageTextAsync(source,1)).Text);
    }

    private static SKRectI Ink(SKBitmap bitmap, SKRectI bounds)
    {
        int l = bounds.Right, t = bounds.Bottom, r = -1, b = -1;
        for (var y = bounds.Top; y < bounds.Bottom; y++) for (var x = bounds.Left; x < bounds.Right; x++)
        { if (bitmap.GetPixel(x,y).Red < 100) { l=Math.Min(l,x);t=Math.Min(t,y);r=Math.Max(r,x);b=Math.Max(b,y); } }
        Assert.True(r>=l && b>=t, "Expected substitute glyph ink."); return new(l,t,r+1,b+1);
    }

    private static async Task<PdfSnapshot> Fixture(string content)
    {
        var reader = new PdfPigBackend(); var source = await new ManagedPdfEditor(reader).CreateAsync(size: new(240,180));
        using var input = source.OpenRead(); using var data = new MemoryStream(); input.CopyTo(data);
        var file = PdfFile.Open(data.ToArray()); var page = file.Dictionary(file.Array(file.Dictionary(file.Catalog["Pages"])["Kids"])[0]);
        PdfReference Font(int width) => file.Add(Dictionary(("Type",new PdfName("Font")),("Subtype",new PdfName("Type1")),
            ("BaseFont",new PdfName("ProPDF-Unavailable-Synthetic-Name")),("Encoding",new PdfName("WinAnsiEncoding")),
            ("FirstChar",new PdfNumber(32)),("LastChar",new PdfNumber(126)),("Widths",Numbers(Enumerable.Repeat((double)width,95).ToArray())),
            ("FontDescriptor",file.Add(Dictionary(("Type",new PdfName("FontDescriptor")),("FontName",new PdfName("ProPDF-Unavailable-Synthetic-Name")),
                ("Flags",new PdfNumber(32)),("FontBBox",Numbers(0,-200,1000,900)),("Ascent",new PdfNumber(800)),("Descent",new PdfNumber(-200)),
                ("CapHeight",new PdfNumber(700)),("ItalicAngle",new PdfNumber(0)),("StemV",new PdfNumber(80)))))));
        page["Resources"] = Dictionary(("Font",Dictionary(("N",Font(200)),("W",Font(800)))));
        page["Contents"] = file.Add(PdfStream.FromDecoded(Encoding.ASCII.GetBytes(content)));
        using var result = new MemoryStream(file.Save()); return await reader.OpenAsync(result);
    }
    private static async Task<SKBitmap> Raster(PdfSnapshot source)
    {
        await using var renderer = new SkiaPdfRenderer(new PdfPigBackend());
        using var tile = await renderer.RenderTileAsync(source,new(1,new(0,0,240,180),2));
        return SKBitmap.FromImage(tile.Image);
    }
}
