using Windows.Storage;
using Windows.Storage.Pickers;

namespace ProPDF.Uno;

/// <summary>Host file boundary. Browser apps may substitute a download/import service; controls do not depend on JavaScript.</summary>
public interface IPdfUnoFiles : IDisposable
{
    Task<string?> PickOpenPathAsync(PdfFileKind kind, CancellationToken token);
    Task<string?> PickSavePathAsync(string suggestedName, CancellationToken token);
    Task PublishFileAsync(string stagedPath, CancellationToken token);
}

/// <summary>Uno StorageFile implementation for desktop and supported device file pickers.</summary>
public sealed class PdfUnoFiles : IPdfUnoFiles
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "propdf-uno-" + Guid.NewGuid().ToString("N"));
    private readonly Dictionary<string, StorageFile> _destinations = new(StringComparer.Ordinal);
    private long _stagedBytes;
    private bool _disposed;
    public async Task<string?> PickOpenPathAsync(PdfFileKind kind, CancellationToken token)
    {
        ObjectDisposedException.ThrowIf(_disposed, this); token.ThrowIfCancellationRequested();
        var picker = new FileOpenPicker();
        foreach (var extension in kind switch { PdfFileKind.Pdf => new[] { ".pdf" }, PdfFileKind.Image => new[] { ".png", ".jpg", ".jpeg", ".tif", ".bmp" }, _ => new[] { "*" } }) picker.FileTypeFilter.Add(extension);
        var file = await picker.PickSingleFileAsync(); token.ThrowIfCancellationRequested();
        if (file is null) return null;
        await using var input = await file.OpenStreamForReadAsync();
        var bytes = await PdfStreams.ReadBoundedAsync(input, 64L * 1024 * 1024, token);
        if (_stagedBytes + bytes.Length > 256L * 1024 * 1024) throw new InvalidOperationException("This file session reached its 256 MiB import budget. Recreate the host before importing more.");
        var path = StagingPath(file.Name); await File.WriteAllBytesAsync(path, bytes, token); _stagedBytes += bytes.Length; return path;
    }
    public async Task<string?> PickSavePathAsync(string suggestedName, CancellationToken token)
    {
        ObjectDisposedException.ThrowIf(_disposed, this); token.ThrowIfCancellationRequested();
        var extension = Path.GetExtension(suggestedName); if (string.IsNullOrEmpty(extension)) extension = ".bin";
        var picker = new FileSavePicker { SuggestedFileName = Path.GetFileNameWithoutExtension(suggestedName) };
        picker.FileTypeChoices.Add("Export file", new List<string> { extension });
        var file = await picker.PickSaveFileAsync(); token.ThrowIfCancellationRequested();
        if (file is null) return null;
        if (_destinations.Count >= 64) throw new InvalidOperationException("Too many pending exports. Recreate the file host.");
        var path = StagingPath(file.Name); _destinations.Add(path, file); return path;
    }
    public async Task PublishFileAsync(string stagedPath, CancellationToken token)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (!_destinations.TryGetValue(stagedPath, out var file)) throw new InvalidOperationException("No user-selected destination owns this export.");
        await using (var input = File.OpenRead(stagedPath))
        await using (var output = await file.OpenStreamForWriteAsync())
        { output.SetLength(0); await input.CopyToAsync(output, token); await output.FlushAsync(token); }
        _destinations.Remove(stagedPath); File.Delete(stagedPath);
    }
    private string StagingPath(string name)
    {
        var folder = Path.Combine(_directory, Guid.NewGuid().ToString("N")); Directory.CreateDirectory(folder);
        return Path.Combine(folder, Path.GetFileName(name.Replace('\\', '/')));
    }
    public void Dispose()
    {
        if (_disposed) return; _disposed = true; _destinations.Clear();
        try { if (Directory.Exists(_directory)) Directory.Delete(_directory, true); } catch (IOException) { }
    }
}
