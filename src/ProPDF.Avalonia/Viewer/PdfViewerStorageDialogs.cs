using Avalonia.Controls;
using Avalonia.Platform.Storage;
using ProPDF.Core;
using ProPDF.Presentation;

namespace ProPDF.Avalonia;

/// <summary>Stream-based native/browser storage. Private staging paths never stand in for successful user saves.</summary>
internal sealed class PdfViewerStorageDialogs(PdfViewer owner) : IPdfWorkspaceDialogs, IPdfWorkspaceFileTransfer, IPdfExternalNavigation, IDisposable
{
    private const long MaximumInputBytes = 128L * 1024 * 1024;
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "propdf-viewer", Guid.NewGuid().ToString("N"));
    private readonly Dictionary<string, IStorageFile> _destinations = new(StringComparer.Ordinal);
    private readonly HashSet<string> _stages = new(StringComparer.Ordinal);
    private bool _disposed;
    public bool AlwaysPickSaveDestination => true;
    private TopLevel Top => TopLevel.GetTopLevel(owner) ?? throw new InvalidOperationException("The viewer is not attached to a top level.");
    public async Task<string?> PickOpenPathAsync(PdfFileKind kind, CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        cancellationToken.ThrowIfCancellationRequested();
        var filters = kind switch
        {
            PdfFileKind.Pdf => new[] { new FilePickerFileType("PDF documents") { Patterns = ["*.pdf"], MimeTypes = ["application/pdf"] } },
            PdfFileKind.Image => new[] { new FilePickerFileType("Images") { Patterns = ["*.png", "*.jpg", "*.jpeg"], MimeTypes = ["image/png", "image/jpeg"] } },
            _ => new[] { FilePickerFileTypes.All }
        };
        var files = await Top.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions { Title = "Open " + kind, AllowMultiple = false, FileTypeFilter = filters });
        try
        {
            if (files.Count == 0) return null;
            cancellationToken.ThrowIfCancellationRequested();
            var file = files[0];
            var properties = await file.GetBasicPropertiesAsync();
            if (properties.Size > (ulong)MaximumInputBytes) throw new IOException("The selected file exceeds the viewer's 128 MiB import limit.");
            var path = CreateStage(file.Name);
            try
            {
                await using var input = await file.OpenReadAsync();
                await using var output = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, FileOptions.Asynchronous);
                var buffer = new byte[81920]; long count = 0; int read;
                while ((read = await input.ReadAsync(buffer.AsMemory(), cancellationToken)) != 0)
                {
                    count += read;
                    if (count > MaximumInputBytes) throw new IOException("The selected file exceeds the viewer's 128 MiB import limit.");
                    await output.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
                }
                return path;
            }
            catch { TryDelete(path); throw; }
        }
        finally { foreach (var file in files) file.Dispose(); }
    }
    public async Task<string?> PickSavePathAsync(string suggestedName, CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        cancellationToken.ThrowIfCancellationRequested();
        suggestedName = Path.GetFileName(suggestedName);
        if (suggestedName.Length > 33 && suggestedName[32] == '_' && Guid.TryParseExact(suggestedName[..32], "N", out _)) suggestedName = suggestedName[33..];
        var file = await Top.StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions { Title = "Save a copy", SuggestedFileName = suggestedName, DefaultExtension = Path.GetExtension(suggestedName).TrimStart('.') });
        if (file is null) return null;
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            var path = CreateStage(file.Name); _destinations.Add(path, file); return path;
        }
        catch { file.Dispose(); throw; }
    }
    public async Task PublishFileAsync(string stagedPath, CancellationToken cancellationToken)
    {
        if (!_destinations.Remove(stagedPath, out var file)) throw new InvalidOperationException("No user-selected output destination exists for this operation.");
        using (file)
        {
            await using var input = new FileStream(stagedPath, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, FileOptions.Asynchronous);
            await using var output = await file.OpenWriteAsync();
            if (output.CanSeek) output.SetLength(0);
            await input.CopyToAsync(output, cancellationToken);
            await output.FlushAsync(cancellationToken);
        }
    }
    public Task<string?> RequestPasswordAsync(CancellationToken cancellationToken) => owner.PromptAsync("Open protected PDF", "Enter the document password. It is used locally and is not stored by the viewer.", true, cancellationToken);
    public async Task<bool> ConfirmAsync(string title, string message, CancellationToken cancellationToken) => await owner.PromptAsync(title, message, false, cancellationToken) is not null;
    public async Task CopyTextAsync(string text, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var clipboard = Top.Clipboard ?? throw new NotSupportedException("This host does not provide a clipboard.");
        await clipboard.SetTextAsync(text);
    }
    public async Task OpenUriAsync(Uri uri, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!PdfUriPolicy.TryNormalize(uri.AbsoluteUri, out var safe)) throw new InvalidOperationException("The URI scheme is not allowed.");
        if (!await Top.Launcher.LaunchUriAsync(safe!)) throw new IOException("The external application could not be opened.");
    }
    private string CreateStage(string name)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        Directory.CreateDirectory(_directory);
        var clean = Path.GetFileName(name);
        if (string.IsNullOrWhiteSpace(clean)) clean = "document.pdf";
        var path = Path.Combine(_directory, Guid.NewGuid().ToString("N") + "_" + clean);
        _stages.Add(path); return path;
    }
    internal void ReleaseCompletedOperations()
    {
        foreach (var file in _destinations.Values) file.Dispose(); _destinations.Clear();
        foreach (var path in _stages) TryDelete(path); _stages.Clear();
    }
    private static void TryDelete(string path) { try { File.Delete(path); } catch (IOException) { } catch (UnauthorizedAccessException) { } }
    public void Dispose()
    {
        if (_disposed) return; _disposed = true; ReleaseCompletedOperations();
        try { Directory.Delete(_directory, true); } catch (DirectoryNotFoundException) { } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }
}
