namespace ProPDF.Core;

public interface IPdfPageExtractor
{
    /// <summary>Creates a new unsigned, unencrypted document containing the selected one-based pages.</summary>
    Task<PdfSnapshot> ExtractPagesAsync(PdfSnapshot source, IEnumerable<int> pages, CancellationToken cancellationToken = default);
}

public interface IPdfAttachmentReader
{
    Task<PdfBinaryAsset> ReadAttachmentAsync(PdfSnapshot source, string name, CancellationToken cancellationToken = default);
}
