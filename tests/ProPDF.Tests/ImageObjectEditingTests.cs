using System.Buffers.Binary;
using ProPDF.Core;
using ProPDF.Editing;
using ProPDF.Engine.PdfPig;
using ProPDF.Kernel;
using ProPDF.Rendering.Skia;
using SkiaSharp;
using Xunit;
using static ProPDF.Kernel.PdfValues;

namespace ProPDF.Tests;

public sealed class ImageObjectEditingTests
{
    private readonly PdfPigBackend _reader = new();
    private ManagedPdfEditor Editor => new(_reader);
    private static byte[] Bytes(PdfSnapshot source) { using var input = source.OpenRead(); using var memory = new MemoryStream(); input.CopyTo(memory); return memory.ToArray(); }
    private static PdfDictionary Page(PdfFile file, int number = 0) => file.Dictionary(file.Array(file.Dictionary(file.Catalog["Pages"])["Kids"])[number]);
    private static PdfBinaryAsset Image(SKColor color)
    {
        using var bitmap = new SKBitmap(new SKImageInfo(8, 4, SKColorType.Rgba8888, SKAlphaType.Unpremul)); bitmap.Erase(color);
        using var image = SKImage.FromBitmap(bitmap); using var data = image.Encode(SKEncodedImageFormat.Png, 100);
        return new(data.ToArray());
    }
    private async Task<PdfSnapshot> SharedImage(int rotation = 0, bool clipped = false, bool optional = false)
    {
        var source = await Editor.CreateAsync(2, new(240, 240));
        var file = PdfFile.Open(Bytes(source));
        var dictionary = Dictionary(("Type", new PdfName("XObject")), ("Subtype", new PdfName("Image")), ("Width", new PdfNumber(2)),
            ("Height", new PdfNumber(2)), ("BitsPerComponent", new PdfNumber(8)), ("ColorSpace", new PdfName("DeviceRGB")));
        if (optional) dictionary["OC"] = file.Add(Dictionary(("Type", new PdfName("OCG")), ("Name", new PdfString("Layer"))));
        var image = file.Add(PdfStream.FromDecoded(new byte[] { 0, 0, 255, 0, 0, 255, 0, 0, 255, 0, 0, 255 }, dictionary));
        var resources = file.Add(Dictionary(("XObject", Dictionary(("Shared", image)))));
        var content = "q " + (clipped ? "20 180 30 40 re W n " : "") + "60 0 0 40 20 180 cm /Shared Do Q q 60 0 0 40 140 180 cm /Shared Do Q";
        for (var i = 0; i < 2; i++)
        {
            var page = Page(file, i); page["Resources"] = resources; page["Rotate"] = new PdfNumber(rotation);
            page["Contents"] = file.Add(PdfStream.FromDecoded(System.Text.Encoding.ASCII.GetBytes(content)));
        }
        using var input = new MemoryStream(file.Save()); return await _reader.OpenAsync(input);
    }
    private async Task<PdfContentObject> First(PdfSnapshot source) => (await Editor.ReadPageContentAsync(source, 1)).Objects.First(o => o.Kind == PdfContentObjectKind.Image);
    private async Task<SKBitmap> Raster(PdfSnapshot source, int page = 1)
    {
        await using var renderer = new SkiaPdfRenderer(_reader);
        using var image = await new PdfRasterExporter(renderer).RenderPageAsync(source, page, new(Dpi: 72));
        return SKBitmap.FromImage(image);
    }
    private static PdfStream SelectedImage(PdfFile file, string resource, int page = 0) => (PdfStream)file.Resolve(file.Dictionary(file.Dictionary(Page(file, page)["Resources"])["XObject"])[resource]);

    [Theory]
    [InlineData(0)] [InlineData(90)] [InlineData(180)] [InlineData(270)]
    public async Task ReplacementPreservesGeometryOtherInvocationsAndOtherPages(int rotation)
    {
        var source = await SharedImage(rotation); var item = await First(source); var oldBytes = Bytes(source);
        var result = await Editor.ApplyAsync(source, [new ReplaceContentImage(item.Reference, Image(SKColors.Red), true)]);
        var objects = (await Editor.ReadPageContentAsync(result, 1)).Objects;
        Assert.Equal(item.Bounds, objects[0].Bounds); Assert.Equal(2, objects.Count);
        Assert.NotEqual(objects[0].ResourceName, objects[1].ResourceName);
        Assert.Equal(8, objects[0].ImageInfo!.PixelWidth); Assert.Equal(4, objects[0].ImageInfo!.PixelHeight);
        Assert.True(objects[0].ImageInfo!.Interpolate); Assert.False(objects[1].ImageInfo!.Interpolate);
        using var pixels = await Raster(result); var red = objects[0].Bounds; var blue = objects[1].Bounds;
        Assert.Equal(SKColors.Red, pixels.GetPixel((int)(red.X + red.Width / 2), (int)(red.Y + red.Height / 2)));
        Assert.Equal(SKColors.Blue, pixels.GetPixel((int)(blue.X + blue.Width / 2), (int)(blue.Y + blue.Height / 2)));
        using var originalPage2 = await Raster(source, 2); using var newPage2 = await Raster(result, 2);
        Assert.Equal(originalPage2.Bytes, newPage2.Bytes); Assert.Equal(oldBytes, Bytes(source));
    }

