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
    /// <summary>Reveals a search result directly without repeatedly traversing preceding hits.</summary>
    public void GoToSearchResult(int index)
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if ((uint)index >= (uint)_searchHits.Count) throw new ArgumentOutOfRangeException(nameof(index));
            _searchIndex = index;
            RevealHitLocked(_searchHits[index]);
        }
        ScheduleRender();
    }
}
