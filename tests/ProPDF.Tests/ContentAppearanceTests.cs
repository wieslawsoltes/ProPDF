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

public sealed class ContentAppearanceTests
{
    private readonly PdfPigBackend _reader = new();
    private ManagedPdfEditor Editor => new(_reader);
    private static byte[] Bytes(PdfSnapshot source) { using var input = source.OpenRead(); using var memory = new MemoryStream(); input.CopyTo(memory); return memory.ToArray(); }
    private static PdfDictionary Page(PdfFile file) => file.Dictionary(file.Array(file.Dictionary(file.Catalog["Pages"])["Kids"])[0]);
    private async Task<PdfSnapshot> Content(string content, Action<PdfFile, PdfDictionary>? configure = null)
    {
        var source = await Editor.CreateAsync(size: new(240, 240));
        var file = PdfFile.Open(Bytes(source)); var page = Page(file);
        var font = file.Add(Dictionary(("Type", new PdfName("Font")), ("Subtype", new PdfName("Type1")),
            ("BaseFont", new PdfName("Helvetica")), ("Encoding", new PdfName("WinAnsiEncoding"))));
        page["Resources"] = Dictionary(("Font", Dictionary(("F1", font))));
        page["Contents"] = file.Add(PdfStream.FromDecoded(Encoding.ASCII.GetBytes(content)));
        configure?.Invoke(file, page);
        using var input = new MemoryStream(file.Save()); return await _reader.OpenAsync(input);
    }
    private async Task<SKBitmap> Raster(PdfSnapshot source)
    {
        await using var renderer = new SkiaPdfRenderer(_reader);
        using var image = await new PdfRasterExporter(renderer).RenderPageAsync(source, 1, new(Dpi: 72));
        return SKBitmap.FromImage(image);
    }
    private async Task<PdfContentObject> Object(PdfSnapshot source, int index = 0) => (await Editor.ReadPageContentAsync(source, 1)).Objects[index];
    private static IReadOnlyList<PdfContentInstruction> Instructions(PdfSnapshot source)
    {
        var file = PdfFile.Open(Bytes(source));
        return PdfContent.Read(file.Decode((PdfStream)file.Resolve(Page(file)["Contents"])));
    }

    [Fact]
    public async Task TextColorChangesEveryShowButKeepsFontsPositionsAndFollowingState()
    {
        var source = await Content("BT /F1 24 Tf 1 0 0 1 25 180 Tm 0 1 0 rg (One) Tj 0 0 1 rg [(Two)] TJ ET BT /F1 20 Tf 1 0 0 1 25 60 Tm (After) Tj ET");
        var beforeText = await _reader.GetPageTextAsync(source, 1);
        var result = await Editor.ApplyAsync(source, [new SetContentAppearance((await Object(source)).Reference, new(FillColor: new(255, 0, 0)))]);
        var afterText = await _reader.GetPageTextAsync(result, 1);
        Assert.Equal(beforeText.Text, afterText.Text);
        Assert.Equal(beforeText.Words.Select(w => w.Bounds), afterText.Words.Select(w => w.Bounds));
        using var before = await Raster(source); using var after = await Raster(result);
        var red = 0;
        for (var y = 30; y < 85; y++) for (var x = 10; x < 190; x++)
        { var color = after.GetPixel(x, y); if (color.Red > 240 && color.Green < 20 && color.Blue < 20) red++; }
        Assert.True(red > 150, $"Only {red} red text pixels.");
        for (var y = 150; y < 220; y++) for (var x = 0; x < 240; x++) Assert.Equal(before.GetPixel(x, y), after.GetPixel(x, y));
        Assert.DoesNotContain(Instructions(result), i => i.Operator == "Do"); // Text was not rasterized.
    }

    [Fact]
    public async Task EvenOddFillRuleAndStrokeColorSurvivePaintingModeChange()
    {
        var source = await Content("1 0 0 rg 30 30 180 180 re 80 80 80 80 re f*");
        var result = await Editor.ApplyAsync(source, [new SetContentAppearance((await Object(source)).Reference,
            new(FillColor: new(0, 0, 255), StrokeColor: new(0, 255, 0), StrokeWidth: 4, PathPaint: PdfPathPaintMode.FillAndStroke))]);
        using var pixels = await Raster(result);
        Assert.Equal(SKColors.White, pixels.GetPixel(120, 120));
        Assert.Equal(SKColors.Blue, pixels.GetPixel(55, 55));
        Assert.Equal(SKColors.Lime, pixels.GetPixel(30, 120));
        Assert.Contains(Instructions(result), i => i.Operator == "B*");
    }

