using System.Runtime.InteropServices.JavaScript;
using System.Text.Json;
using ProPDF.Core;
using ProPDF.Presentation;
using ProPDF.SampleSupport;

namespace ProPDF.Uno.Sample;

internal static partial class PlatformHost
{
    private static bool _initialized;
    public static async Task InitializeAsync()
    {
        using var document = JSHost.GlobalThis.GetPropertyAsJSObject("document")
            ?? throw new InvalidOperationException("The browser document is unavailable.");
        var baseUri = document.GetPropertyAsString("baseURI") ?? throw new InvalidOperationException("Browser document has no base URI.");
        await JSHost.ImportAsync("ProPDFBrowser", new Uri(new Uri(baseUri), "browser-host.js").AbsoluteUri);
        _initialized = true;
    }
    [JSImport("pickFile", "ProPDFBrowser")]
    internal static partial Task<string?> PickAsync(string accept, int maximumBytes);
    [JSImport("downloadFile", "ProPDFBrowser")]
    internal static partial void Download(string name, string mime, string base64);
    [JSImport("setDirty", "ProPDFBrowser")]
    internal static partial void SetDirty(bool dirty);
    [JSImport("ready", "ProPDFBrowser")]
    private static partial Task MarkReadyAsync();
    [JSImport("failure", "ProPDFBrowser")]
    private static partial void ReportFailure(string error);
    public static IPdfUnoFiles CreateFiles() => new BrowserFiles();
    public static async Task ReadyAsync(PdfEditor editor, DemoWorkspace runtime)
    { BrowserTest.Initialize(editor, runtime); await MarkReadyAsync(); }
    public static void Failed(string error) { if (_initialized) ReportFailure(error); else Console.Error.WriteLine(error); }
}

internal sealed class BrowserFiles : IPdfUnoFiles
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "propdf-browser-" + Guid.NewGuid().ToString("N"));
    private readonly Dictionary<string, string> _exports = new(StringComparer.Ordinal);
    private long _imported;
    private bool _disposed;
    public async Task<string?> PickOpenPathAsync(PdfFileKind kind, CancellationToken token)
    {
        ObjectDisposedException.ThrowIf(_disposed, this); token.ThrowIfCancellationRequested();
        var json = await PlatformHost.PickAsync(kind switch { PdfFileKind.Pdf => ".pdf,application/pdf", PdfFileKind.Image => "image/png,image/jpeg,image/bmp,image/tiff", _ => "" }, 32 * 1024 * 1024);
        token.ThrowIfCancellationRequested(); if (json is null) return null;
        using var envelope = JsonDocument.Parse(json);
        var name = envelope.RootElement.GetProperty("name").GetString() ?? "document.pdf";
        var bytes = Convert.FromBase64String(envelope.RootElement.GetProperty("base64").GetString() ?? "");
        if (bytes.Length > 32 * 1024 * 1024 || _imported + bytes.Length > 128L * 1024 * 1024) throw new InvalidDataException("Browser import budget exceeded. Reload after downloading your edits.");
        var path = PathFor(name); await File.WriteAllBytesAsync(path, bytes, token); _imported += bytes.Length; return path;
    }
    public Task<string?> PickSavePathAsync(string suggestedName, CancellationToken token)
    {
        ObjectDisposedException.ThrowIf(_disposed, this); token.ThrowIfCancellationRequested();
        if (_exports.Count >= 64) throw new InvalidOperationException("Too many pending exports. Reload after saving your document.");
        var name = SafeName(suggestedName); var path = PathFor(name); _exports.Add(path, name); return Task.FromResult<string?>(path);
    }
    public async Task PublishFileAsync(string path, CancellationToken token)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (!_exports.TryGetValue(path, out var name)) throw new InvalidOperationException("This export has no registered browser destination.");
        await using var input = File.OpenRead(path);
        var bytes = await PdfStreams.ReadBoundedAsync(input, 64L * 1024 * 1024, token);
        token.ThrowIfCancellationRequested();
        var mime = Path.GetExtension(name).ToLowerInvariant() switch { ".pdf" => "application/pdf", ".png" => "image/png", ".jpg" => "image/jpeg", ".json" => "application/json", ".txt" => "text/plain;charset=utf-8", _ => "application/octet-stream" };
        PlatformHost.Download(name, mime, Convert.ToBase64String(bytes));
        _exports.Remove(path); File.Delete(path);
    }
    private string PathFor(string name)
    { var directory = Path.Combine(_directory, Guid.NewGuid().ToString("N")); Directory.CreateDirectory(directory); return Path.Combine(directory, SafeName(name)); }
    private static string SafeName(string name)
    {
        name = Path.GetFileName(name.Replace('\\', '/'));
        var result = new string(name.Where(c => !char.IsControl(c) && c is not ':' and not '*' and not '?' and not '"' and not '<' and not '>' and not '|').Take(180).ToArray());
        return string.IsNullOrWhiteSpace(result) || result is "." or ".." ? "document.bin" : result;
    }
    public void Dispose()
    {
        if (_disposed) return; _disposed = true; _exports.Clear();
        if (Directory.Exists(_directory)) Directory.Delete(_directory, true);
    }
}
