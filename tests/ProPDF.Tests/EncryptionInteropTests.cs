using System.Text;
using System.Text.Json;
using ProPDF.Core;
using ProPDF.Editing;
using ProPDF.Engine.PdfPig;
using ProPDF.Kernel;
using Xunit;

namespace ProPDF.Tests;

public sealed class EncryptionInteropTests
{
    [Theory]
    [InlineData("RC4-40")]
    [InlineData("RC4-128")]
    [InlineData("AES-128")]
    [InlineData("AES-256-R5")]
    [InlineData("AES-256")]
    public async Task IndependentlyProducedStandardSecurityFilesOpenAndRoundTrip(string algorithm)
    {
        using var fixtures = JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(AppContext.BaseDirectory, "Fixtures/encryption.json")));
        var bytes = Convert.FromBase64String(fixtures.RootElement.GetProperty("fixtures").GetProperty(algorithm).GetString()!);
        var owner = PdfFile.Open(bytes, "owner-password");
        Assert.True(owner.IsOwnerAuthorized);
        var reader = PdfFile.Open(bytes, "reader-password");
        Assert.False(reader.IsOwnerAuthorized);
        Assert.Throws<UnauthorizedAccessException>(() => reader.Save());
        Assert.Throws<UnauthorizedAccessException>(() => PdfFile.Open(bytes, "not-a-password"));
        Assert.Contains("Independent cipher fixture", Encoding.ASCII.GetString(OwnedEditingTests.PageContents(owner)));
        var backend = new PdfPigBackend();
        using var input = new MemoryStream(owner.Save());
        var reopened = await backend.OpenAsync(input, new PdfOpenOptions { Password = "reader-password" });
        Assert.Equal("Independent encryption fixture", reopened.Metadata.Title);
        Assert.Contains("Independent cipher fixture", (await backend.GetPageTextAsync(reopened, 1)).Text);
        // Owner-authorized decryption removes encryption and preserves the original content.
        using var original = new MemoryStream(bytes);
        var snapshot = await new ManagedPdfLoader().OpenAsync(original, new PdfOpenOptions { Password = "owner-password" });
        var plain = await new ManagedPdfEditor(backend).ApplyAsync(snapshot, [new ChangeEncryption(null)]);
        Assert.False(PdfFile.Open(await OwnedEditingTests.Bytes(plain)).IsEncrypted);
        Assert.Contains("Independent cipher fixture", (await backend.GetPageTextAsync(plain, 1)).Text);
    }
}
