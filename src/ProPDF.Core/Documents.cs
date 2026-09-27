using System.Collections.ObjectModel;

namespace ProPDF.Core;

public sealed class PdfOpenOptions
{
    public string? Password { get; init; }
    public long MaximumBytes { get; init; } = 256L * 1024 * 1024;
    public int MaximumPages { get; init; } = 100_000;
    public bool UseLenientParsing { get; init; }
    public override string ToString() => $"PDF options: limit {MaximumBytes} bytes / {MaximumPages} pages; password [redacted]";

    public void Validate()
    {
        if (MaximumBytes is <= 0 or > int.MaxValue || MaximumPages <= 0)
            throw new ArgumentOutOfRangeException(nameof(MaximumBytes));
    }
}

public sealed record PdfPageInfo(int Number, PdfSize Size);
public sealed record PdfMetadata(string Title = "", string Author = "", string Subject = "", string Keywords = "");

/// <summary>Immutable, independently readable document revision. Bytes and page collections are defensively copied.</summary>
public sealed class PdfSnapshot
{
    private readonly byte[] _bytes;
    private readonly string? _password;

    public PdfSnapshot(ReadOnlySpan<byte> bytes, IEnumerable<PdfPageInfo> pages, PdfMetadata? metadata = null, string? password = null)
    {
        if (bytes.IsEmpty) throw new ArgumentException("The document is empty.", nameof(bytes));
        var copy = pages.ToArray();
        if (copy.Length == 0) throw new ArgumentException("At least one page is required.", nameof(pages));
        for (var i = 0; i < copy.Length; i++)
            if (copy[i].Number != i + 1 || copy[i].Size.Width <= 0 || copy[i].Size.Height <= 0)
                throw new ArgumentException("Pages must be sequential, one-based and have positive dimensions.", nameof(pages));
        _bytes = bytes.ToArray();
        _password = password;
        Pages = new ReadOnlyCollection<PdfPageInfo>(copy);
        Metadata = metadata ?? new PdfMetadata();
    }

    public Guid Id { get; } = Guid.NewGuid();
    public long ByteLength => _bytes.LongLength;
    public IReadOnlyList<PdfPageInfo> Pages { get; }
    public PdfMetadata Metadata { get; }
    public Stream OpenRead() => new MemoryStream(_bytes, 0, _bytes.Length, writable: false, publiclyVisible: false);
    // A method, not a serialized property, so ordinary diagnostic/JSON output cannot accidentally include credentials.
    public string? GetPassword() => _password;
    public PdfPageInfo GetPage(int number) => number >= 1 && number <= Pages.Count ? Pages[number - 1] : throw new ArgumentOutOfRangeException(nameof(number));
    public override string ToString() => $"PDF {Id}: {Pages.Count} pages, {ByteLength} bytes";
}

public interface IPdfDocumentLoader
{
    /// <summary>Loads from the current stream position, without closing the caller's stream.</summary>
    Task<PdfSnapshot> OpenAsync(Stream source, PdfOpenOptions? options = null, CancellationToken cancellationToken = default);
}

public enum PdfCapability
{
    PageOrganization, ContentInsertion, ContentReplacement, Annotations, Forms,
    Redaction, Metadata, Attachments, Encryption, DigitalSignatures, Bookmarks
}

public interface IPdfEditOperation
{
    string Description { get; }
    PdfCapability Capability { get; }
}

public interface IPdfEditor
{
    IReadOnlySet<PdfCapability> Capabilities { get; }
    /// <summary>Applies every operation atomically and returns a validated new revision; never mutates the source.</summary>
    Task<PdfSnapshot> ApplyAsync(PdfSnapshot source, IReadOnlyList<IPdfEditOperation> operations, CancellationToken cancellationToken = default);
}

public sealed class PdfRevisionConflictException : InvalidOperationException
{
    public PdfRevisionConflictException() : base("The document changed after this operation was prepared. Reload its revision before applying the edit.") { }
}

public sealed record PdfWord(string Text, PdfRect Bounds, int StartIndex);
public sealed record PdfTextPage(int PageNumber, string Text, IReadOnlyList<PdfWord> Words);
public sealed record PdfSearchHit(int PageNumber, int StartIndex, int Length, string Text, IReadOnlyList<PdfRect> Bounds);

public sealed record PdfSearchOptions(bool MatchCase = false, bool WholeWord = false, bool RegularExpression = false, int MaximumResults = 10_000);

public interface IPdfTextService
{
    Task<PdfTextPage> GetPageTextAsync(PdfSnapshot source, int pageNumber, CancellationToken cancellationToken = default);
    Task<IReadOnlyList<PdfSearchHit>> SearchAsync(PdfSnapshot source, string query, PdfSearchOptions? options = null, CancellationToken cancellationToken = default);
}

public static class PdfStreams
{
    public static async Task<byte[]> ReadBoundedAsync(Stream source, long maximumBytes, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(source);
        if (maximumBytes is <= 0 or > int.MaxValue) throw new ArgumentOutOfRangeException(nameof(maximumBytes));
        if (source.CanSeek && source.Length - source.Position > maximumBytes)
            throw new InvalidDataException("PDF input exceeds the configured byte limit.");
        using var output = new MemoryStream();
        var buffer = new byte[81_920];
        while (true)
        {
            var read = await source.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
            if (read == 0) break;
            if (output.Length + read > maximumBytes) throw new InvalidDataException("PDF input exceeds the configured byte limit.");
            output.Write(buffer, 0, read);
        }
        cancellationToken.ThrowIfCancellationRequested();
        return output.ToArray();
    }

    /// <summary>Writes in the destination directory, flushes, then replaces. Cancellation is observed before the commit point.</summary>
    public static async Task SaveAtomicAsync(PdfSnapshot source, string path, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        var fullPath = Path.GetFullPath(path);
        var directory = Path.GetDirectoryName(fullPath)!;
        if (!Directory.Exists(directory)) throw new DirectoryNotFoundException(directory);
        var temporary = Path.Combine(directory, $".{Path.GetFileName(fullPath)}.{Guid.NewGuid():N}.tmp");
        try
        {
            await using (var destination = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None,
                81_920, FileOptions.Asynchronous | FileOptions.WriteThrough))
            {
                await using var input = source.OpenRead();
                await input.CopyToAsync(destination, cancellationToken).ConfigureAwait(false);
                await destination.FlushAsync(cancellationToken).ConfigureAwait(false);
                destination.Flush(flushToDisk: true);
            }
            cancellationToken.ThrowIfCancellationRequested();
            File.Move(temporary, fullPath, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporary)) File.Delete(temporary);
        }
    }
}
