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

public sealed class ContentSelectionTests
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
    [InlineData(0, false)] [InlineData(90, false)] [InlineData(180, false)] [InlineData(270, false)]
    [InlineData(0, true)] [InlineData(90, true)] [InlineData(180, true)] [InlineData(270, true)]
    public async Task OneTransactionTransformsMixedObjectsAgainstOriginalInspection(int rotation, bool crop)
    {
        var source = await Content("1 0 0 rg 60 220 40 35 re f BT /F1 14 Tf 1 0 0 1 70 150 Tm (Move me) Tj ET 0 0 1 rg 220 100 25 25 re f", rotation, crop);
        var before = (await Editor.ReadPageContentAsync(source, 1)).Objects;
        var operation = PdfContentSelection.Transform(new[] { before[1], before[0] }, PdfAffineTransform.Translation(8, 11));
        var result = await Editor.ApplyAsync(source, [operation]);
        var after = (await Editor.ReadPageContentAsync(result, 1)).Objects;
        Assert.Equal(before.Count, after.Count);
        for (var i = 0; i < 2; i++)
        { Assert.Equal(before[i].Bounds.X + 8, after[i].Bounds.X, 3); Assert.Equal(before[i].Bounds.Y + 11, after[i].Bounds.Y, 3); }
        Assert.Equal(before[2].Bounds, after[2].Bounds);
        Assert.Equal("Move me", after[1].Text);
        // Verify raw glyph order independently of the optional backend's rotated word-grouping heuristics.
        using var pdfStream = result.OpenRead();
        using var independent = UglyToad.PdfPig.PdfDocument.Open(pdfStream);
        Assert.Equal("Move me", string.Concat(independent.GetPage(1).Letters.Select(letter => letter.Value)));
    }
    [Fact]
    public async Task SortingSelectionDoesNotChangePaintingOrderOrReplayPersistentStateTwice()
    {
        var source = await Content("1 0 0 rg 30 250 50 50 re f BT /F1 16 Tf 22 TL 1 0 0 1 20 330 Tm 0 0 1 rg 3 2 (Move) \" ET BT 1 0 0 1 20 150 Tm (Keep spaced) Tj ET");
        var before = (await Editor.ReadPageContentAsync(source, 1)).Objects;
        var delta = PdfAffineTransform.Translation(120, 15);
        var a = await Editor.ApplyAsync(source, [PdfContentSelection.Transform(before.Take(2), delta)]);
        var b = await Editor.ApplyAsync(source, [PdfContentSelection.Transform(before.Take(2).Reverse(), delta)]);
        using var original = await Raster(source); using var first = await Raster(a); using var second = await Raster(b);
        for (var y = 0; y < first.Height; y++) for (var x = 0; x < first.Width; x++) Assert.Equal(first.GetPixel(x, y), second.GetPixel(x, y));
        for (var y = 200; y < 300; y++) for (var x = 0; x < 350; x++) Assert.Equal(original.GetPixel(x, y), first.GetPixel(x, y));
    }
    [Fact]
    public async Task DeleteSeveralObjectsKeepsUnselectedOverlappingContentAndUndoIsOneStep()
    {
        var source = await Content("0 1 0 rg 10 240 250 150 re f BT /F1 16 Tf 1 0 0 1 25 330 Tm (Remove first) Tj ET BT 1 0 0 1 25 290 Tm (Remove second) Tj ET 0 0 1 rg 310 20 30 30 re f");
        var session = new PdfSession(_reader, Editor);
        using (var stream = source.OpenRead()) await session.OpenAsync(stream);
        var original = session.Current!;
        var objects = (await Editor.ReadPageContentAsync(original, 1)).Objects;
        await session.ApplyAsync(PdfContentSelection.Delete(objects.Where(o => o.Kind == PdfContentObjectKind.Text)), original.Id);
        Assert.DoesNotContain("Remove", (await _reader.GetPageTextAsync(session.Current!, 1)).Text);
        Assert.Equal(2, (await Editor.ReadPageContentAsync(session.Current!, 1)).Objects.Count);
        using var after = await Raster(session.Current!); Assert.Equal(SKColors.Lime, after.GetPixel(40, 65)); Assert.Equal(SKColors.Blue, after.GetPixel(320, 365));
        Assert.True(await session.UndoAsync()); Assert.Same(original, session.Current); Assert.False(await session.UndoAsync());
        Assert.True(await session.RedoAsync()); Assert.DoesNotContain("Remove", (await _reader.GetPageTextAsync(session.Current!, 1)).Text);
    }
    [Fact]
    public async Task HeterogeneousBatchPreservesUnselectedObjectAndRejectsStaleRetry()
    {
        var source = await Content("1 0 0 rg 20 300 30 30 re f 0 1 0 rg 70 300 30 30 re f 0 0 1 rg 120 300 30 30 re f");
        var before = (await Editor.ReadPageContentAsync(source, 1)).Objects;
        var operation = new EditContentObjects(new PdfContentObjectEdit[] {
            new DeleteContentObject(before[0].Reference),
            new SetContentAppearance(before[1].Reference, new(FillColor: new PdfColor(255, 0, 255))) });
        var result = await Editor.ApplyAsync(source, [operation]);
        var after = (await Editor.ReadPageContentAsync(result, 1)).Objects; Assert.Equal(2, after.Count);
        Assert.Equal(before[2].Bounds, after[1].Bounds);
        using var bitmap = await Raster(result); Assert.Equal(SKColors.White, bitmap.GetPixel(25, 80)); Assert.Equal(SKColors.Magenta, bitmap.GetPixel(75, 80)); Assert.Equal(SKColors.Blue, bitmap.GetPixel(125, 80));
        await Assert.ThrowsAsync<PdfRevisionConflictException>(() => Editor.ApplyAsync(result, [operation]));
    }
    [Fact]
    public async Task DuplicateCreatesOneCopyPerInvocationAndGroupRotationUsesSharedCenter()
    {
        var source = await Content("1 0 0 rg 30 280 40 30 re f 0 0 1 rg 110 250 20 50 re f");
        var before = (await Editor.ReadPageContentAsync(source, 1)).Objects;
        var duplicate = await Editor.ApplyAsync(source, [PdfContentSelection.Duplicate(before, PdfAffineTransform.Translation(160, 0))]);
        var after = (await Editor.ReadPageContentAsync(duplicate, 1)).Objects; Assert.Equal(4, after.Count);
        Assert.Equal(before[0].Bounds, after[1].Bounds); Assert.Equal(before[1].Bounds, after[3].Bounds);
        Assert.Equal(before[0].Bounds.X + 160, after[0].Bounds.X, 3); Assert.Equal(before[1].Bounds.X + 160, after[2].Bounds.X, 3);
        var union = PdfContentSelection.Bounds(before); var transform = PdfAffineTransform.RotationAt(90, new(union.X + union.Width / 2, union.Y + union.Height / 2));
        var rotated = await Editor.ApplyAsync(source, [PdfContentSelection.Transform(before, transform)]);
        var actual = (await Editor.ReadPageContentAsync(rotated, 1)).Objects;
        for (var i = 0; i < before.Count; i++) RectEqual(transform.Map(before[i].Bounds), actual[i].Bounds);
    }
    [Fact]
    public async Task AnyInvalidTargetOrUnsupportedObjectRollsBackEntireSelection()
    {
        var source = await Content("1 0 0 rg 40 250 30 30 re f /Span BMC 90 250 30 30 re f EMC");
        var session = new PdfSession(_reader, Editor); using (var stream = source.OpenRead()) await session.OpenAsync(stream);
        var original = session.Current!; var objects = (await Editor.ReadPageContentAsync(original, 1)).Objects;
        Assert.True(objects[0].CanEdit); Assert.False(objects[1].CanEdit);
        await Assert.ThrowsAsync<NotSupportedException>(() => session.ApplyAsync(PdfContentSelection.Delete(objects), original.Id));
        Assert.Same(original, session.Current); Assert.False(session.CanUndo);
        var invalid = new EditContentObjects(new PdfContentObjectEdit[] { new DeleteContentObject(objects[0].Reference), new DeleteContentObject(objects[0].Reference with { Index = 500 }) });
        await Assert.ThrowsAsync<PdfRevisionConflictException>(() => session.ApplyAsync(invalid, original.Id));
        Assert.Same(original, session.Current);
        var badTransform = new EditContentObjects(new PdfContentObjectEdit[] { new TransformContentObject(objects[0].Reference, new(0, 0, 0, 0, 0, 0)) });
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => session.ApplyAsync(badTransform, original.Id));
        Assert.Same(original, session.Current);
    }
    [Fact]
    public async Task SharedFormResourceIsNotMutatedByMultipleInvocationTransforms()
    {
        var source = await Content(""); var file = PdfFile.Open(Bytes(source)); var page = Page(file);
        var form = file.Add(PdfStream.FromDecoded("1 0 0 rg 0 0 30 30 re f"u8, Dictionary(("Type", new PdfName("XObject")), ("Subtype", new PdfName("Form")), ("BBox", Numbers(0, 0, 30, 30)), ("Resources", new PdfDictionary()))));
        page["Resources"] = Dictionary(("XObject", Dictionary(("Shared", form))));
        page["Contents"] = file.Add(PdfStream.FromDecoded("q 1 0 0 1 20 300 cm /Shared Do Q q 1 0 0 1 90 300 cm /Shared Do Q q 1 0 0 1 160 300 cm /Shared Do Q"u8));
        using (var stream = new MemoryStream(file.Save())) source = await _reader.OpenAsync(stream);
        var objects = (await Editor.ReadPageContentAsync(source, 1)).Objects;
        var result = await Editor.ApplyAsync(source, [PdfContentSelection.Transform(objects.Take(2), PdfAffineTransform.Translation(0, 100))]);
        using var bitmap = await Raster(result);
        Assert.Equal(SKColors.Red, bitmap.GetPixel(30, 180)); Assert.Equal(SKColors.Red, bitmap.GetPixel(100, 180)); Assert.Equal(SKColors.Red, bitmap.GetPixel(170, 80));
        var reread = PdfFile.Open(Bytes(result)); Assert.Single(reread.EnumerateObjects(), o => o.Value is PdfStream s && s.Dictionary.Name("Subtype") == "Form");
    }
    [Theory]
    [InlineData(PdfSelectionAlignment.Left)] [InlineData(PdfSelectionAlignment.Right)] [InlineData(PdfSelectionAlignment.HorizontalCenter)]
    [InlineData(PdfSelectionAlignment.Top)] [InlineData(PdfSelectionAlignment.Bottom)] [InlineData(PdfSelectionAlignment.VerticalCenter)]
    public async Task AlignmentUsesUnionEdgeOrCenterWithoutResizingObjects(PdfSelectionAlignment alignment)
    {
        var source = await Editor.CreateAsync(size: new(400, 400));
        source = await Editor.ApplyAsync(source, [new AddShape(1, new(20, 30, 40, 50), Fill: PdfColor.Blue), new AddShape(1, new(130, 170, 70, 30), Fill: PdfColor.Blue)]);
        var before = (await Editor.ReadPageContentAsync(source, 1)).Objects;
        var result = await Editor.ApplyAsync(source, [PdfContentSelection.Align(before, alignment)]);
        var after = (await Editor.ReadPageContentAsync(result, 1)).Objects;
        double Coordinate(PdfRect b) => alignment switch {
            PdfSelectionAlignment.Left => b.X, PdfSelectionAlignment.Right => b.Right, PdfSelectionAlignment.HorizontalCenter => b.X + b.Width / 2,
            PdfSelectionAlignment.Top => b.Y, PdfSelectionAlignment.Bottom => b.Bottom, _ => b.Y + b.Height / 2 };
        var target = Coordinate(PdfContentSelection.Bounds(before));
        for (var i = 0; i < after.Count; i++)
        { Assert.Equal(target, Coordinate(after[i].Bounds), 3); Assert.Equal(before[i].Bounds.Width, after[i].Bounds.Width, 3); Assert.Equal(before[i].Bounds.Height, after[i].Bounds.Height, 3); }
    }
    [Theory]
    [InlineData(PdfSelectionDistribution.HorizontalCenters)] [InlineData(PdfSelectionDistribution.VerticalCenters)]
    [InlineData(PdfSelectionDistribution.HorizontalGaps)] [InlineData(PdfSelectionDistribution.VerticalGaps)]
    public void DistributionPreservesEndpointsAndEqualizesCentersOrGaps(PdfSelectionDistribution distribution)
    {
        var items = Fake(new(10, 20, 20, 10), new(45, 65, 30, 25), new(85, 100, 10, 40), new(200, 240, 50, 30));
        var operation = PdfContentSelection.Distribute(items.Reverse(), distribution);
        var after = operation.Edits.Cast<TransformContentObject>().Select(e => e.Transform.Map(items[e.Object.Index].Bounds)).ToArray();
        Assert.Equal(items[0].Bounds, after[0]); Assert.Equal(items[^1].Bounds, after[^1]);
        var horizontal = distribution is PdfSelectionDistribution.HorizontalCenters or PdfSelectionDistribution.HorizontalGaps;
        var gaps = distribution is PdfSelectionDistribution.HorizontalGaps or PdfSelectionDistribution.VerticalGaps;
        double Start(PdfRect b) => horizontal ? b.X : b.Y; double Extent(PdfRect b) => horizontal ? b.Width : b.Height;
        var intervals = after.Zip(after.Skip(1), (a, b) => gaps ? Start(b) - Start(a) - Extent(a) : Start(b) + Extent(b) / 2 - Start(a) - Extent(a) / 2).ToArray();
        foreach (var interval in intervals) Assert.Equal(intervals[0], interval, 6);
    }
    [Fact]
    public void SelectionValidationRejectsMixedOrDuplicateReferencesAndNeverEnumeratesWithoutBound()
    {
        var items = Fake(new(0, 0, 10, 10), new(20, 20, 10, 10)); var reference = items[0].Reference;
        Assert.Throws<ArgumentException>(() => new EditContentObjects([]));
        Assert.Throws<ArgumentException>(() => new EditContentObjects(new PdfContentObjectEdit[] { new DeleteContentObject(reference), new DeleteContentObject(reference) }));
        Assert.Throws<ArgumentException>(() => new EditContentObjects(new PdfContentObjectEdit[] { PdfContentSelection.Delete(items) }));
        Assert.Throws<ArgumentException>(() => PdfContentSelection.Bounds(new[] { items[0], null! }));
        foreach (var changed in new[] { items[1].Reference with { Revision = Guid.NewGuid() }, items[1].Reference with { PageNumber = 2 }, items[1].Reference with { Fingerprint = "different" } })
            Assert.Throws<PdfRevisionConflictException>(() => new EditContentObjects(new PdfContentObjectEdit[] { new DeleteContentObject(reference), new DeleteContentObject(changed) }));
        var enumerated = 0;
        IEnumerable<PdfContentObjectEdit> Endless() { while (true) { enumerated++; yield return new DeleteContentObject(reference with { Index = enumerated }); } }
        Assert.Throws<ArgumentException>(() => new EditContentObjects(Endless())); Assert.Equal(1001, enumerated);
        Assert.Throws<ArgumentException>(() => PdfContentSelection.Align(items.Take(1), PdfSelectionAlignment.Left));
        Assert.Throws<ArgumentException>(() => PdfContentSelection.Distribute(items, PdfSelectionDistribution.HorizontalCenters));
        Assert.Throws<ArgumentOutOfRangeException>(() => PdfContentSelection.Align(items, (PdfSelectionAlignment)999));
        Assert.Throws<ArgumentOutOfRangeException>(() => PdfContentSelection.Distribute(items, (PdfSelectionDistribution)999));
        Assert.Throws<InvalidOperationException>(() => PdfContentSelection.Distribute(Fake(new(0, 0, 100, 10), new(20, 0, 100, 10), new(40, 0, 100, 10)), PdfSelectionDistribution.HorizontalGaps));
    }
    [Fact]
    public async Task CancellationAndTamperedAggregateDoNotPublish()
    {
        var source = await Content("0 0 1 rg 20 20 30 30 re f 80 20 30 30 re f"); var objects = (await Editor.ReadPageContentAsync(source, 1)).Objects;
        var edit = PdfContentSelection.Delete(objects);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Editor.ApplyAsync(source, [edit], new CancellationToken(true)));
        await Assert.ThrowsAsync<PdfRevisionConflictException>(() => Editor.ApplyAsync(source, [edit with { Object = edit.Object with { PageNumber = 2 } }]));
        Assert.Equal(2, (await Editor.ReadPageContentAsync(source, 1)).Objects.Count);
    }
    private static PdfContentObject[] Fake(params PdfRect[] bounds)
    {
        var revision = Guid.NewGuid(); return bounds.Select((b, i) => new PdfContentObject(new(revision, 1, "fingerprint", i), PdfContentObjectKind.Path, b)).ToArray();
    }
    private static void RectEqual(PdfRect expected, PdfRect actual)
    { Assert.Equal(expected.X, actual.X, 3); Assert.Equal(expected.Y, actual.Y, 3); Assert.Equal(expected.Width, actual.Width, 3); Assert.Equal(expected.Height, actual.Height, 3); }
}
