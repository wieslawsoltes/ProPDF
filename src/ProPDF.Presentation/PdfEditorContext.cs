using ProPDF.Core;

namespace ProPDF.Presentation;

/// <summary>Application composition boundary. Controls depend on interfaces, never a concrete editing vendor.</summary>
public sealed class PdfEditorContext
{
    public PdfEditorContext(PdfViewportController viewport, IPdfDocumentInspector? inspector = null,
        Func<CancellationToken, Task<PdfSnapshot>>? createDocument = null, IPdfDocumentLoader? documentLoader = null)
    {
        Viewport = viewport ?? throw new ArgumentNullException(nameof(viewport));
        Inspector = inspector;
        viewport.ConfigureContentService(inspector as IPdfContentService);
        CreateDocument = createDocument;
        DocumentLoader = documentLoader;
    }
    public PdfViewportController Viewport { get; }
    public PdfSession Session => Viewport.Session;
    public IPdfDocumentInspector? Inspector { get; }
    public IPdfPageExtractor? PageExtractor => Inspector as IPdfPageExtractor;
    public IPdfAttachmentReader? AttachmentReader => Inspector as IPdfAttachmentReader;
    public Func<CancellationToken, Task<PdfSnapshot>>? CreateDocument { get; }
    public IPdfDocumentLoader? DocumentLoader { get; }

    public async Task NewAsync(CancellationToken cancellationToken = default)
    {
        var create = CreateDocument ?? throw new NotSupportedException("No document creator was configured.");
        var document = await create(cancellationToken).ConfigureAwait(false);
        using var stream = document.OpenRead();
        await Session.OpenAsync(stream, cancellationToken: cancellationToken).ConfigureAwait(false);
    }
    public async Task OpenPathAsync(string path, string? password = null, CancellationToken cancellationToken = default)
    {
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, FileOptions.Asynchronous | FileOptions.SequentialScan);
        await Session.OpenAsync(stream, new PdfOpenOptions { Password = password }, path, cancellationToken).ConfigureAwait(false);
        Viewport.GoToPage(1);
    }
    public Task ApplyAsync(IPdfEditOperation operation, CancellationToken cancellationToken = default) =>
        Session.ApplyAsync(operation, Session.Current?.Id, cancellationToken);
}
