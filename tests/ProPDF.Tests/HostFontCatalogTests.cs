using ProPDF.Core;
using ProPDF.Editing;
using ProPDF.Engine.PdfPig;
using ProPDF.Rendering.Skia;
using SkiaSharp;
using Xunit;
using static ProPDF.Tests.FontEmbeddingTests;

namespace ProPDF.Tests;

public sealed class HostFontCatalogTests
{
    private static PdfFontFace Face(string family = "Owned Test", bool bold = false, bool italic = false, int height = 700)
        => new(family, TinyTrueType.Create(glyphTop: height), bold, italic);

    [Fact]
    public void InputProgramsAreCopiedAndAliasesDoNotDuplicateProgramBytes()
    {
        var program = TinyTrueType.Create(); var face = new PdfFontFace("Owned Test", program);
        Array.Fill<byte>(program, 0);
        using var reopened = face.Open(); Assert.True(reopened.ContainsGlyphs("AΩ😀"));
        var catalog = new PdfFontCatalog([face], aliases: new Dictionary<string,string> { ["Helvetica"] = "Owned Test", ["Arial"] = "Owned Test" });
        Assert.Same(face, catalog.FindFace("ABCDEF+Helvetica-Regular")); Assert.Same(face, catalog.FindFace("Arial"));
        Assert.Equal(1, catalog.Count); Assert.Equal(face.ByteLength, catalog.ByteLength);
        Assert.Null(catalog.FindFace("Courier")); Assert.Null(catalog.FindFace("Symbol"));
    }

    [Theory]
    [InlineData(false,false)] [InlineData(true,false)] [InlineData(false,true)] [InlineData(true,true)]
    public void AllFourStylesResolveIndependentlyWithoutFakeRegularFallback(bool bold, bool italic)
    {
        var faces = new[] { Face(), Face(bold:true), Face(italic:true), Face(bold:true,italic:true) };
        var catalog = new PdfFontCatalog(faces, "Owned Test");
        Assert.Same(faces.Single(f => f.Bold == bold && f.Italic == italic), catalog.FindFace("Unknown", bold, italic));
        Assert.Null(new PdfFontCatalog([faces[0]], "Owned Test").FindFace("Unknown", true, false));
    }

    [Fact]
    public void InvalidCatalogsFailAtConstruction()
    {
        Assert.Throws<ArgumentNullException>(() => new PdfFontCatalog(null!));
        Assert.Throws<ArgumentException>(() => new PdfFontCatalog([Face(), Face("owned test")]));
        Assert.Throws<ArgumentException>(() => new PdfFontCatalog([Face()], "absent"));
        Assert.Throws<ArgumentNullException>(() => new PdfFontCatalog([null!]));
        Assert.Throws<ArgumentException>(() => new PdfFontCatalog([Face()], aliases: new Dictionary<string,string> { ["A"]="absent" }));
        Assert.Throws<ArgumentException>(() => new PdfFontCatalog([Face()], aliases: new Dictionary<string,string> { ["Owned Test"]="Owned Test" }));
        Assert.Throws<ArgumentException>(() => new PdfFontCatalog([Face()], aliases: new Dictionary<string,string> { ["A"]="B", ["B"]="Owned Test" }));
    }

    [Theory]
    [InlineData(0)] [InlineData(11)] [InlineData(16*1024*1024+1)]
    public void ProgramSizeIsBoundedBeforeNativeDecode(int length)
        => Assert.Throws<ArgumentOutOfRangeException>(() => new PdfFontFace("Test", new byte[length]));

    [Theory]
    [InlineData("")] [InlineData(" ")] [InlineData("bad\0name")]
    public void InvalidFamilyIsRejected(string family)
        => Assert.ThrowsAny<ArgumentException>(() => new PdfFontFace(family, TinyTrueType.Create()));

    [Fact]
    public void BrokenFontProgramIsRejectedInsteadOfBecomingASystemDefault()
        => Assert.Throws<InvalidDataException>(() => new PdfFontFace("Test", new byte[128]));

    [Fact]
    public void CatalogEnumerationStopsAtItsBudget()
    {
        var visits = 0; var program = TinyTrueType.Create();
        IEnumerable<PdfFontFace> Infinite() { while (true) yield return new PdfFontFace("Face" + visits++, program); }
        Assert.Throws<ArgumentOutOfRangeException>(() => new PdfFontCatalog(Infinite())); Assert.Equal(65, visits);
    }

