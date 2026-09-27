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

public sealed class ContentObjectTests
{
    private readonly PdfPigBackend _reader = new();
    private ManagedPdfEditor Editor => new(_reader);
    private static byte[] Bytes(PdfSnapshot source) { using var input = source.OpenRead(); using var output = new MemoryStream(); input.CopyTo(output); return output.ToArray(); }
    private static PdfDictionary Page(PdfFile file) => file.Dictionary(file.Array(file.Dictionary(file.Catalog["Pages"])["Kids"])[0]);
    private async Task<PdfSnapshot> Content(string content, int rotation = 0, bool crop = false)
    {
        var source = await Editor.CreateAsync(size: new PdfSize(400, 400)); var file = PdfFile.Open(Bytes(source)); var page = Page(file);
        var font = file.Add(Dictionary(("Type", new PdfName("Font")), ("Subtype", new PdfName("Type1")), ("BaseFont", new PdfName("Helvetica")), ("Encoding", new PdfName("WinAnsiEncoding"))));
        page["Resources"] = Dictionary(("Font", Dictionary(("F1", font))));
        page["Contents"] = file.Add(PdfStream.FromDecoded(Encoding.ASCII.GetBytes(content))); page["Rotate"] = new PdfNumber(rotation);
        if (crop) { page["CropBox"] = Numbers(20, 30, 320, 330); page["UserUnit"] = new PdfNumber(1.25); }
        using var stream = new MemoryStream(file.Save()); return await _reader.OpenAsync(stream);
    }
    private async Task<SKBitmap> Raster(PdfSnapshot source)
    {
        await using var renderer = new SkiaPdfRenderer(_reader);
        using var image = await new PdfRasterExporter(renderer).RenderPageAsync(source, 1, new(Dpi: 72)); return SKBitmap.FromImage(image);
    }
    [Theory]
    [InlineData(0)] [InlineData(90)] [InlineData(180)] [InlineData(270)]
    public async Task TransformExistingPathUsesViewCoordinatesAndPreservesNeighbor(int rotation)
    {
        var source = await Content("1 0 0 rg 60 230 40 35 re f 0 0 1 rg 210 140 35 35 re f", rotation);
        var objects = (await Editor.ReadPageContentAsync(source, 1)).Objects;
        Assert.Equal(2, objects.Count); Assert.All(objects, item => Assert.True(item.CanEdit, item.ReadOnlyReason));
        var red = objects[0]; var delta = PdfAffineTransform.Translation(12, 18);
        var result = await Editor.ApplyAsync(source, [new TransformContentObject(red.Reference, delta)]);
        var after = (await Editor.ReadPageContentAsync(result, 1)).Objects;
        Assert.Equal(red.Bounds.X + 12, after[0].Bounds.X, 4); Assert.Equal(red.Bounds.Y + 18, after[0].Bounds.Y, 4);
        Assert.Equal(objects[1].Bounds, after[1].Bounds);
        using var pixels = await Raster(result); var center = new PdfPoint(after[0].Bounds.X + 20, after[0].Bounds.Y + 18);
        Assert.True(pixels.GetPixel((int)center.X, (int)center.Y).Red > 240);
    }
    [Theory]
    [InlineData(0)] [InlineData(90)] [InlineData(180)] [InlineData(270)]
    public async Task TransformAccountsForCropOriginAndUserUnit(int rotation)
    {
        var source = await Content("1 0 0 rg 60 100 40 35 re f", rotation, crop: true);
        var before = Assert.Single((await Editor.ReadPageContentAsync(source, 1)).Objects);
        var result = await Editor.ApplyAsync(source, [new TransformContentObject(before.Reference, PdfAffineTransform.Translation(8, 11))]);
        var after = Assert.Single((await Editor.ReadPageContentAsync(result, 1)).Objects);
        Assert.Equal(before.Bounds.X + 8, after.Bounds.X, 4); Assert.Equal(before.Bounds.Y + 11, after.Bounds.Y, 4);
    }
    [Theory]
    [InlineData(0)] [InlineData(90)] [InlineData(180)] [InlineData(270)]
    public void AffineRoundTripAndRotation(int degrees)
    {
        var mapping = PdfAffineTransform.RotationAt(degrees, new(50, 80)).Concat(PdfAffineTransform.ScaleAt(1.5, .6, new(20, 40)));
        var original = new PdfPoint(82.4, -11.8); var result = mapping.Inverse().Map(mapping.Map(original));
        Assert.Equal(original.X, result.X, 8); Assert.Equal(original.Y, result.Y, 8);
    }
    [Fact]
    public async Task TextTransformKeepsPersistentSpacingAndColorForFollowingObjects()
    {
        var source = await Content("BT /F1 16 Tf 22 TL 1 0 0 1 20 330 Tm 0 0 1 rg 3 2 (Move) \" ET\nBT 1 0 0 1 20 150 Tm (Keep spaced) Tj ET");
        var objects = (await Editor.ReadPageContentAsync(source, 1)).Objects; Assert.Equal(2, objects.Count);
        var before = await _reader.GetPageTextAsync(source, 1);
        var result = await Editor.ApplyAsync(source, [new TransformContentObject(objects[0].Reference, PdfAffineTransform.Translation(40, 20))]);
        var after = await _reader.GetPageTextAsync(result, 1);
        Assert.Equal(before.Words.Where(w => w.Text.Contains("Keep", StringComparison.Ordinal)).Select(w => w.Bounds), after.Words.Where(w => w.Text.Contains("Keep", StringComparison.Ordinal)).Select(w => w.Bounds));
        using var a = await Raster(source); using var b = await Raster(result);
        for (var y = 200; y < 290; y++) for (var x = 0; x < 300; x++) Assert.Equal(a.GetPixel(x, y), b.GetPixel(x, y));
    }
    [Fact]
    public async Task DeleteTextPreservesFollowingStateAndDoesNotDeleteOverlappingShape()
    {
        var source = await Content("0 1 0 rg 10 270 180 60 re f BT /F1 16 Tf 22 TL 1 0 0 1 20 330 Tm 0 0 1 rg 3 2 (Remove) \" ET BT 1 0 0 1 20 150 Tm (Keep) Tj ET");
        var objects = (await Editor.ReadPageContentAsync(source, 1)).Objects;
        var result = await Editor.ApplyAsync(source, [new DeleteContentObject(objects[1].Reference)]);
        var text = (await _reader.GetPageTextAsync(result, 1)).Text; Assert.DoesNotContain("Remove", text); Assert.Contains("Keep", text);
        Assert.Equal(2, (await Editor.ReadPageContentAsync(result, 1)).Objects.Count);
        using var a = await Raster(source); using var b = await Raster(result); Assert.Equal(a.GetPixel(15, 80), b.GetPixel(15, 80));
        for (var y = 200; y < 290; y++) for (var x = 0; x < 300; x++) Assert.Equal(a.GetPixel(x, y), b.GetPixel(x, y));
    }
    [Fact]
    public async Task InterleavedPathMatrixStateSurvivesTransform()
    {
        var source = await Content("1 0 0 rg 20 20 m 2 0 0 2 0 0 cm 50 20 l 50 50 l h f 0 0 1 rg 100 100 10 10 re f");
        var objects = (await Editor.ReadPageContentAsync(source, 1)).Objects;
        var result = await Editor.ApplyAsync(source, [new TransformContentObject(objects[0].Reference, PdfAffineTransform.Translation(20, 15))]);
        var after = (await Editor.ReadPageContentAsync(result, 1)).Objects; Assert.Equal(objects[1].Bounds, after[1].Bounds);
        using var a = await Raster(source); using var b = await Raster(result); Assert.Equal(a.GetPixel(205, 195), b.GetPixel(205, 195));
    }
    [Fact]
    public async Task DuplicateAndClipAreNativeButNotRedaction()
    {
        var source = await Content("1 0 0 rg 40 280 100 80 re f");
        var item = Assert.Single((await Editor.ReadPageContentAsync(source, 1)).Objects);
        var result = await Editor.ApplyAsync(source, [new DuplicateContentObject(item.Reference, PdfAffineTransform.Translation(150, 0))]);
        Assert.Equal(2, (await Editor.ReadPageContentAsync(result, 1)).Objects.Count);
        var objects = (await Editor.ReadPageContentAsync(result, 1)).Objects;
        result = await Editor.ApplyAsync(result, [new ClipContentObject(objects[1].Reference, new PdfRect(40, 40, 50, 80))]);
        Assert.Equal(2, (await Editor.ReadPageContentAsync(result, 1)).Objects.Count); // The clipped source remains in the PDF.
        using var pixels = await Raster(result); Assert.Equal(SKColors.Red, pixels.GetPixel(50, 60));
        Assert.Equal(SKColors.White, pixels.GetPixel(120, 60)); Assert.Equal(SKColors.Red, pixels.GetPixel(200, 60));
    }
    [Fact]
    public async Task SharedFormInvocationsAreEditedIndependently()
    {
        var source = await Content(""); var file = PdfFile.Open(Bytes(source)); var page = Page(file);
        var form = file.Add(PdfStream.FromDecoded("1 0 0 rg 0 0 40 40 re f"u8, Dictionary(("Type", new PdfName("XObject")), ("Subtype", new PdfName("Form")), ("BBox", Numbers(0, 0, 40, 40)), ("Resources", new PdfDictionary()))));
        page["Resources"] = Dictionary(("XObject", Dictionary(("Shared", form))));
        page["Contents"] = file.Add(PdfStream.FromDecoded("q 1 0 0 1 20 300 cm /Shared Do Q q 1 0 0 1 200 300 cm /Shared Do Q"u8));
        using var input = new MemoryStream(file.Save()); source = await _reader.OpenAsync(input);
        var objects = (await Editor.ReadPageContentAsync(source, 1)).Objects; Assert.All(objects, o => Assert.Equal(PdfContentObjectKind.Form, o.Kind));
        var result = await Editor.ApplyAsync(source, [new TransformContentObject(objects[0].Reference, PdfAffineTransform.Translation(0, 100))]);
        using var pixels = await Raster(result); Assert.Equal(SKColors.White, pixels.GetPixel(30, 70)); Assert.Equal(SKColors.Red, pixels.GetPixel(30, 170)); Assert.Equal(SKColors.Red, pixels.GetPixel(210, 70));
        var reread = PdfFile.Open(Bytes(result)); Assert.Single(reread.EnumerateObjects(), o => o.Value is PdfStream s && s.Dictionary.Name("Subtype") == "Form");
    }
    [Fact]
    public async Task TextObjectReplacementPreservesOverlappingGraphicsAndZOrder()
    {
        var source = await Content("0 1 0 rg 10 250 250 120 re f BT /F1 16 Tf 1 0 0 1 30 330 Tm (Old words) Tj ET 0 0 1 rg 300 100 20 20 re f");
        var item = (await Editor.ReadPageContentAsync(source, 1)).Objects.Single(o => o.Kind == PdfContentObjectKind.Text);
        var result = await Editor.ApplyAsync(source, [new ReplaceContentText(item.Reference, new PdfRect(30, 60, 180, 70), "New wrapped words for this rectangle", 16)]);
        var text = (await _reader.GetPageTextAsync(result, 1)).Text; Assert.DoesNotContain("Old", text); Assert.Contains("New wrapped words", text);
        using var pixels = await Raster(result); Assert.Equal(SKColors.Lime, pixels.GetPixel(15, 40)); Assert.Equal(SKColors.Blue, pixels.GetPixel(310, 290));
    }
    [Theory]
    [InlineData(PdfTextAlignment.Left)] [InlineData(PdfTextAlignment.Center)] [InlineData(PdfTextAlignment.Right)]
    public async Task WrappedTextAlignmentAndIndependentExtraction(PdfTextAlignment alignment)
    {
        var source = await Editor.CreateAsync(size: new PdfSize(300, 300));
        var result = await Editor.ApplyAsync(source, [new AddTextBox(1, new PdfRect(20, 30, 110, 100), "First words and more words\nSecond line", 14, Alignment: alignment)]);
        var page = await _reader.GetPageTextAsync(result, 1); Assert.Contains("First words", page.Text); Assert.Contains("Second line", page.Text);
        Assert.All(page.Words, word => { Assert.InRange(word.Bounds.X, 19.9, 131); Assert.True(word.Bounds.Right <= 131); Assert.InRange(word.Bounds.Y, 28, 131); });
        var first = page.Words[0]; if (alignment != PdfTextAlignment.Left) Assert.True(first.Bounds.X > 20);
    }
    [Fact]
    public async Task TextOverflowAndStaleHandlesNeverPublishPartialChanges()
    {
        var source = await Content("BT /F1 14 Tf 1 0 0 1 20 300 Tm (Existing) Tj ET");
        var session = new PdfSession(_reader, Editor); using (var input = source.OpenRead()) await session.OpenAsync(input);
        var current = session.Current!; var item = Assert.Single((await Editor.ReadPageContentAsync(current, 1)).Objects);
        await Assert.ThrowsAsync<InvalidOperationException>(() => session.ApplyAsync(new ReplaceContentText(item.Reference, new PdfRect(20, 20, 20, 5), "Text will overflow")));
        Assert.Same(current, session.Current);
        await session.ApplyAsync(new SetDocumentMetadata(new PdfMetadata("New revision")));
        await Assert.ThrowsAsync<PdfRevisionConflictException>(() => session.ApplyAsync(new DeleteContentObject(item.Reference)));
        Assert.Contains("Existing", (await _reader.GetPageTextAsync(session.Current!, 1)).Text);
    }
    [Fact]
    public async Task ForgedOrWithinBatchStaleHandlesAreRejected()
    {
        var source = await Content("10 10 20 20 re f 50 10 20 20 re f"); var items = (await Editor.ReadPageContentAsync(source, 1)).Objects;
        await Assert.ThrowsAsync<PdfRevisionConflictException>(() => Editor.ApplyAsync(source, [new DeleteContentObject(items[0].Reference with { Fingerprint = "wrong" })]));
        await Assert.ThrowsAsync<PdfRevisionConflictException>(() => Editor.ApplyAsync(source, [new DeleteContentObject(items[0].Reference), new DeleteContentObject(items[1].Reference)]));
        Assert.Equal(2, (await Editor.ReadPageContentAsync(source, 1)).Objects.Count);
    }
    [Theory]
    [InlineData("10 10 20 20 re W f")]
    [InlineData("/Span BMC 10 10 20 20 re f EMC")]
    [InlineData("BT /F1 14 Tf 4 Tr 1 0 0 1 20 300 Tm (Clip text) Tj ET")]
    public async Task UnsafeIndependentObjectEditsAreExplicitlyReadOnly(string content)
    {
        var source = await Content(content); var item = Assert.Single((await Editor.ReadPageContentAsync(source, 1)).Objects);
        Assert.False(item.CanEdit); Assert.NotNull(item.ReadOnlyReason);
        await Assert.ThrowsAsync<NotSupportedException>(() => Editor.ApplyAsync(source, [new DeleteContentObject(item.Reference)]));
    }
    [Fact]
    public async Task BudgetsAndCancellationAreEnforced()
    {
        var source = await Content("10 10 20 20 re f 50 10 20 20 re f");
        await Assert.ThrowsAsync<InvalidDataException>(() => Editor.ReadPageContentAsync(source, 1, new(MaximumObjects: 1)));
        await Assert.ThrowsAsync<InvalidDataException>(() => Editor.ReadPageContentAsync(source, 1, new(MaximumInstructions: 1)));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Editor.ReadPageContentAsync(source, 1, cancellationToken: new(true)));
        var item = (await Editor.ReadPageContentAsync(source, 1)).Objects[0];
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => Editor.ApplyAsync(source, [new TransformContentObject(item.Reference, default)]));
    }
}
