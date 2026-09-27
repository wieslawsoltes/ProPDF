using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
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

public sealed class OwnedEditingTests
{
    private readonly PdfPigBackend _independent = new();
    private ManagedPdfEditor Editor => new(_independent);

    [Fact]
    public async Task OwnedLoaderAndWriterRequireNoThirdPartyPdfEngine()
    {
        var editor = new ManagedPdfEditor(); var source = await editor.CreateAsync(2, new PdfSize(200, 300));
        var result = await editor.ApplyAsync(source, [new AddText(1, new PdfPoint(20, 60), "Owned kernel"), new RotatePage(2), new AddBookmark("Chapter", 2)]);
        Assert.Equal(300, result.Pages[1].Size.Width);
        Assert.Equal("Chapter", Assert.Single((await editor.InspectAsync(result)).Bookmarks).Title);
        using var stream = result.OpenRead(); var independent = await _independent.OpenAsync(stream);
        Assert.Contains("Owned kernel", (await _independent.GetPageTextAsync(independent, 1)).Text);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Aes256EncryptedEditsPreserveAccessAndReopenIndependently(bool allowCopy)
    {
        var document = await Editor.CreateAsync();
        document = await Editor.ApplyAsync(document, [new AddText(1, new PdfPoint(20, 60), "Protected native content"),
            new ChangeEncryption(new PdfEncryptionSettings("reader-password", "owner-password", allowCopy: allowCopy))]);
        document = await Editor.ApplyAsync(document, [new SetDocumentMetadata(new PdfMetadata("Encrypted title")), new AddText(1, new PdfPoint(20, 120), "Owner revision")]);
        var bytes = await Bytes(document); Assert.DoesNotContain("Protected native content", Encoding.Latin1.GetString(bytes));
        var owner = PdfFile.Open(bytes, "owner-password"); Assert.True(owner.IsOwnerAuthorized); Assert.True(owner.IsEncrypted);
        Assert.Equal(8, owner.Dictionary(owner.Dictionary(owner.Catalog["Extensions"])["ADBE"]).Number("ExtensionLevel"));
        var reader = PdfFile.Open(bytes, "reader-password"); Assert.False(reader.IsOwnerAuthorized);
        Assert.Throws<UnauthorizedAccessException>(() => reader.Save());
        Assert.Throws<UnauthorizedAccessException>(() => PdfFile.Open(bytes, "wrong-password"));
        using var input = new MemoryStream(bytes); var independent = await _independent.OpenAsync(input, new PdfOpenOptions { Password = "reader-password" });
        Assert.Contains("Owner revision", (await _independent.GetPageTextAsync(independent, 1)).Text);
        Assert.Equal("Encrypted title", independent.Metadata.Title);
        var permission = (int)owner.Dictionary(owner.Trailer["Encrypt"])["P"].AsNumber();
        Assert.Equal(allowCopy, (permission & 16) != 0);
    }

    [Fact]
    public async Task UnsupportedInternationalPasswordPreparationIsExplicitAndAtomic()
    {
        var document = await Editor.CreateAsync(); var session = new PdfSession(_independent, Editor);
        using (var input = document.OpenRead()) await session.OpenAsync(input);
        var before = session.Current;
        await Assert.ThrowsAsync<NotSupportedException>(() => session.ApplyAsync(new ChangeEncryption(new PdfEncryptionSettings("żółć", "owner-password"))));
        Assert.Same(before, session.Current); Assert.False(session.IsDirty);
    }

    [Fact]
    public async Task ImportedFieldsReceiveCollisionSafeNamesAndRetainValues()
    {
        var source = await Editor.CreateAsync(2);
        source = await Editor.ApplyAsync(source, [new AddFormField(1, new PdfRect(20, 30, 150, 30), "Name", value: "Ada"),
            new AddFormField(2, new PdfRect(20, 30, 150, 30), "Other", value: "Grace")]);
        var result = await Editor.ApplyAsync(source, [new InsertDocumentPages(source, [1, 1, 2], 3)]);
        var fields = (await Editor.InspectAsync(result)).Fields;
        Assert.Equal(5, fields.Count); Assert.Equal(5, fields.Select(f => f.Name).Distinct(StringComparer.Ordinal).Count());
        Assert.Equal(3, fields.Count(f => f.Value == "Ada"));
        var flattened = await Editor.ApplyAsync(result, [new FlattenForms()]);
        Assert.Empty((await Editor.InspectAsync(flattened)).Fields);
        Assert.Contains("Ada", (await _independent.GetPageTextAsync(flattened, 3)).Text);
    }

    [Fact]
    public async Task DeletingAWidgetPageRemovesOnlyItsFields()
    {
        var document = await Editor.CreateAsync(2);
        document = await Editor.ApplyAsync(document, [new AddFormField(1, new PdfRect(20, 30, 150, 30), "First"),
            new AddFormField(1, new PdfRect(20, 90, 150, 30), "Second"), new AddFormField(2, new PdfRect(20, 30, 150, 30), "Keep")]);
        var result = await Editor.ApplyAsync(document, [new DeletePage(1)]);
        Assert.Equal("Keep", Assert.Single((await Editor.InspectAsync(result)).Fields).Name);
    }

    [Fact]
    public async Task NativeRedactionDropsIntersectingImageInvocationAndPrunesUnusedImageBytes()
    {
        using var bitmap = new SKBitmap(16, 16); bitmap.Erase(SKColors.Magenta);
        using var image = SKImage.FromBitmap(bitmap); using var encoded = image.Encode(SKEncodedImageFormat.Png, 100);
        var document = await Editor.CreateAsync(size: new PdfSize(200, 300));
        document = await Editor.ApplyAsync(document, [new AddImage(1, new PdfRect(20, 30, 60, 60), new PdfBinaryAsset(encoded.ToArray())), new AddText(1, new PdfPoint(20, 160), "Preserved")]);
        var result = await Editor.ApplyAsync(document, [new RedactRegion(1, new PdfRect(30, 40, 15, 15))]);
        var file = PdfFile.Open(await Bytes(result));
        Assert.DoesNotContain(file.EnumerateObjects(), item => item.Value is PdfStream stream && stream.Dictionary.Name("Subtype") == "Image");
        Assert.Contains("Preserved", (await _independent.GetPageTextAsync(result, 1)).Text);
        await using var renderer = new SkiaPdfRenderer(_independent); using var tile = await renderer.RenderTileAsync(result, new SkiaTileRequest(1, new PdfRect(0, 0, 100, 100), 1));
        using var pixels = SKBitmap.FromImage(tile.Image); Assert.Equal(SKColors.Black, pixels.GetPixel(35, 45)); Assert.Equal(SKColors.White, pixels.GetPixel(65, 65));
    }

    [Fact]
    public async Task NativeRedactionPreservesTextStateWithoutKeepingRemovedStrings()
    {
        var source = await Editor.CreateAsync(size: new PdfSize(300, 300)); var file = PdfFile.Open(await Bytes(source));
        var page = Page(file); var font = file.Add(Dictionary(("Type", new PdfName("Font")), ("Subtype", new PdfName("Type1")), ("BaseFont", new PdfName("Helvetica")), ("Encoding", new PdfName("WinAnsiEncoding"))));
        page["Resources"] = Dictionary(("Font", Dictionary(("F1", font))));
        page["Contents"] = file.Add(PdfStream.FromDecoded("BT /F1 12 Tf 14 TL 1 0 0 1 20 250 Tm 3 2 (REMOVE SECRET) \" ET\nBT 1 0 0 1 20 100 Tm (Keep spaced) Tj ET\n"u8));
        using var input = new MemoryStream(file.Save()); var document = await _independent.OpenAsync(input);
        var result = await Editor.ApplyAsync(document, [new RedactRegion(1, new PdfRect(10, 45, 200, 35))]);
        var text = (await _independent.GetPageTextAsync(result, 1)).Text;
        Assert.DoesNotContain("SECRET", text); Assert.Contains("Keep", text);
        var reopened = PdfFile.Open(await Bytes(result));
        var pageBytes = PageContents(reopened); var operations = PdfContent.Read(pageBytes);
        Assert.Contains(operations, op => op.Operator == "Tw"); Assert.Contains(operations, op => op.Operator == "Tc");
        Assert.DoesNotContain("SECRET", Encoding.ASCII.GetString(pageBytes));
    }

    [Fact]
    public async Task SemanticAndSoftMaskRedactionAreRefusedWithoutPublishingPartialOutput()
    {
        var source = await Editor.CreateAsync(size: new PdfSize(200, 300));
        foreach (var mode in new[] { "tagged", "ActualText", "softmask" })
        {
            var file = PdfFile.Open(await Bytes(source)); var page = Page(file);
            if (mode == "tagged") file.Catalog["StructTreeRoot"] = file.Add(Dictionary(("Type", new PdfName("StructTreeRoot"))));
            if (mode == "ActualText") page["Contents"] = file.Add(PdfStream.FromDecoded("/Span << /ActualText (secret) >> BDC 0 0 10 10 re f EMC"u8));
            if (mode == "softmask")
            {
                page["Resources"] = Dictionary(("ExtGState", Dictionary(("GS1", Dictionary(("SMask", new PdfDictionary()))))));
                page["Contents"] = file.Add(PdfStream.FromDecoded("/GS1 gs 0 0 10 10 re f"u8));
            }
            using var input = new MemoryStream(file.Save()); var document = await new ManagedPdfLoader().OpenAsync(input);
            var session = new PdfSession(new ManagedPdfLoader(), new ManagedPdfEditor());
            using (var loaded = document.OpenRead()) await session.OpenAsync(loaded);
            var before = session.Current;
            await Assert.ThrowsAsync<NotSupportedException>(() => session.ApplyAsync(new RedactRegion(1, new PdfRect(10, 10, 50, 50))));
            Assert.Same(before, session.Current);
        }
    }

    [Fact]
    public async Task AppendSigningRetainsPriorRevisionAndDetectsTampering()
    {
        using var key = RSA.Create(2048); using var certificate = Certificate(key);
        var service = new PdfSignatureService(_independent); var document = await Editor.CreateAsync();
        var original = await Bytes(document);
        var signed = await service.SignAsync(document, new DotNetRsaSignature(key), [certificate], "First");
        Assert.True((await Bytes(signed)).AsSpan().StartsWith(original));
        var second = await service.SignAsync(signed, new DotNetRsaSignature(key), [certificate], "Second");
        var results = await service.VerifyIntegrityAsync(second);
        Assert.Equal(2, results.Count); Assert.All(results, result => Assert.True(result.CryptographicallyValid, result.Error));
        Assert.False(results[0].CoversCurrentDocument); Assert.True(results[1].CoversCurrentDocument);
        Assert.Equal(2, results[0].SignedRevision); Assert.Equal(3, results[1].SignedRevision); Assert.All(results, result => Assert.Equal(3, result.TotalRevisions));
        var tampered = await Bytes(second); tampered[7] = (byte)'6';
        using var input = new MemoryStream(tampered); var changed = await _independent.OpenAsync(input);
        Assert.All(await service.VerifyIntegrityAsync(changed), result => Assert.False(result.CryptographicallyValid));
        await Assert.ThrowsAsync<NotSupportedException>(() => Editor.ApplyAsync(second, [new RotatePage(1)]));
    }

    [Fact]
    public async Task EncryptedPdfCanBeSignedWithOwnerAuthorization()
    {
        using var key = RSA.Create(2048); using var certificate = Certificate(key);
        var document = await Editor.CreateAsync();
        document = await Editor.ApplyAsync(document, [new ChangeEncryption(new PdfEncryptionSettings("reader-password", "owner-password"))]);
        var service = new PdfSignatureService(_independent);
        var result = await service.SignAsync(document, new DotNetRsaSignature(key), [certificate], "Approval");
        var signature = Assert.Single(await service.VerifyIntegrityAsync(result)); Assert.True(signature.CryptographicallyValid, signature.Error);
        Assert.True(signature.CoversCurrentDocument); Assert.True((await Editor.InspectAsync(result)).IsEncrypted);
    }

    [Fact]
    public async Task EcdsaProviderUsesHostKeyWithoutPdfVendorAdapters()
    {
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var request = new CertificateRequest("CN=ProPDF ECDSA fixture", key, HashAlgorithmName.SHA256);
        using var certificate = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddMinutes(-5), DateTimeOffset.UtcNow.AddDays(1));
        var service = new PdfSignatureService(_independent);
        var signed = await service.SignAsync(await Editor.CreateAsync(), new DotNetEcdsaSignature(key), [certificate], "ECDSA");
        var signature = Assert.Single(await service.VerifyIntegrityAsync(signed)); Assert.True(signature.CryptographicallyValid, signature.Error);
    }

    private static X509Certificate2 Certificate(RSA key) => new CertificateRequest("CN=ProPDF owned signature fixture", key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1)
        .CreateSelfSigned(DateTimeOffset.UtcNow.AddMinutes(-5), DateTimeOffset.UtcNow.AddDays(1));
    internal static async Task<byte[]> Bytes(PdfSnapshot document) { using var source = document.OpenRead(); using var output = new MemoryStream(); await source.CopyToAsync(output); return output.ToArray(); }
    internal static PdfDictionary Page(PdfFile file) => file.Dictionary(file.Array(file.Dictionary(file.Catalog["Pages"])["Kids"])[0]);
    internal static byte[] PageContents(PdfFile file)
    {
        var raw = file.Resolve(Page(file)["Contents"]); var streams = raw is PdfArray array ? array.ToArray() : [raw];
        using var output = new MemoryStream(); foreach (var item in streams) { output.Write(file.Decode((PdfStream)file.Resolve(item))); output.WriteByte(10); }
        return output.ToArray();
    }
}
internal static class PdfTestObjectExtensions
{
    public static double AsNumber(this PdfObject value) => Assert.IsType<PdfNumber>(value).Value;
}
