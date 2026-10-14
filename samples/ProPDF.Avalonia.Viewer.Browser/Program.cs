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
        // Browser options must participate in PreSetupBrowser. Registering a fresh
        // instance with AppBuilder.With later replaces the initialized asset resolver
        // and breaks lazy imports such as the native/browser storage provider.
        return AppBuilder.Configure<ViewerApplication>().WithInterFont().LogToTrace()
            .StartBrowserAppAsync("out", new BrowserPlatformOptions { PreferFileDialogPolyfill = true });
    }
    [JSExport]
    public static string Snapshot() => ViewerApplication.Shell?.Snapshot() ?? "{\"ready\":false}";
    [JSImport("printPdf", "viewer-host")]
    internal static partial void PrintPdf(string base64);
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