    [Fact]
    public async Task ReplacementRetainsAncestorClipping()
    {
        var source = await SharedImage(clipped: true); var item = await First(source);
        var result = await Editor.ApplyAsync(source, [new ReplaceContentImage(item.Reference, Image(SKColors.Red))]);
        using var pixels = await Raster(result);
        Assert.Equal(SKColors.Red, pixels.GetPixel(30, 40)); Assert.Equal(SKColors.White, pixels.GetPixel(65, 40));
        Assert.Equal(SKColors.Blue, pixels.GetPixel(160, 40));
    }

    [Fact]
    public async Task TransparentReplacementUsesUnpremultipliedRgbAndGraySoftMask()
    {
        var source = await SharedImage(); var result = await Editor.ApplyAsync(source,
            [new ReplaceContentImage((await First(source)).Reference, Image(new SKColor(255, 0, 0, 128)))]);
        var item = await First(result); Assert.True(item.ImageInfo!.HasSoftMask);
        var file = PdfFile.Open(Bytes(result)); var image = SelectedImage(file, item.ResourceName!);
        var samples = file.Decode(image); Assert.Equal(new byte[] { 255, 0, 0 }, samples.Take(3));
        var mask = (PdfStream)file.Resolve(image.Dictionary["SMask"]); Assert.Equal("DeviceGray", mask.Dictionary.Name("ColorSpace"));
        Assert.All(file.Decode(mask), b => Assert.Equal(128, b));
        using var pixels = await Raster(result); var color = pixels.GetPixel(40, 40);
        Assert.Equal(255, color.Red); Assert.InRange((int)color.Green, 126, 128); Assert.InRange((int)color.Blue, 126, 128);
        Assert.Equal(SKColors.Blue, pixels.GetPixel(160, 40));
    }

    [Fact]
    public async Task InterpolationCopiesTheDictionaryButRetainsOriginalEncodedPixels()
    {
        var source = await SharedImage(); var item = await First(source); var originalFile = PdfFile.Open(Bytes(source));
        var original = SelectedImage(originalFile, item.ResourceName!);
        var result = await Editor.ApplyAsync(source, [new SetContentImageInterpolation(item.Reference, true)]);
        var objects = (await Editor.ReadPageContentAsync(result, 1)).Objects;
        Assert.True(objects[0].ImageInfo!.Interpolate); Assert.False(objects[1].ImageInfo!.Interpolate);
        var file = PdfFile.Open(Bytes(result)); Assert.Equal(original.EncodedBytes, SelectedImage(file, objects[0].ResourceName!).EncodedBytes);
        Assert.Equal(original.EncodedBytes, SelectedImage(file, objects[1].ResourceName!).EncodedBytes);
        Assert.False((await Editor.ReadPageContentAsync(result, 2)).Objects[0].ImageInfo!.Interpolate);
    }

    [Fact]
    public async Task ImageAppearanceChangesOpacityWithoutTouchingResourcePixels()
    {
        var source = await SharedImage(); var item = await First(source);
        var result = await Editor.ApplyAsync(source, [new SetContentAppearance(item.Reference, new(Opacity: .5))]);
        var after = await First(result); Assert.Equal(item.ResourceName, after.ResourceName); Assert.Equal(item.ImageInfo, after.ImageInfo);
        using var pixels = await Raster(result); var color = pixels.GetPixel(40, 40);
        Assert.Equal(255, color.Blue); Assert.InRange((int)color.Red, 126, 128);
        Assert.Equal(SKColors.Blue, pixels.GetPixel(160, 40));
        await Assert.ThrowsAsync<ArgumentException>(() => Editor.ApplyAsync(source, [new SetContentAppearance(item.Reference, new(FillColor: PdfColor.Black))]));
    }

    [Fact]
    public async Task OptionalContentImageIsReadOnlyInsteadOfDroppingLayerSemantics()
    {
        var source = await SharedImage(optional: true); var item = await First(source); Assert.False(item.CanEdit);
        await Assert.ThrowsAsync<NotSupportedException>(() => Editor.ApplyAsync(source, [new ReplaceContentImage(item.Reference, Image(SKColors.Red))]));
        await Assert.ThrowsAsync<NotSupportedException>(() => Editor.ApplyAsync(source, [new SetContentImageInterpolation(item.Reference, true)]));
    }