    [Theory]
    [InlineData("s")] [InlineData("b")] [InlineData("b*")]
    public async Task ExplicitPathClosureIsRetained(string paint)
    {
        var source = await Content("20 20 m 160 20 l 160 160 l " + paint);
        var result = await Editor.ApplyAsync(source, [new SetContentAppearance((await Object(source)).Reference,
            new(StrokeWidth: 3, PathPaint: PdfPathPaintMode.Stroke))]);
        var operations = Instructions(result).Select(i => i.Operator).ToArray();
        var stroke = Array.IndexOf(operations, "S"); Assert.True(stroke > 0); Assert.Equal("h", operations[stroke - 1]);
    }

    [Theory]
    [InlineData(PdfPathPaintMode.Fill)] [InlineData(PdfPathPaintMode.Stroke)] [InlineData(PdfPathPaintMode.FillAndStroke)]
    public async Task PathPaintingModesProduceTheirNativeOperators(PdfPathPaintMode mode)
    {
        var source = await Content("20 20 100 100 re f");
        var result = await Editor.ApplyAsync(source, [new SetContentAppearance((await Object(source)).Reference,
            new(FillColor: new(255, 0, 0), StrokeColor: new(0, 0, 255), StrokeWidth: 4, PathPaint: mode))]);
        using var pixels = await Raster(result);
        Assert.Equal(mode == PdfPathPaintMode.Stroke ? SKColors.White : SKColors.Red, pixels.GetPixel(70, 170));
        Assert.Contains(Instructions(result), i => i.Operator == (mode == PdfPathPaintMode.Fill ? "f" : mode == PdfPathPaintMode.Stroke ? "S" : "B"));
    }

    [Fact]
    public async Task DashCapJoinAndWidthAreLocalToTheSelectedPath()
    {
        var source = await Content("0 G 12 w 20 120 m 220 120 l S 20 60 m 220 60 l S");
        var result = await Editor.ApplyAsync(source, [new SetContentAppearance((await Object(source)).Reference,
            new(StrokeColor: new(255, 0, 0), StrokeWidth: 6, LineCap: PdfLineCap.Round, LineJoin: PdfLineJoin.Bevel, Dash: new([10, 10])))]);
        using var before = await Raster(source); using var after = await Raster(result);
        Assert.Equal(SKColors.Red, after.GetPixel(25, 120)); Assert.Equal(SKColors.White, after.GetPixel(35, 120));
        for (var y = 165; y < 200; y++) for (var x = 0; x < 240; x++) Assert.Equal(before.GetPixel(x, y), after.GetPixel(x, y));
        Assert.Contains(Instructions(result), i => i.Operator == "J" && ((PdfNumber)i.Operands[0]).Value == 1);
        Assert.Contains(Instructions(result), i => i.Operator == "j" && ((PdfNumber)i.Operands[0]).Value == 2);
    }

    [Theory]
    [InlineData(0, 255)] [InlineData(.5, 127)] [InlineData(1, 0)]
    public async Task OpacityChangesSelectedPaintNotItsNeighbor(double opacity, int green)
    {
        var source = await Content("1 0 0 rg 20 140 60 60 re f 140 140 60 60 re f");
        var result = await Editor.ApplyAsync(source, [new SetContentAppearance((await Object(source)).Reference, new(Opacity: opacity))]);
        using var pixels = await Raster(result);
        var color = pixels.GetPixel(50, 70); Assert.Equal(255, color.Red); Assert.InRange((int)color.Green, Math.Max(0, green - 1), green + 1);
        Assert.Equal(SKColors.Red, pixels.GetPixel(160, 70));
    }

    [Fact]
    public async Task ColorAlphaMultipliesExplicitOpacity()
    {
        var source = await Content("20 20 80 80 re f");
        var result = await Editor.ApplyAsync(source, [new SetContentAppearance((await Object(source)).Reference,
            new(FillColor: new(255, 0, 0, 64), Opacity: .5))]);
        using var pixels = await Raster(result); var color = pixels.GetPixel(50, 180);
        Assert.Equal(255, color.Red); Assert.InRange((int)color.Green, 222, 224);
    }

