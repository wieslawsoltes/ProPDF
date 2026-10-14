using System.Runtime.InteropServices.JavaScript;
using System.Runtime.Versioning;
using Avalonia;
using Avalonia.Browser;
using ProPDF.Core;

namespace ProPDF.Avalonia.Viewer.Browser;

[SupportedOSPlatform("browser")]
public static partial class Program
{
    public static Task Main(string[] args)
    {
        ViewerApplication.CreatePrintService = () => new BrowserPdfPrintHandoff();
        ViewerApplication.CreateFileSaveService = () => new BrowserFileSaveHandoff();
        // Pass options to browser pre-setup so lazy storage imports retain the
        // initialized asset resolver. Open still uses the real browser file picker.
        return AppBuilder.Configure<ViewerApplication>().WithInterFont().LogToTrace()
            .StartBrowserAppAsync("out", new BrowserPlatformOptions { PreferFileDialogPolyfill = true });
    }
    [JSExport]
    public static string Snapshot() => ViewerApplication.Shell?.Snapshot() ?? "{\"ready\":false}";
    [JSImport("printPdf", "viewer-host")]
    internal static partial void PrintPdf(string base64);
    [JSImport("saveFile", "viewer-host")]
    internal static partial void SaveFile(string base64, string suggestedName);

    // An explicit download handoff avoids treating a write-only download handle
    // as a native read/write filesystem handle. No browser permission is patched,
    // requested globally, or silently bypassed after a native picker denial.
    private sealed class BrowserFileSaveHandoff : IPdfViewerFileSaveService
    {
        public async Task SaveAsync(Stream content, string suggestedName, CancellationToken cancellationToken = default)
        {
            var bytes = await PdfStreams.ReadBoundedAsync(content, 128L * 1024 * 1024, cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            SaveFile(Convert.ToBase64String(bytes), suggestedName);
        }
    }
    private sealed class BrowserPdfPrintHandoff : IPdfViewerPrintService
    {
        public Task PrintAsync(PdfSnapshot document, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            using var input = document.OpenRead();
            if (input.Length > 128L * 1024 * 1024) throw new IOException("Browser print handoff is limited to 128 MiB.");
            using var output = new MemoryStream(); input.CopyTo(output);
            PrintPdf(Convert.ToBase64String(output.ToArray()));
            return Task.CompletedTask;
        }
    }
}