    [Fact]
    public async Task InvalidImageFailsWithoutChangingSessionOrHistory()
    {
        var source = await SharedImage(); var session = new PdfSession(_reader, Editor);
        using (var input = source.OpenRead()) await session.OpenAsync(input);
        var current = session.Current!; var item = await First(current);
        await Assert.ThrowsAsync<InvalidDataException>(() => session.ApplyAsync(new ReplaceContentImage(item.Reference, new("not an image"u8))));
        Assert.Same(current, session.Current); Assert.False(session.CanUndo);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => session.ApplyAsync(new ReplaceContentImage(item.Reference, Image(SKColors.Red)), cancellationToken: new(true)));
        Assert.Same(current, session.Current);
    }

    [Theory]
    [InlineData(1, "RGBY")] [InlineData(2, "GRYB")] [InlineData(3, "YBGR")] [InlineData(4, "BYRG")]
    [InlineData(5, "RBGY")] [InlineData(6, "BRYG")] [InlineData(7, "YGBR")] [InlineData(8, "GYRB")]
    public async Task ExifOrientationIsAppliedToNativePdfPixels(int orientation, string expected)
    {
        using var bitmap = new SKBitmap(32, 16);
        var colors = new[] { SKColors.Red, SKColors.Lime, SKColors.Blue, SKColors.Yellow };
        for (var y = 0; y < 16; y++) for (var x = 0; x < 32; x++) bitmap.SetPixel(x, y, colors[(y >= 8 ? 2 : 0) + (x >= 16 ? 1 : 0)]);
        using var image = SKImage.FromBitmap(bitmap); using var jpeg = image.Encode(SKEncodedImageFormat.Jpeg, 100);
        var encoded = WithOrientation(jpeg.ToArray(), orientation);
        using (var data = SKData.CreateCopy(encoded)) using (var codec = SKCodec.Create(data)) Assert.Equal(orientation, (int)codec.EncodedOrigin);
        var source = await Editor.CreateAsync(size: new(240, 240));
        var result = await Editor.ApplyAsync(source, [new AddImage(1, new(20, 20, 160, 80), new(encoded))]);
        var item = await First(result); Assert.Equal(orientation >= 5 ? 16 : 32, item.ImageInfo!.PixelWidth);
        Assert.Equal(orientation >= 5 ? 32 : 16, item.ImageInfo!.PixelHeight);
        var file = PdfFile.Open(Bytes(result)); var samples = file.Decode(SelectedImage(file, item.ResourceName!));
        var width = item.ImageInfo.PixelWidth; var height = item.ImageInfo.PixelHeight;
        var map = new Dictionary<char, SKColor> { ['R'] = SKColors.Red, ['G'] = SKColors.Lime, ['B'] = SKColors.Blue, ['Y'] = SKColors.Yellow };
        for (var i = 0; i < 4; i++)
        {
            var x = i % 2 == 0 ? width / 4 : width * 3 / 4; var y = i < 2 ? height / 4 : height * 3 / 4;
            var offset = (y * width + x) * 3; var color = map[expected[i]];
            Assert.InRange(Math.Abs(samples[offset] - color.Red), 0, 12);
            Assert.InRange(Math.Abs(samples[offset + 1] - color.Green), 0, 12);
            Assert.InRange(Math.Abs(samples[offset + 2] - color.Blue), 0, 12);
        }
    }

    private static byte[] WithOrientation(byte[] jpeg, int orientation)
    {
        // Synthetic EXIF metadata; no camera photograph or other binary fixture is committed.
        var exif = new byte[32]; "Exif\0\0"u8.CopyTo(exif);
        exif[6] = exif[7] = (byte)'I'; BinaryPrimitives.WriteUInt16LittleEndian(exif.AsSpan(8), 42);
        BinaryPrimitives.WriteUInt32LittleEndian(exif.AsSpan(10), 8); BinaryPrimitives.WriteUInt16LittleEndian(exif.AsSpan(14), 1);
        BinaryPrimitives.WriteUInt16LittleEndian(exif.AsSpan(16), 0x112); BinaryPrimitives.WriteUInt16LittleEndian(exif.AsSpan(18), 3);
        BinaryPrimitives.WriteUInt32LittleEndian(exif.AsSpan(20), 1); BinaryPrimitives.WriteUInt16LittleEndian(exif.AsSpan(24), (ushort)orientation);
        using var output = new MemoryStream(); output.Write(jpeg.AsSpan(0, 2)); output.WriteByte(0xff); output.WriteByte(0xe1);
        output.WriteByte(0); output.WriteByte((byte)(exif.Length + 2)); output.Write(exif); output.Write(jpeg.AsSpan(2)); return output.ToArray();
    }
}