    [Fact]
    public async Task SuppliedStylesProduceIndependentGeometricInkAndLeavePdfUnchanged()
    {
        var catalog = new PdfFontCatalog([Face(height:400), Face(bold:true,height:700)], aliases: new Dictionary<string,string> { ["Helvetica"]="Owned Test" });
        var backend = new PdfPigBackend(catalog);
        var editor = new ManagedPdfEditor(backend);
        var blank = await editor.CreateAsync(size: new(180,180));
        var pdf = await editor.ApplyAsync(blank, [new AddText(1,new(20,60),"A",40),new AddText(1,new(20,140),"A",40,"Helvetica-Bold")]);
        var bytes = Bytes(pdf);
        using var pixels = await Raster(backend,pdf);
        Assert.Equal(SKColors.Black, pixels.GetPixel(30,50));
        Assert.Equal(SKColors.White, pixels.GetPixel(30,40)); // regular glyph starts at y44
        Assert.Equal(SKColors.Black, pixels.GetPixel(30,120)); // bold starts at y112
        Assert.Equal(bytes,Bytes(pdf)); Assert.Equal("AA",(await backend.GetPageTextAsync(pdf,1)).Text.Replace("\n","").Replace(" ",""));
    }

    [Fact]
    public async Task EmbeddedOutlinesTakePrecedenceOverHostSubstitution()
    {
        var backend = new PdfPigBackend(new PdfFontCatalog([Face(height:700)], "Owned Test"));
        var editor = new ManagedPdfEditor(backend); var blank = await editor.CreateAsync(size:new(160,100));
        var pdf = await editor.ApplyAsync(blank,[new AddText(1,new(20,60),"A",40,EmbeddedFont:new PdfBinaryAsset(TinyTrueType.Create(glyphTop:300)))]);
        using var pixels = await Raster(backend,pdf);
        Assert.Equal(SKColors.White,pixels.GetPixel(30,40)); Assert.Equal(SKColors.Black,pixels.GetPixel(30,54));
    }

    [Fact]
    public async Task SeparateBackendCatalogsNeverShareFontConfiguration()
    {
        var editor = new ManagedPdfEditor(); var blank=await editor.CreateAsync(size:new(160,100));
        var pdf=await editor.ApplyAsync(blank,[new AddText(1,new(20,60),"A",40)]);
        var shortBackend = new PdfPigBackend(new PdfFontCatalog([Face(height:300)],"Owned Test"));
        var tallBackend = new PdfPigBackend(new PdfFontCatalog([Face(height:700)],"Owned Test"));
        await Task.WhenAll(Enumerable.Range(0,8).Select(async i =>
        {
            using var pixels=await Raster(i%2==0?shortBackend:tallBackend,pdf);
            Assert.Equal(i%2==0?SKColors.White:SKColors.Black,pixels.GetPixel(30,40));
        }));
    }

    [Fact]
    public async Task FirstMissingGlyphCannotShadowExplicitFaceForLaterCoveredGlyphs()
    {
        var backend = new PdfPigBackend(new PdfFontCatalog([Face()], "Owned Test"));
        var editor = new ManagedPdfEditor(backend);var blank=await editor.CreateAsync(size:new(180,100));
        var pdf=await editor.ApplyAsync(blank,[new AddText(1,new(20,60),"BA",40)]);
        using var pixels=await Raster(backend,pdf);
        // B needs a system fallback. The following A must still be our solid test rectangle.
        Assert.Equal(SKColors.Black,pixels.GetPixel(60,40));
    }

    private static byte[] Bytes(PdfSnapshot pdf) { using var input=pdf.OpenRead(); using var buffer=new MemoryStream();input.CopyTo(buffer);return buffer.ToArray(); }
    private static async Task<SKBitmap> Raster(PdfPigBackend backend,PdfSnapshot pdf)
    {
        await using var renderer=new SkiaPdfRenderer(backend);
        using var image=await new PdfRasterExporter(renderer).RenderPageAsync(pdf,1,new(Dpi:72));
        return SKBitmap.FromImage(image);
    }
}
