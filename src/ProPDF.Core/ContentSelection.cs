using System.Collections.Immutable;

namespace ProPDF.Core;

/// <summary>Applies one edit per selected object against a single original page inspection.</summary>
public sealed record EditContentObjects : PdfContentObjectEdit
{
    public const int MaximumSelection = 1000;
    public EditContentObjects(IEnumerable<PdfContentObjectEdit> edits) : this(Capture(edits)) { }
    private EditContentObjects(ImmutableArray<PdfContentObjectEdit> edits)
        : base(edits[0].Object, "Edit selected content objects") => Edits = edits;
    public ImmutableArray<PdfContentObjectEdit> Edits { get; }

    private static ImmutableArray<PdfContentObjectEdit> Capture(IEnumerable<PdfContentObjectEdit> edits)
    {
        ArgumentNullException.ThrowIfNull(edits);
        var copy = edits.Take(MaximumSelection + 1).ToImmutableArray();
        if (copy.Length is 0 or > MaximumSelection) throw new ArgumentException("Select between 1 and 1000 objects.", nameof(edits));
        if (copy.Any(e => e is null || e.Object is null || e is EditContentObjects))
            throw new ArgumentException("Nested selections and null edits are not supported.", nameof(edits));
        var first = copy[0].Object;
        if (copy.Any(e => e.Object.Revision != first.Revision || e.Object.PageNumber != first.PageNumber || e.Object.Fingerprint != first.Fingerprint))
            throw new PdfRevisionConflictException();
        if (copy.Select(e => e.Object.Index).Distinct().Count() != copy.Length)
            throw new ArgumentException("An object may be edited only once in a selection transaction.", nameof(edits));
        return copy;
    }
}

public enum PdfSelectionAlignment { Left, HorizontalCenter, Right, Top, VerticalCenter, Bottom }
public enum PdfSelectionDistribution { HorizontalCenters, VerticalCenters, HorizontalGaps, VerticalGaps }

/// <summary>Pure geometry for a same-page selection. Bounds are logical selection bounds, not exact painted ink.</summary>
public static class PdfContentSelection
{
    public static PdfRect Bounds(IEnumerable<PdfContentObject> objects) => Union(Capture(objects));
    private static PdfRect Union(IReadOnlyList<PdfContentObject> items)
    {
        var x = items.Min(o => o.Bounds.X); var y = items.Min(o => o.Bounds.Y);
        return new(x, y, items.Max(o => o.Bounds.Right) - x, items.Max(o => o.Bounds.Bottom) - y);
    }
    private static PdfContentObject[] Capture(IEnumerable<PdfContentObject> objects)
    {
        ArgumentNullException.ThrowIfNull(objects);
        var items = objects.Take(EditContentObjects.MaximumSelection + 1).ToArray();
        if (items.Length is 0 or > EditContentObjects.MaximumSelection || items.Any(o => o is null))
            throw new ArgumentException("Select between 1 and 1000 nonnull objects.", nameof(objects));
        // Use the transaction validator for revision, page, fingerprint and duplicate checks.
        _ = new EditContentObjects(items.Select(o => new DeleteContentObject(o.Reference)));
        return items;
    }
    public static EditContentObjects Transform(IEnumerable<PdfContentObject> objects, PdfAffineTransform transform)
    {
        transform.Validate();
        return new(Capture(objects).Select(o => new TransformContentObject(o.Reference, transform)));
    }
    public static EditContentObjects Delete(IEnumerable<PdfContentObject> objects) => new(Capture(objects).Select(o => new DeleteContentObject(o.Reference)));
    public static EditContentObjects Duplicate(IEnumerable<PdfContentObject> objects, PdfAffineTransform transform)
    {
        transform.Validate();
        return new(Capture(objects).Select(o => new DuplicateContentObject(o.Reference, transform)));
    }
    public static EditContentObjects Align(IEnumerable<PdfContentObject> objects, PdfSelectionAlignment alignment)
    {
        if (!Enum.IsDefined(alignment)) throw new ArgumentOutOfRangeException(nameof(alignment));
        var items = Capture(objects);
        if (items.Length < 2) throw new ArgumentException("Alignment requires at least two objects.", nameof(objects));
        var b = Union(items);
        return new(items.Select(o =>
        {
            var r = o.Bounds;
            var dx = alignment switch
            {
                PdfSelectionAlignment.Left => b.X - r.X,
                PdfSelectionAlignment.HorizontalCenter => (b.X + b.Right - r.X - r.Right) / 2,
                PdfSelectionAlignment.Right => b.Right - r.Right, _ => 0
            };
            var dy = alignment switch
            {
                PdfSelectionAlignment.Top => b.Y - r.Y,
                PdfSelectionAlignment.VerticalCenter => (b.Y + b.Bottom - r.Y - r.Bottom) / 2,
                PdfSelectionAlignment.Bottom => b.Bottom - r.Bottom, _ => 0
            };
            return new TransformContentObject(o.Reference, PdfAffineTransform.Translation(dx, dy));
        }));
    }
    public static EditContentObjects Distribute(IEnumerable<PdfContentObject> objects, PdfSelectionDistribution distribution)
    {
        if (!Enum.IsDefined(distribution)) throw new ArgumentOutOfRangeException(nameof(distribution));
        var items = Capture(objects);
        if (items.Length < 3) throw new ArgumentException("Distribution requires at least three objects.", nameof(objects));
        var horizontal = distribution is PdfSelectionDistribution.HorizontalCenters or PdfSelectionDistribution.HorizontalGaps;
        var gaps = distribution is PdfSelectionDistribution.HorizontalGaps or PdfSelectionDistribution.VerticalGaps;
        double Start(PdfRect b) => horizontal ? b.X : b.Y;
        double Extent(PdfRect b) => horizontal ? b.Width : b.Height;
        double Center(PdfRect b) => Start(b) + Extent(b) / 2;
        items = items.OrderBy(o => gaps ? Start(o.Bounds) : Center(o.Bounds)).ThenBy(o => o.Reference.Index).ToArray();
        var first = items[0].Bounds; var last = items[^1].Bounds;
        var interval = gaps ? (Start(last) + Extent(last) - Start(first) - items.Sum(o => Extent(o.Bounds))) / (items.Length - 1)
            : (Center(last) - Center(first)) / (items.Length - 1);
        if (gaps && interval < 0) throw new InvalidOperationException("Equal nonoverlapping gaps do not fit between the outermost selected objects.");
        var cursor = gaps ? Start(first) : Center(first);
        var edits = new List<PdfContentObjectEdit>(items.Length);
        for (var i = 0; i < items.Length; i++)
        {
            var delta = cursor - (gaps ? Start(items[i].Bounds) : Center(items[i].Bounds));
            // Leave endpoint objects exactly fixed, avoiding floating-point drift.
            if (i == 0 || i == items.Length - 1) delta = 0;
            edits.Add(new TransformContentObject(items[i].Reference, PdfAffineTransform.Translation(horizontal ? delta : 0, horizontal ? 0 : delta)));
            cursor += interval + (gaps ? Extent(items[i].Bounds) : 0);
        }
        return new(edits);
    }
}
