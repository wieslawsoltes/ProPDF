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

public sealed class ImagePaintRenderingTests
{
    private readonly PdfPigBackend _reader = new();
    private async Task<PdfSnapshot> Fixture(string content, bool interpolation = false, byte? maskAlpha = null, bool stencil = false, bool groupMask = false)
    {
        var source = await new ManagedPdfEditor(_reader).CreateAsync(size: new(240, 180));
        using var input = source.OpenRead(); using var buffer = new MemoryStream(); input.CopyTo(buffer);
        var file = PdfFile.Open(buffer.ToArray());
        var page = file.Dictionary(file.Array(file.Dictionary(file.Catalog["Pages"])["Kids"])[0]);
        var dictionary = Dictionary(("Type", new PdfName("XObject")), ("Subtype", new PdfName("Image")),
            ("Width", new PdfNumber(2)), ("Height", new PdfNumber(1)), ("BitsPerComponent", new PdfNumber(stencil ? 1 : 8)),
            ("Interpolate", new PdfBoolean(interpolation)));
        if (stencil) dictionary["ImageMask"] = new PdfBoolean(true);
        else dictionary["ColorSpace"] = new PdfName("DeviceRGB");
        if (maskAlpha is { } alpha) dictionary["SMask"] = file.Add(PdfStream.FromDecoded(new[] { alpha, alpha },
            Dictionary(("Type", new PdfName("XObject")), ("Subtype", new PdfName("Image")), ("Width", new PdfNumber(2)),
            ("Height", new PdfNumber(1)), ("BitsPerComponent", new PdfNumber(8)), ("ColorSpace", new PdfName("DeviceGray")))));
        var image = file.Add(PdfStream.FromDecoded(stencil ? new byte[] { 0 } : new byte[] { 255, 0, 0, 0, 0, 255 }, dictionary));
        var half = Dictionary(("ca", new PdfNumber(.5)), ("CA", new PdfNumber(.5)));
        if (groupMask)
        {
            // An opaque half-gray luminosity mask; /ca and this mask must each apply once.
            var group = file.Add(PdfStream.FromDecoded("0.5 g 0 0 240 180 re f"u8.ToArray(),
                Dictionary(("Type", new PdfName("XObject")), ("Subtype", new PdfName("Form")), ("BBox", Numbers(0, 0, 240, 180)),
                    ("Resources", new PdfDictionary()), ("Group", Dictionary(("S", new PdfName("Transparency")), ("CS", new PdfName("DeviceGray")))))));
            half["SMask"] = Dictionary(("S", new PdfName("Luminosity")), ("G", group));
        }
        page["Resources"] = Dictionary(("XObject", Dictionary(("I", image))), ("ExtGState", Dictionary(("Half", half),
            ("None", Dictionary(("ca", new PdfNumber(0)))), ("Multiply", Dictionary(("BM", new PdfName("Multiply")))))));
        page["Contents"] = file.Add(PdfStream.FromDecoded(Encoding.ASCII.GetBytes(content)));
        using var result = new MemoryStream(file.Save()); return await _reader.OpenAsync(result);
    }
    private async Task<SKBitmap> Raster(PdfSnapshot source, int tile = 512, double dpi = 72)
    {
        await using var renderer = new SkiaPdfRenderer(_reader);
        var size = source.GetPage(1).Size; var scale = dpi / 72;
        var width = (int)Math.Ceiling(size.Width * scale); var height = (int)Math.Ceiling(size.Height * scale);
        using var surface = SKSurface.Create(new SKImageInfo(width, height));
        surface.Canvas.Clear(SKColors.White);
        for (var y = 0; y < height; y += tile) for (var x = 0; x < width; x += tile)
        {
            using var part = await renderer.RenderTileAsync(source, new(1,
                new(x / scale, y / scale, Math.Min(tile / scale, size.Width - x / scale), Math.Min(tile / scale, size.Height - y / scale)), scale));
            surface.Canvas.DrawImage(part.Image, x, y);
        }
        using var image = surface.Snapshot(); return SKBitmap.FromImage(image);
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public async Task ReusedImagePaintsRetainPerInvocationAlphaAndBlend(bool reverse)
    {
        var first = "q /Half gs 80 0 0 40 10 120 cm /I Do Q ";
        var second = "q 80 0 0 40 130 120 cm /I Do Q ";
        var source = await Fixture((reverse ? second + first : first + second) + "q /None gs 80 0 0 40 10 50 cm /I Do Q");
        using var bitmap = await Raster(source);
        var transparentRed = bitmap.GetPixel(20, 40); Assert.Equal(255, transparentRed.Red); Assert.InRange((int)transparentRed.Green, 126, 128);
        Assert.Equal(SKColors.Red, bitmap.GetPixel(140, 40));
        Assert.Equal(SKColors.White, bitmap.GetPixel(20, 100));
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public async Task ConstantAlphaMultipliesImageSoftMaskOnce(bool stencil)
    {
        var source = await Fixture("q 1 0 0 rg /Half gs 80 0 0 40 10 120 cm /I Do Q", maskAlpha: stencil ? null : (byte)128, stencil: stencil);
        using var bitmap = await Raster(source); var pixel = bitmap.GetPixel(20, 40);
        Assert.Equal(255, pixel.Red); Assert.InRange((int)pixel.Green, stencil ? 126 : 189, stencil ? 128 : 193);
    }

    [Fact]
    public async Task GraphicsStateLuminosityMaskAndImageAlphaApplyOnce()
    {
        var source = await Fixture("q /Half gs 80 0 0 40 10 120 cm /I Do Q", groupMask: true);
        using var bitmap = await Raster(source); var pixel = bitmap.GetPixel(20, 40);
        Assert.Equal(255, pixel.Red); Assert.InRange((int)pixel.Green, 185, 197);
    }

    [Fact]
    public async Task ImageBlendModeUsesBackdropAndDoesNotLeakToNextInvocation()
    {
        var source = await Fixture("0 1 0 rg 0 0 240 180 re f q /Multiply gs 80 0 0 40 10 120 cm /I Do Q q 80 0 0 40 130 120 cm /I Do Q");
        using var bitmap = await Raster(source); Assert.Equal(SKColors.Black, bitmap.GetPixel(20, 40)); Assert.Equal(SKColors.Red, bitmap.GetPixel(140, 40));
    }

    [Fact]
    public async Task ImageInterpolationChangesSamplingNotStoredPixelData()
    {
        const string content = "q 100 0 0 80 20 70 cm /I Do Q";
        using var nearest = await Raster(await Fixture(content));
        using var linear = await Raster(await Fixture(content, interpolation: true));
        Assert.Equal(SKColors.Red, nearest.GetPixel(60, 60));
        var blended = linear.GetPixel(60, 60);
        Assert.InRange((int)blended.Red, 150, 210); Assert.InRange((int)blended.Blue, 45, 105); Assert.Equal(0, blended.Green);
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public async Task AlphaAndInterpolationRemainContinuousAcrossFractionalTiles(bool interpolation)
    {
        var source = await Fixture("q /Half gs 200 0 0 120 10 30 cm /I Do Q", interpolation);
        using var single = await Raster(source, 1024, 111);
        using var tiled = await Raster(source, 64, 111);
        Assert.Equal(single.Width, tiled.Width); Assert.Equal(single.Height, tiled.Height);
        var differences = 0;
        for (var y = 0; y < single.Height; y++) for (var x = 0; x < single.Width; x++)
        {
            var a = single.GetPixel(x, y); var b = tiled.GetPixel(x, y);
            if (Math.Abs(a.Red - b.Red) > 2 || Math.Abs(a.Green - b.Green) > 2 || Math.Abs(a.Blue - b.Blue) > 2) differences++;
        }
        Assert.Equal(0, differences);
    }
}
