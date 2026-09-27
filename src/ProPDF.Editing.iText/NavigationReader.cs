using iText.Kernel.Pdf;
using ProPDF.Core;

namespace ProPDF.Editing.iText;

public sealed partial class ITextPdfEditor : IPdfNavigationService
{
    public Task<PdfDocumentNavigation> ReadNavigationAsync(PdfSnapshot source, PdfNavigationOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(source);
        var settings = options ?? new PdfNavigationOptions();
        if (settings.MaximumEntries is < 1 or > 100_000 || settings.MaximumDepth is < 1 or > 512)
            throw new ArgumentOutOfRangeException(nameof(options));
        return Task.Run(() =>
        {
            using var input = source.OpenRead();
            using var document = new PdfDocument(CreateReader(input, source.GetPassword()));
            var bookmarks = ReadOutlineNavigation(document, settings, cancellationToken);
            var links = new List<PdfNavigationLink>();
            for (var number = 1; number <= document.GetNumberOfPages(); number++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var page = document.GetPage(number);
                var transform = Transform(page);
                foreach (var annotation in page.GetAnnotations())
                {
                    if (!PdfName.Link.Equals(annotation.GetSubtype())) continue;
                    if (bookmarks.Count + links.Count >= settings.MaximumEntries) throw new InvalidDataException("Navigation entry limit exceeded.");
                    var rectangle = annotation.GetRectangle().ToRectangle();
                    var bounds = transform.ToView(new PdfRect(rectangle.GetX(), rectangle.GetY(),
                        Math.Max(0, rectangle.GetWidth()), Math.Max(0, rectangle.GetHeight())));
                    links.Add(new PdfNavigationLink(number, AnnotationId(annotation), bounds, ReadTarget(document, annotation.GetPdfObject())));
                }
            }
            return new PdfDocumentNavigation(source.Id, bookmarks.AsReadOnly(), links.AsReadOnly());
        }, cancellationToken);
    }

    private static List<PdfNavigationBookmark> ReadOutlineNavigation(PdfDocument document, PdfNavigationOptions settings, CancellationToken token)
    {
        var results = new List<PdfNavigationBookmark>();
        var first = document.GetCatalog().GetPdfObject().GetAsDictionary(PdfName.Outlines)?.GetAsDictionary(PdfName.First);
        if (first is null) return results;
        var stack = new Stack<(PdfDictionary Entry, string Parent, int Index, int Depth)>();
        var seen = new HashSet<PdfDictionary>(ReferenceEqualityComparer.Instance);
        stack.Push((first, "", 0, 0));
        while (stack.TryPop(out var item))
        {
            token.ThrowIfCancellationRequested();
            if (!seen.Add(item.Entry)) throw new InvalidDataException("Cyclic or multiply referenced outline entries are not supported.");
            if (results.Count >= settings.MaximumEntries || item.Depth >= settings.MaximumDepth)
                throw new InvalidDataException("Outline size or depth limit exceeded.");
            var path = item.Parent.Length == 0 ? item.Index.ToString(System.Globalization.CultureInfo.InvariantCulture) : $"{item.Parent}/{item.Index}";
            var title = item.Entry.GetAsString(PdfName.Title)?.ToUnicodeString() ?? "Untitled bookmark";
            if (title.Length > 16_384) throw new InvalidDataException("Bookmark title exceeds its limit.");
            results.Add(new PdfNavigationBookmark(path, title, item.Depth,
                (item.Entry.GetAsNumber(PdfName.Count)?.IntValue() ?? 0) >= 0, ReadTarget(document, item.Entry)));
            if (item.Entry.GetAsDictionary(PdfName.Next) is { } next) stack.Push((next, item.Parent, item.Index + 1, item.Depth));
            if (item.Entry.GetAsDictionary(PdfName.First) is { } child) stack.Push((child, path, 0, item.Depth + 1));
        }
        return results;
    }

