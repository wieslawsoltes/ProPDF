using System.Text;
using ProPDF.Core;
using ProPDF.Editing;
using ProPDF.Engine.PdfPig;
using ProPDF.Rendering.Skia;
using Xunit;
using SkiaSharp;

namespace ProPDF.Tests;

public sealed class OutputTests
{
    [Theory]
    [InlineData("all", "1,2,3,4,5")]
    [InlineData("odd", "1,3,5")]
    [InlineData("even", "2,4")]
    [InlineData("3-1, last,2", "3,2,1,5")]
    [InlineData(" 1 , 3-last ", "1,3,4,5")]
    public void PageRangesPreserveOrderAndRemoveDuplicates(string expression, string expected) =>
        Assert.Equal(expected, string.Join(',', PdfPageSelection.Parse(expression, 5)));

    [Theory]
    [InlineData("0")][InlineData("6")][InlineData("1,,2")][InlineData("1-2-3")][InlineData("-2")][InlineData("NaN")]
    public void InvalidRangesFail(string expression) => Assert.ThrowsAny<Exception>(() => PdfPageSelection.Parse(expression, 5));

    [Fact]
    public void RangeBudgetsAreEnforced() => Assert.Throws<ArgumentException>(() => PdfPageSelection.Parse("all", 20, 10));

    [Theory]
    [InlineData(PdfRasterFormat.Png)]
    [InlineData(PdfRasterFormat.Jpeg)]
    [InlineData(PdfRasterFormat.Webp)]
    public async Task RasterExportAssemblesMultipleTilesAndRoundTrips(PdfRasterFormat format)
    {
        var backend = new PdfPigBackend();
        var editor = new ManagedPdfEditor(backend);
        var document = await editor.CreateAsync(size: new PdfSize(700, 700));
        document = await editor.ApplyAsync(document, new IPdfEditOperation[] { new AddShape(1, new PdfRect(490, 490, 80, 80), Fill: new PdfColor(255, 0, 0)) });
        await using var renderer = new SkiaPdfRenderer(backend);
        var export = new PdfRasterExporter(renderer);
        var asset = await export.ExportPageAsync(document, 1, new PdfRasterExportOptions(72, format));
        using var bitmap = SKBitmap.Decode(asset.ToArray());
        Assert.Equal(700, bitmap.Width);
        Assert.Equal(700, bitmap.Height);
        var pixel = bitmap.GetPixel(530, 530);
        Assert.True(pixel.Red > 220 && pixel.Green < 30);
        Assert.True(bitmap.GetPixel(100, 100).Red > 240);
        using var crop = await export.RenderPageAsync(document, 1, new PdfRasterExportOptions(72), new PdfRect(500, 500, 50, 50));
        Assert.Equal(50, crop.Width);
        using var cropPixels = SKBitmap.FromImage(crop);
        Assert.True(cropPixels.GetPixel(20, 20).Red > 240 && cropPixels.GetPixel(20, 20).Green < 20);
    }

    [Fact]
    public async Task OversizedAndCancelledExportsDoNotPublishFiles()
    {
        var backend = new PdfPigBackend();
        var editor = new ManagedPdfEditor(backend);
        var document = await editor.CreateAsync();
        await using var renderer = new SkiaPdfRenderer(backend);
        var export = new PdfRasterExporter(renderer);
        await Assert.ThrowsAsync<InvalidOperationException>(() => export.RenderPageAsync(document, 1, new PdfRasterExportOptions(MaximumPixels: 100)));
        var path = Path.Combine(Path.GetTempPath(), $"propdf-export-{Guid.NewGuid():N}.png");
        try
        {
            await File.WriteAllTextAsync(path, "original");
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => export.SavePageAsync(document, 1, path, cancellationToken: new CancellationToken(true)));
            Assert.Equal("original", await File.ReadAllTextAsync(path));
            await Assert.ThrowsAsync<InvalidOperationException>(() => PdfFileOutput.WriteAtomicAsync(path, async (stream, token) =>
            {
                await stream.WriteAsync(new byte[] { 1, 2, 3 }, token);
                throw new InvalidOperationException("Synthetic failed writer");
            }));
            Assert.Equal("original", await File.ReadAllTextAsync(path));
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public async Task TextExportHonorsOrderAndByteLimitAndLeavesStreamOpen()
    {
        var backend = new PdfPigBackend();
        var editor = new ManagedPdfEditor(backend);
        var document = await editor.CreateAsync(2);
        document = await editor.ApplyAsync(document, new IPdfEditOperation[]
        { new AddText(1, new PdfPoint(20, 40), "First"), new AddText(2, new PdfPoint(20, 40), "Second") });
        var export = new PdfTextExporter(backend);
        using var output = new MemoryStream();
        await export.WriteAsync(document, output, new[] { 2, 1 });
        Assert.True(output.CanWrite);
        Assert.Equal("Second\n\f\nFirst", Encoding.UTF8.GetString(output.ToArray()));
        using var limited = new MemoryStream();
        await Assert.ThrowsAsync<InvalidDataException>(() => export.WriteAsync(document, limited, maximumBytes: 2));
    }

    [Fact]
    public async Task ComparisonDetectsChangedPixelsTextAndAddedPages()
    {
        var backend = new PdfPigBackend();
        var editor = new ManagedPdfEditor(backend);
        var original = await editor.CreateAsync(size: new PdfSize(200, 300));
        var modified = await editor.ApplyAsync(original, new IPdfEditOperation[]
        { new AddShape(1, new PdfRect(20, 30, 40, 50), Fill: new PdfColor(255, 0, 0)), new AddText(1, new PdfPoint(20, 120), "Changed"), new InsertBlankPage(2, new PdfSize(200, 300)) });
        await using var renderer = new SkiaPdfRenderer(backend);
        var compare = new PdfDocumentComparer(renderer, backend);
        Assert.True((await compare.CompareAsync(original, original)).AreEqual);
        var result = await compare.CompareAsync(original, modified);
        Assert.False(result.AreEqual);
        Assert.Equal(2, result.ChangedPageCount);
        Assert.Equal(PdfPageChangeKind.Changed, result.Pages[0].Kind);
        Assert.True(result.Pages[0].ChangedPixels > 1000);
        Assert.True(result.Pages[0].TextChanged);
        Assert.NotNull(result.Pages[0].ChangedBounds);
        Assert.Equal(PdfPageChangeKind.Added, result.Pages[1].Kind);
        Assert.Equal(PdfPageChangeKind.Removed, (await compare.CompareAsync(modified, original)).Pages[1].Kind);
    }

    [Fact]
    public async Task MetadataOnlyChangesHaveNoVisualDifferenceAndSizeChangesAreReported()
    {
        var backend = new PdfPigBackend();
        var editor = new ManagedPdfEditor(backend);
        var original = await editor.CreateAsync(size: new PdfSize(200, 300));
        var metadata = await editor.ApplyAsync(original, new IPdfEditOperation[] { new SetDocumentMetadata(new PdfMetadata("Different title")) });
        await using var renderer = new SkiaPdfRenderer(backend);
        var compare = new PdfDocumentComparer(renderer, backend);
        Assert.True((await compare.CompareAsync(original, metadata)).AreEqual);
        var rotated = await editor.ApplyAsync(original, new IPdfEditOperation[] { new RotatePage(1) });
        Assert.Equal(PdfPageChangeKind.Resized, Assert.Single((await compare.CompareAsync(original, rotated)).Pages).Kind);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => compare.CompareAsync(original, metadata, cancellationToken: new CancellationToken(true)));
    }
}
