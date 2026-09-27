using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using ProPDF.Kernel;
using ProPDF.Core;
using ProPDF.Editing;
using ProPDF.Engine.PdfPig;
using ProPDF.Rendering.Skia;
using SkiaSharp;
using Xunit;

namespace ProPDF.Tests;

public sealed class EditingTests
{
    private readonly PdfPigBackend _backend = new();
    private ManagedPdfEditor Editor => new(_backend);

    [Fact]
    public async Task NativeTextRoundTripsThroughIndependentParser()
    {
        var document = await Editor.CreateAsync();
        var edited = await Editor.ApplyAsync(document, [new AddText(1, new PdfPoint(40, 60), "ProPDF native content"),
            new SetDocumentMetadata(new PdfMetadata("Round trip", "ProPDF"))]);
        var text = await _backend.GetPageTextAsync(edited, 1);
        Assert.Contains("ProPDF native content", text.Text);
        Assert.Equal("Round trip", edited.Metadata.Title);
        Assert.Equal("ProPDF", edited.Metadata.Author);
        Assert.Empty((await _backend.GetPageTextAsync(document, 1)).Text);
        var matches = await _backend.SearchAsync(edited, "NATIVE", new PdfSearchOptions(WholeWord: true));
        Assert.Single(matches);
        Assert.NotEmpty(matches[0].Bounds);
    }

    [Theory]
    [InlineData(0)] [InlineData(90)] [InlineData(180)] [InlineData(270)]
    public async Task InsertedVectorsHonorPageRotation(int rotation)
    {
        var document = await Editor.CreateAsync(size: new PdfSize(200, 300));
        var edited = await Editor.ApplyAsync(document, [new RotatePage(1, rotation),
            new AddShape(1, new PdfRect(20, 30, 40, 50), Fill: new PdfColor(255, 0, 0), Stroke: new PdfColor(255, 0, 0))]);
        await using var renderer = new SkiaPdfRenderer(_backend);
        using var tile = await renderer.RenderTileAsync(edited, new SkiaTileRequest(1, new PdfRect(0, 0, 100, 100), 1));
        using var pixels = SKBitmap.FromImage(tile.Image);
        Assert.True(pixels.GetPixel(40, 50).Red > 240 && pixels.GetPixel(40, 50).Green < 20);
        Assert.Equal(SKColors.White, pixels.GetPixel(5, 5));
    }

    [Fact]
    public async Task PageOrganizationMergeAndExtractionPreserveOrder()
    {
        var first = await Editor.CreateAsync(2, new PdfSize(200, 300));
        first = await Editor.ApplyAsync(first, [new AddText(1, new PdfPoint(20, 40), "First"), new AddText(2, new PdfPoint(20, 40), "Second")]);
        var moved = await Editor.ApplyAsync(first, [new MovePage(1, 2)]);
        Assert.Contains("Second", (await _backend.GetPageTextAsync(moved, 1)).Text);
        Assert.Contains("First", (await _backend.GetPageTextAsync(moved, 2)).Text);
        var merged = await Editor.ApplyAsync(moved, [new InsertDocumentPages(first, [2], 2), new DeletePage(3)]);
        Assert.Equal(2, merged.Pages.Count);
        Assert.Contains("Second", (await _backend.GetPageTextAsync(merged, 2)).Text);
        var extracted = await Editor.ExtractPagesAsync(first, [2, 1]);
        Assert.Contains("Second", (await _backend.GetPageTextAsync(extracted, 1)).Text);
        Assert.Contains("First", (await _backend.GetPageTextAsync(extracted, 2)).Text);
    }

    [Theory]
    [InlineData(PdfAnnotationKind.Note)] [InlineData(PdfAnnotationKind.FreeText)]
    [InlineData(PdfAnnotationKind.Highlight)] [InlineData(PdfAnnotationKind.Underline)]
    [InlineData(PdfAnnotationKind.StrikeOut)] [InlineData(PdfAnnotationKind.Rectangle)]
    [InlineData(PdfAnnotationKind.Ellipse)] [InlineData(PdfAnnotationKind.Link)]
    public async Task NativeAnnotationsHavePersistentIdentityAndCanBeDeleted(PdfAnnotationKind kind)
    {
        var document = await Editor.CreateAsync();
        var edited = await Editor.ApplyAsync(document, [new AddAnnotation(1, new PdfRect(20, 30, 100, 40), kind, "Review", "Tester", Uri: "https://example.org")]);
        var info = await Editor.InspectAsync(edited);
        var annotation = Assert.Single(info.Annotations);
        Assert.Equal("Review", annotation.Contents);
        Assert.Equal("Tester", annotation.Author);
        Assert.Equal(20, annotation.Bounds.X, 3);
        Assert.Equal(30, annotation.Bounds.Y, 3);
        var removed = await Editor.ApplyAsync(edited, [new DeleteAnnotation(1, annotation.Id)]);
        Assert.Empty((await Editor.InspectAsync(removed)).Annotations);
    }