    public static IEnumerable<object[]> BlendModes() => Enum.GetValues<PdfBlendMode>().Select(m => new object[] { m });
    [Theory]
    [MemberData(nameof(BlendModes))]
    public async Task BlendModesRoundTripAndRender(PdfBlendMode mode)
    {
        var source = await Content("0 1 0 rg 20 140 100 60 re f 1 0 0 rg 40 150 60 30 re f");
        var result = await Editor.ApplyAsync(source, [new SetContentAppearance((await Object(source, 1)).Reference, new(BlendMode: mode))]);
        var file = PdfFile.Open(Bytes(result)); var gs = file.Dictionary(file.Dictionary(Page(file)["Resources"])["ExtGState"]);
        Assert.Contains(gs, item => file.Dictionary(item.Value).Name("BM") == mode.ToString());
        using var pixels = await Raster(result);
        Assert.Equal(SKColors.Lime, pixels.GetPixel(25, 60));
        if (mode == PdfBlendMode.Multiply) Assert.Equal(SKColors.Black, pixels.GetPixel(65, 70));
        if (mode == PdfBlendMode.Screen) Assert.Equal(SKColors.Yellow, pixels.GetPixel(65, 70));
    }

    [Fact]
    public async Task RepeatedEditsDoNotLeakStyleToFollowingText()
    {
        var source = await Content("BT /F1 20 Tf 0 0 1 rg 1 0 0 1 20 190 Tm (First) Tj ET BT 1 0 0 1 20 60 Tm (After) Tj ET");
        using var before = await Raster(source);
        var styled = await Editor.ApplyAsync(source, [new SetContentAppearance((await Object(source)).Reference, new(FillColor: new(255, 0, 0)))]);
        var result = await Editor.ApplyAsync(styled, [new TransformContentObject((await Object(styled)).Reference, PdfAffineTransform.Translation(20, 20))]);
        using var after = await Raster(result);
        for (var y = 150; y < 220; y++) for (var x = 0; x < 240; x++) Assert.Equal(before.GetPixel(x, y), after.GetPixel(x, y));
        Assert.Contains("After", (await _reader.GetPageTextAsync(result, 1)).Text);
    }

    [Theory]
    [InlineData(double.NaN)] [InlineData(double.PositiveInfinity)] [InlineData(-.1)] [InlineData(1.1)]
    public async Task InvalidOpacityFailsAtomically(double value)
    {
        var source = await Content("20 20 80 80 re f"); var session = new PdfSession(_reader, Editor);
        using (var input = source.OpenRead()) await session.OpenAsync(input);
        var current = session.Current!; var item = await Object(current);
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => session.ApplyAsync(new SetContentAppearance(
            item.Reference, new(Opacity: value))));
        Assert.Same(current, session.Current); Assert.False(session.IsDirty);
    }

    [Fact]
    public void AppearanceValidationAndDashArraysAreBoundedAndImmutable()
    {
        Assert.Throws<ArgumentException>(() => new PdfContentAppearance().Validate());
        Assert.Throws<ArgumentOutOfRangeException>(() => new PdfContentAppearance(StrokeWidth: -1).Validate());
        Assert.Throws<ArgumentOutOfRangeException>(() => new PdfContentAppearance(BlendMode: (PdfBlendMode)99).Validate());
        Assert.Throws<ArgumentOutOfRangeException>(() => new PdfDashPattern([0, 0]));
        Assert.Throws<ArgumentOutOfRangeException>(() => new PdfDashPattern([double.NaN]));
        Assert.Throws<ArgumentOutOfRangeException>(() => new PdfDashPattern(Enumerable.Repeat(1d, 1000)));
        var data = new[] { 3d, 5d }; var dash = new PdfDashPattern(data, 2); data[0] = 999;
        Assert.Equal(3, dash.Lengths[0]); Assert.Equal(2, dash.Phase); Assert.Empty(PdfDashPattern.Solid.Lengths);
    }

    [Fact]
    public async Task StaleAppearanceHandlesAndTextPaintingConversionsAreRejected()
    {
        var source = await Content("BT /F1 20 Tf 20 180 Td (Words) Tj ET"); var item = await Object(source);
        await Assert.ThrowsAsync<ArgumentException>(() => Editor.ApplyAsync(source, [new SetContentAppearance(item.Reference, new(PathPaint: PdfPathPaintMode.Fill))]));
        var changed = await Editor.ApplyAsync(source, [new SetDocumentMetadata(new PdfMetadata("Changed"))]);
        await Assert.ThrowsAsync<PdfRevisionConflictException>(() => Editor.ApplyAsync(changed, [new SetContentAppearance(item.Reference, new(Opacity: .5))]));
    }

    [Fact]
    public async Task MarkedContentIsNotRestyled()
    {
        var source = await Content("/Span BMC 20 20 80 80 re f EMC"); var item = await Object(source);
        Assert.False(item.CanEdit);
        await Assert.ThrowsAsync<NotSupportedException>(() => Editor.ApplyAsync(source, [new SetContentAppearance(item.Reference, new(Opacity: .5))]));
    }
}
