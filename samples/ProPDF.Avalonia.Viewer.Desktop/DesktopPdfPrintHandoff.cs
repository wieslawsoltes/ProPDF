using System.Diagnostics;
using ProPDF.Core;

namespace ProPDF.Avalonia.Viewer.Desktop;

/// <summary>Opens a temporary PDF in the system PDF application, where the user chooses print settings.</summary>
public sealed class DesktopPdfPrintHandoff : IPdfViewerPrintService, IDisposable
{
    private readonly List<string> _paths = [];
    public async Task PrintAsync(PdfSnapshot document, CancellationToken cancellationToken = default)
    {
        var path = Path.Combine(Path.GetTempPath(), "ProPDF-print-" + Guid.NewGuid().ToString("N") + ".pdf");
        _paths.Add(path);
        using var input = document.OpenRead();
        await using (var output = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, FileOptions.Asynchronous)) await input.CopyToAsync(output, cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        using var process = Process.Start(new ProcessStartInfo(path) { UseShellExecute = true }) ?? throw new IOException("No system PDF application accepted the print handoff.");
    }
    public void Dispose()
    {
        foreach (var path in _paths) { try { File.Delete(path); } catch (IOException) { } catch (UnauthorizedAccessException) { } }
        _paths.Clear();
    }
}
