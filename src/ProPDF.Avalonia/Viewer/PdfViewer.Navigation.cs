using ProPDF.Core;

namespace ProPDF.Avalonia;

public partial class PdfViewer
{
    private PdfViewerCommand? _activateBookmark;
    public PdfViewerCommand ActivateBookmarkCommand => _activateBookmark ??= Command(async parameter =>
    {
        if (Workspace is not { } workspace || parameter is not PdfNavigationBookmark bookmark) return;
        workspace.SelectedBookmark = bookmark;
        await workspace.FollowBookmarkCommand.ExecuteAsync();
    }, parameter => HasDocument && parameter is PdfNavigationBookmark);
}
