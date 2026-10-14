using Avalonia;

namespace ProPDF.Avalonia;

/// <summary>An explicit host output handoff, used instead of a native save picker when supplied.</summary>
/// <remarks>
/// The viewer owns the readable input stream and keeps it alive until the returned task completes.
/// Implementations must not retain or dispose that stream. Throw on failure or cancellation.
/// Browser download completion means the browser accepted the handoff, not that a user retained a disk file.
/// The host owns this service; the viewer never disposes it.
/// </remarks>
public interface IPdfViewerFileSaveService
{
    Task SaveAsync(Stream content, string suggestedName, CancellationToken cancellationToken = default);
}

public partial class PdfViewer
{
    public static readonly StyledProperty<IPdfViewerFileSaveService?> FileSaveServiceProperty =
        AvaloniaProperty.Register<PdfViewer, IPdfViewerFileSaveService?>(nameof(FileSaveService));

    /// <summary>Optional host save/download handoff. Null uses Avalonia's user-selected storage destination.</summary>
    public IPdfViewerFileSaveService? FileSaveService
    {
        get => GetValue(FileSaveServiceProperty);
        set => SetValue(FileSaveServiceProperty, value);
    }
}