    private static PdfNavigationTarget Unsupported(string reason) => new(UnsupportedReason: reason);
    private static PdfNavigationTarget ReadTarget(PdfDocument document, PdfDictionary entry)
    {
        if (entry.Get(PdfName.Dest) is { } direct) return ResolveDestination(document, direct, new HashSet<string>(StringComparer.Ordinal), 0);
        var action = entry.GetAsDictionary(PdfName.A);
        if (action is null) return Unsupported("No destination");
        if (action.ContainsKey(PdfName.Next)) return Unsupported("Chained actions are disabled");
        var kind = action.GetAsName(PdfName.S);
        if (PdfName.GoTo.Equals(kind))
        {
            var destination = action.Get(PdfName.D);
            return destination is null ? Unsupported("Missing GoTo destination") : ResolveDestination(document, destination, new HashSet<string>(StringComparer.Ordinal), 0);
        }
        if (PdfName.URI.Equals(kind))
        {
            var text = action.GetAsString(PdfName.URI)?.ToUnicodeString();
            return PdfUriPolicy.TryNormalize(text, out var uri) ? new PdfNavigationTarget(ExternalUri: uri!.AbsoluteUri) : Unsupported("URI scheme or credentials are not permitted");
        }
        if (PdfName.Named.Equals(kind))
        {
            var name = action.GetAsName(PdfName.N)?.GetValue();
            return name is "FirstPage" or "LastPage" or "NextPage" or "PrevPage"
                ? new PdfNavigationTarget(NamedAction: name) : Unsupported("Unsupported named action");
        }
        return Unsupported("Remote, launch, JavaScript and multimedia actions are disabled");
    }

    private static PdfNavigationTarget ResolveDestination(PdfDocument document, PdfObject value, HashSet<string> names, int depth)
    {
        if (depth >= 32) return Unsupported("Destination resolution depth exceeded");
        if (value is PdfIndirectReference reference) value = reference.GetRefersTo();
        var name = value switch { PdfString text => text.ToUnicodeString(), PdfName key => key.GetValue(), _ => null };
        if (name is not null)
        {
            if (!names.Add(name)) return Unsupported("Cyclic named destination");
            var resolved = document.GetCatalog().GetNameTree(PdfName.Dests).GetEntry(name) ??
                document.GetCatalog().GetPdfObject().GetAsDictionary(PdfName.Dests)?.Get(new PdfName(name));
            return resolved is null ? Unsupported("Named destination not found") : ResolveDestination(document, resolved, names, depth + 1);
        }
        if (value is PdfDictionary dictionary)
            return dictionary.Get(PdfName.D) is { } destination ? ResolveDestination(document, destination, names, depth + 1) : Unsupported("Malformed destination dictionary");
        if (value is not PdfArray array || array.Size() < 2 || array.GetAsDictionary(0) is not { } pageDictionary)
            return Unsupported("Unsupported explicit destination");
        var pageNumber = document.GetPageNumber(pageDictionary);
        if (pageNumber < 1 || pageNumber > document.GetNumberOfPages()) return Unsupported("Destination page is outside this document");
        var page = document.GetPage(pageNumber);
        var transform = Transform(page);
        var mode = array.GetAsName(1)?.GetValue();
        double? Number(int index)
        {
            if (index >= array.Size() || array.GetAsNumber(index) is not { } number) return null;
            var result = number.DoubleValue();
            return double.IsFinite(result) ? result : null;
        }
        if (mode is "Fit" or "FitB") return new PdfNavigationTarget(pageNumber);
        if (mode is "XYZ")
        {
            var left = Number(2);
            var top = Number(3);
            var point = transform.ToView(new PdfPoint(left ?? transform.CropBox.X, top ?? transform.CropBox.Bottom));
            var swap = transform.Rotation is 90 or 270;
            var zoom = Number(4);
            return new PdfNavigationTarget(pageNumber, PdfDestinationFit.KeepZoom,
                (swap ? top : left).HasValue ? point.X : null,
                (swap ? left : top).HasValue ? point.Y : null,
                zoom is > 0 ? zoom : null);
        }
        if (mode is "FitH" or "FitBH")
        {
            var top = Number(2);
            var point = transform.ToView(new PdfPoint(transform.CropBox.X, top ?? transform.CropBox.Bottom));
            return new PdfNavigationTarget(pageNumber, PdfDestinationFit.Width, Y: top.HasValue ? point.Y : null);
        }
        if (mode is "FitV" or "FitBV")
        {
            var left = Number(2);
            var point = transform.ToView(new PdfPoint(left ?? transform.CropBox.X, transform.CropBox.Bottom));
            return new PdfNavigationTarget(pageNumber, PdfDestinationFit.Height, X: left.HasValue ? point.X : null);
        }
        if (mode == "FitR" && Number(2) is { } x && Number(3) is { } y && Number(4) is { } right && Number(5) is { } topEdge && right > x && topEdge > y)
            return new PdfNavigationTarget(pageNumber, PdfDestinationFit.Rectangle, Region: transform.ToView(new PdfRect(x, y, right - x, topEdge - y)));
        return Unsupported("Destination fit mode is unsupported or malformed");
    }
}
