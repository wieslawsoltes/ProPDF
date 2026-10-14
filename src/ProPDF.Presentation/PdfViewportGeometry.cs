using ProPDF.Core;

namespace ProPDF.Presentation;

public sealed partial class PdfViewportController
{
    /// <summary>Returns the current placement in viewport DIP, or null when a page is absent from the layout.</summary>
    public PdfRect? GetPageViewportBounds(int pageNumber)
    {
        lock (_gate)
        {
            if (_disposed || !_placements.TryGetValue(pageNumber, out var page)) return null;
            return new PdfRect(page.Bounds.X - _offset.X, page.Bounds.Y - _offset.Y, page.Bounds.Width, page.Bounds.Height);
        }
    }
}
