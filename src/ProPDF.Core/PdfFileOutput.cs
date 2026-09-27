using System.Text;

namespace ProPDF.Core;

/// <summary>Atomic file output for non-PDF exports. The callback must not dispose the provided stream.</summary>
public static class PdfFileOutput
{
    public static async Task WriteAtomicAsync(string path, Func<Stream, CancellationToken, Task> write, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ArgumentNullException.ThrowIfNull(write);
        cancellationToken.ThrowIfCancellationRequested();
        var target = Path.GetFullPath(path);
        var directory = Path.GetDirectoryName(target)!;
        if (!Directory.Exists(directory)) throw new DirectoryNotFoundException(directory);
        var temporary = Path.Combine(directory, $".propdf-export-{Guid.NewGuid():N}.tmp");
        try
        {
            await using (var output = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81_920,
                FileOptions.Asynchronous | FileOptions.WriteThrough))
            {
                await write(output, cancellationToken).ConfigureAwait(false);
                await output.FlushAsync(cancellationToken).ConfigureAwait(false);
                output.Flush(true);
            }
            cancellationToken.ThrowIfCancellationRequested();
            File.Move(temporary, target, overwrite: true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }

    public static Task SaveAssetAsync(string path, PdfBinaryAsset asset, CancellationToken cancellationToken = default) =>
        WriteAtomicAsync(path, async (stream, token) => await stream.WriteAsync(asset.ToArray(), token).ConfigureAwait(false), cancellationToken);
}

/// <summary>Streaming UTF-8 text export in extraction order. This is not visual layout reconstruction or OCR.</summary>
public sealed class PdfTextExporter(IPdfTextService textService)
{
    private readonly IPdfTextService _text = textService ?? throw new ArgumentNullException(nameof(textService));

    public Task SaveAsync(PdfSnapshot source, string path, IEnumerable<int>? pages = null,
        long maximumBytes = 64L * 1024 * 1024, CancellationToken cancellationToken = default) =>
        PdfFileOutput.WriteAtomicAsync(path, (output, token) => WriteAsync(source, output, pages, maximumBytes, token), cancellationToken);

    public async Task WriteAsync(PdfSnapshot source, Stream destination, IEnumerable<int>? pages = null,
        long maximumBytes = 64L * 1024 * 1024, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(destination);
        if (!destination.CanWrite) throw new ArgumentException("The output stream is not writable.", nameof(destination));
        if (maximumBytes is < 1 or > int.MaxValue) throw new ArgumentOutOfRangeException(nameof(maximumBytes));
        var selected = (pages ?? Enumerable.Range(1, source.Pages.Count)).Take(100_001).ToArray();
        if (selected.Length is < 1 or > 100_000) throw new ArgumentOutOfRangeException(nameof(pages));
        foreach (var number in selected) source.GetPage(number);
        long written = 0;
        for (var i = 0; i < selected.Length; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var page = await _text.GetPageTextAsync(source, selected[i], cancellationToken).ConfigureAwait(false);
            var content = (i == 0 ? "" : "\n\f\n") + page.Text;
            var length = Encoding.UTF8.GetByteCount(content);
            if (length > maximumBytes - written) throw new InvalidDataException("Text export exceeds the configured byte limit.");
            await destination.WriteAsync(Encoding.UTF8.GetBytes(content), cancellationToken).ConfigureAwait(false);
            written += length;
        }
    }
}