    [Fact]
    public async Task InkIsStoredAsNativeAnnotation()
    {
        var document = await Editor.CreateAsync();
        var edited = await Editor.ApplyAsync(document, [new AddInkAnnotation(1, [new PdfPoint(20, 30), new PdfPoint(80, 60), new PdfPoint(90, 20)])]);
        Assert.Equal("Ink", Assert.Single((await Editor.InspectAsync(edited)).Annotations).Kind);
    }

    [Theory]
    [InlineData(PdfFormFieldKind.Text)] [InlineData(PdfFormFieldKind.ComboBox)] [InlineData(PdfFormFieldKind.ListBox)]
    public async Task FormsCanBeAuthoredFilledAndFlattened(PdfFormFieldKind kind)
    {
        var document = await Editor.CreateAsync();
        var edited = await Editor.ApplyAsync(document,
            [new AddFormField(1, new PdfRect(30, 40, 180, 40), "Name", kind, choices: ["Ada", "Grace"]), new SetFormValue("Name", "Ada")]);
        var field = Assert.Single((await Editor.InspectAsync(edited)).Fields);
        Assert.Equal("Ada", field.Value);
        var flattened = await Editor.ApplyAsync(edited, [new FlattenForms()]);
        Assert.Empty((await Editor.InspectAsync(flattened)).Fields);
        Assert.Contains("Ada", (await _backend.GetPageTextAsync(flattened, 1)).Text);
    }

    [Fact]
    public async Task ReadOnlyFieldsRejectChanges()
    {
        var document = await Editor.CreateAsync();
        var edited = await Editor.ApplyAsync(document, [new AddFormField(1, new PdfRect(30, 40, 180, 40), "Locked", value: "Initial", readOnly: true)]);
        await Assert.ThrowsAsync<InvalidOperationException>(() => Editor.ApplyAsync(edited, [new SetFormValue("Locked", "Changed")]));
    }

    [Fact]
    public async Task RedactionActuallyRemovesTextRatherThanPaintingOverIt()
    {
        var document = await Editor.CreateAsync(size: new PdfSize(300, 300));
        var withText = await Editor.ApplyAsync(document, [new AddText(1, new PdfPoint(20, 60), "SECRET-471829"),
            new AddText(1, new PdfPoint(20, 160), "Keep this text")]);
        var redacted = await Editor.ApplyAsync(withText, [new RedactRegion(1, new PdfRect(10, 30, 200, 50))]);
        Assert.DoesNotContain("SECRET", (await _backend.GetPageTextAsync(redacted, 1)).Text);
        Assert.Contains("Keep this text", (await _backend.GetPageTextAsync(redacted, 1)).Text);
        using var stream = redacted.OpenRead();
        using var all = new MemoryStream(); stream.CopyTo(all);
        var independent = PdfFile.Open(all.ToArray());
        foreach (var item in independent.EnumerateObjects())
            if (item.Value is PdfStream pdfStream)
                Assert.DoesNotContain("SECRET-471829", Encoding.Latin1.GetString(independent.Decode(pdfStream)));
        await using var renderer = new SkiaPdfRenderer(_backend);
        using var tile = await renderer.RenderTileAsync(redacted, new SkiaTileRequest(1, new PdfRect(0, 0, 220, 100), 1));
        using var pixels = SKBitmap.FromImage(tile.Image);
        Assert.Equal(SKColors.Black, pixels.GetPixel(30, 50));
    }

