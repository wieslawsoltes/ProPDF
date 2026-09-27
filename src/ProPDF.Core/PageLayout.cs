namespace ProPDF.Core;

public enum PdfLayoutMode { Continuous, Facing, SinglePage }
public sealed record PdfPagePlacement(int PageNumber, PdfRect Bounds);

/// <summary>Metadata-only layout. Visible-row lookup is O(log n + visible pages), not O(document pages).</summary>
public sealed class PdfPageLayout
{
    private sealed record Row(double Top, double Bottom, PdfPagePlacement[] Pages);
    private readonly Row[] _rows;
    private readonly IReadOnlyList<PdfPagePlacement> _pages;

    private PdfPageLayout(Row[] rows, PdfPagePlacement[] pages, PdfSize extent, double scale)
    {
        _rows = rows;
        _pages = Array.AsReadOnly(pages);
        Extent = extent;
        Scale = scale;
    }

    public IReadOnlyList<PdfPagePlacement> Pages => _pages;
    public PdfSize Extent { get; }
    public double Scale { get; }

    public static PdfPageLayout Create(IReadOnlyList<PdfPageInfo> pages, double viewportWidth, double zoom = 1,
        PdfLayoutMode mode = PdfLayoutMode.Continuous, int currentPage = 1, double gap = 20)
    {
        if (pages.Count == 0 || !double.IsFinite(viewportWidth) || viewportWidth <= 0 ||
            !double.IsFinite(zoom) || zoom is < 0.05 or > 16 || !double.IsFinite(gap) || gap < 0)
            throw new ArgumentOutOfRangeException(nameof(zoom));
        if (currentPage < 1 || currentPage > pages.Count) throw new ArgumentOutOfRangeException(nameof(currentPage));
        var scale = zoom * 96 / 72;
        var selected = mode == PdfLayoutMode.SinglePage ? [pages[currentPage - 1]] : pages.ToArray();
        var groups = new List<PdfPageInfo[]>();
        for (var i = 0; i < selected.Length;)
        {
            var count = mode == PdfLayoutMode.Facing && i > 0 ? Math.Min(2, selected.Length - i) : 1;
            groups.Add(selected.Skip(i).Take(count).ToArray());
            i += count;
        }
        var contentWidth = groups.Max(g => g.Sum(p => p.Size.Width * scale) + (g.Length - 1) * gap);
        var width = Math.Max(viewportWidth, contentWidth + 2 * gap);
        var rows = new List<Row>();
        var all = new List<PdfPagePlacement>();
        var y = gap;
        foreach (var group in groups)
        {
            var groupWidth = group.Sum(p => p.Size.Width * scale) + (group.Length - 1) * gap;
            var x = (width - groupWidth) / 2;
            var height = group.Max(p => p.Size.Height * scale);
            var placements = new List<PdfPagePlacement>();
            foreach (var page in group)
            {
                var placement = new PdfPagePlacement(page.Number, new PdfRect(x, y, page.Size.Width * scale, page.Size.Height * scale));
                placements.Add(placement);
                all.Add(placement);
                x += placement.Bounds.Width + gap;
            }
            rows.Add(new Row(y, y + height, placements.ToArray()));
            y += height + gap;
        }
        return new PdfPageLayout(rows.ToArray(), all.ToArray(), new PdfSize(width, y), scale);
    }

    public IEnumerable<PdfPagePlacement> GetVisible(PdfRect viewport)
    {
        var low = 0;
        var high = _rows.Length;
        while (low < high)
        {
            var middle = low + (high - low) / 2;
            if (_rows[middle].Bottom <= viewport.Y) low = middle + 1;
            else high = middle;
        }
        for (var index = low; index < _rows.Length && _rows[index].Top < viewport.Bottom; index++)
            foreach (var page in _rows[index].Pages)
                if (page.Bounds.Intersects(viewport)) yield return page;
    }

    public (int PageNumber, PdfPoint Point)? HitTest(PdfPoint point)
    {
        foreach (var page in GetVisible(new PdfRect(point.X, point.Y, 0.001, 0.001)))
            if (page.Bounds.Contains(point))
                return (page.PageNumber, new PdfPoint((point.X - page.Bounds.X) / Scale, (point.Y - page.Bounds.Y) / Scale));
        return null;
    }
}