    [Fact]
    public async Task RegionReplacementRemovesOriginalAndWritesReplacement()
    {
        var document = await Editor.CreateAsync();
        document = await Editor.ApplyAsync(document, [new AddText(1, new PdfPoint(30, 60), "Old sentence")]);
        var edited = await Editor.ApplyAsync(document, [new ReplaceRegionText(1, new PdfRect(20, 35, 220, 50), "New sentence")]);
        var text = (await _backend.GetPageTextAsync(edited, 1)).Text;
        Assert.DoesNotContain("Old", text);
        Assert.Contains("New sentence", text);
    }

    [Fact]
    public async Task AttachmentsAndBookmarksRoundTrip()
    {
        var document = await Editor.CreateAsync();
        var bytes = Encoding.UTF8.GetBytes("ProPDF attachment fixture");
        var edited = await Editor.ApplyAsync(document, [new AddAttachment("notes.txt", new PdfBinaryAsset(bytes), "text/plain"), new AddBookmark("Introduction", 1)]);
        var info = await Editor.InspectAsync(edited);
        Assert.Equal("notes.txt", Assert.Single(info.Attachments).Name);
        Assert.Equal("Introduction", Assert.Single(info.Bookmarks).Title);
        Assert.Equal(bytes, (await Editor.ReadAttachmentAsync(edited, "notes.txt")).ToArray());
        var removed = await Editor.ApplyAsync(edited, [new RemoveAttachment("notes.txt"), new RemoveBookmark(0)]);
        Assert.Empty((await Editor.InspectAsync(removed)).Attachments);
        Assert.Empty((await Editor.InspectAsync(removed)).Bookmarks);
    }

    [Fact]
    public async Task Aes256RequiresPasswordAndEnforcesOwnerEditingPermissions()
    {
        var document = await Editor.CreateAsync();
        var encrypted = await Editor.ApplyAsync(document, [new ChangeEncryption(new PdfEncryptionSettings("reader-secret", "owner-secret-123"))]);
        Assert.True((await Editor.InspectAsync(encrypted)).IsEncrypted);
        using (var input = encrypted.OpenRead()) await Assert.ThrowsAnyAsync<Exception>(() => _backend.OpenAsync(input));
        PdfSnapshot readerView;
        using (var input = encrypted.OpenRead()) readerView = await _backend.OpenAsync(input, new PdfOpenOptions { Password = "reader-secret" });
        await Assert.ThrowsAnyAsync<Exception>(() => Editor.ApplyAsync(readerView, [new RotatePage(1)]));
        var decrypted = await Editor.ApplyAsync(encrypted, [new ChangeEncryption(null)]);
        Assert.False((await Editor.InspectAsync(decrypted)).IsEncrypted);
        Assert.Null(decrypted.GetPassword());
    }

    [Fact]
    public async Task TransactionFailureNeverPublishesPartialNativeEdits()
    {
        var document = await Editor.CreateAsync();
        var session = new PdfSession(_backend, Editor);
        using (var input = document.OpenRead()) await session.OpenAsync(input);
        var revision = session.Current;
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => session.ApplyAsync([new AddText(1, new PdfPoint(20, 40), "Should roll back"), new RotatePage(999)]));
        Assert.Same(revision, session.Current);
        Assert.False(session.IsDirty);
        Assert.Empty((await _backend.GetPageTextAsync(session.Current!, 1)).Text);
    }

    [Fact]
    public async Task NativeCancellationLeavesSourceUntouched()
    {
        var document = await Editor.CreateAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Editor.ApplyAsync(document, [new RotatePage(1)], new CancellationToken(true)));
        Assert.Single(document.Pages);
    }

    [Fact]
    public async Task DigitalSignatureIsCryptographicallyVerifiableAndBlocksUnsafeRewrite()
    {
        using var key = RSA.Create(2048);
        var request = new CertificateRequest("CN=ProPDF ephemeral test", key, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        using var certificate = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddMinutes(-5), DateTimeOffset.UtcNow.AddDays(1));
        var chain = new[] { certificate };
        var document = await Editor.CreateAsync();
        var signing = new PdfSignatureService(_backend);
        var signed = await signing.SignAsync(document, new DotNetRsaSignature(key), chain, "Approval", "Regression test");
        var result = Assert.Single(await signing.VerifyIntegrityAsync(signed));
        Assert.True(result.CryptographicallyValid, result.Error);
        Assert.True(result.CoversCurrentDocument);
        await Assert.ThrowsAsync<NotSupportedException>(() => Editor.ApplyAsync(signed, [new RotatePage(1)]));
    }
}
