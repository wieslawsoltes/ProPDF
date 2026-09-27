using System.Globalization;
using ProPDF.Core;
using ProPDF.Kernel;
using static ProPDF.Kernel.PdfValues;

namespace ProPDF.Editing;

public sealed partial class ManagedPdfEditor
{
    private sealed class OutlineNode(PdfReference reference, PdfDictionary dictionary, OutlineNode? parent)
    {
        public PdfReference Reference { get; } = reference;
        public PdfDictionary Dictionary { get; } = dictionary;
        public OutlineNode? Parent { get; set; } = parent;
        public List<OutlineNode> Children { get; } = [];
        public bool IsOpen { get; set; } = dictionary.Number("Count") >= 0;
    }

    private static OutlineNode ReadOutlineTree(PdfGraph graph, PdfNavigationOptions settings)
    {
        var raw = graph.File.Catalog["Outlines"];
        var dictionary = graph.Resolve(raw) as PdfDictionary ?? Dictionary(("Type", new PdfName("Outlines")));
        var root = new OutlineNode(raw as PdfReference ?? graph.File.Add(dictionary), dictionary, null);
        var seen = new HashSet<PdfDictionary>(ReferenceEqualityComparer.Instance) { dictionary };
        var stack = new Stack<(PdfObject Raw, OutlineNode Parent, int Depth)>();
        if (dictionary["First"] is not PdfNull) stack.Push((dictionary["First"], root, 0));
        var count = 0;
        while (stack.TryPop(out var item))
        {
            graph.File.CancellationToken.ThrowIfCancellationRequested();
            var entry = graph.Dictionary(item.Raw);
            if (!seen.Add(entry)) throw new InvalidDataException("Cyclic or multiply referenced outline entries.");
            if (++count > settings.MaximumEntries || item.Depth >= settings.MaximumDepth)
                throw new InvalidDataException("Outline size or depth limit exceeded.");
            if (entry.Text("Title").Length > 16_384) throw new InvalidDataException("Bookmark title exceeds its limit.");
            var node = new OutlineNode(item.Raw as PdfReference ?? graph.File.Add(entry), entry, item.Parent);
            item.Parent.Children.Add(node);
            if (entry["Next"] is not PdfNull) stack.Push((entry["Next"], item.Parent, item.Depth));
            if (entry["First"] is not PdfNull) stack.Push((entry["First"], node, item.Depth + 1));
        }
        return root;
    }

    private static IEnumerable<(OutlineNode Node, string Path, int Depth)> OutlineEntries(OutlineNode root)
    {
        var stack = new Stack<(OutlineNode Node, string Path, int Depth)>();
        for (var i = root.Children.Count - 1; i >= 0; i--) stack.Push((root.Children[i], i.ToString(CultureInfo.InvariantCulture), 0));
        while (stack.TryPop(out var item))
        {
            yield return item;
            for (var i = item.Node.Children.Count - 1; i >= 0; i--)
                stack.Push((item.Node.Children[i], item.Path + "/" + i.ToString(CultureInfo.InvariantCulture), item.Depth + 1));
        }
    }

    private static OutlineNode OutlineAt(OutlineNode root, string? path, bool allowRoot = false)
    {
        if (string.IsNullOrEmpty(path)) return allowRoot ? root : throw new ArgumentException("A bookmark path is required.");
        if (path.Length > 4096) throw new ArgumentOutOfRangeException(nameof(path));
        var parts = path.Split('/'); if (parts.Length > 128) throw new ArgumentOutOfRangeException(nameof(path));
        var node = root;
        foreach (var part in parts)
        {
            if (!int.TryParse(part, NumberStyles.None, CultureInfo.InvariantCulture, out var i) || i < 0 || i >= node.Children.Count)
                throw new KeyNotFoundException($"Bookmark path does not exist: {path}");
            node = node.Children[i];
        }
        return node;
    }

    private static void EditOutline(PdfGraph graph, PdfOutlineEdit operation)
    {
        var root = ReadOutlineTree(graph, new PdfNavigationOptions());
        static void Title(string title)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(title);
            if (title.Length > 16_384) throw new ArgumentOutOfRangeException(nameof(title));
        }
        static int Position(int requested, int count)
        {
            var position = requested == -1 ? count : requested;
            if (position < 0 || position > count) throw new ArgumentOutOfRangeException(nameof(requested));
            return position;
        }
        PdfArray Destination(int page) => new(graph.Page(page).Reference, new PdfName("Fit"));
        switch (operation)
        {
            case InsertOutline insert:
                Title(insert.Title);
                var parent = OutlineAt(root, insert.ParentPath, true);
                var dictionary = Dictionary(("Title", new PdfString(insert.Title)), ("Dest", Destination(insert.PageNumber)));
                parent.Children.Insert(Position(insert.Position, parent.Children.Count), new OutlineNode(graph.File.Add(dictionary), dictionary, parent) { IsOpen = insert.IsOpen });
                break;
            case UpdateOutline update:
                var node = OutlineAt(root, update.Path);
                if (update.Title is not null) { Title(update.Title); node.Dictionary["Title"] = new PdfString(update.Title); }
                if (update.PageNumber is { } number) { node.Dictionary.Remove("A"); node.Dictionary["Dest"] = Destination(number); }
                if (update.IsOpen.HasValue) node.IsOpen = update.IsOpen.Value;
                break;
            case DeleteOutline delete:
                var deleted = OutlineAt(root, delete.Path); deleted.Parent!.Children.Remove(deleted); break;
            case MoveOutline move:
                var moved = OutlineAt(root, move.Path); var target = OutlineAt(root, move.ParentPath, true);
                for (var ancestor = target; ancestor is not null; ancestor = ancestor.Parent)
                    if (ReferenceEquals(ancestor, moved)) throw new InvalidOperationException("A bookmark cannot move into itself or its descendants.");
                var position = Position(move.Position, target.Children.Count - (ReferenceEquals(moved.Parent, target) ? 1 : 0));
                moved.Parent!.Children.Remove(moved); target.Children.Insert(position, moved); moved.Parent = target; break;
            default: throw new NotSupportedException(operation.GetType().Name);
        }
        // Rebuild structural links without cloning content, preserving actions, style and destinations.
        var entries = OutlineEntries(root).ToArray();
        if (entries.Length > 100_000 || entries.Any(entry => entry.Depth >= 128)) throw new InvalidDataException("Edited outline exceeds its size/depth budget.");
        foreach (var entry in entries.Select(e => e.Node).Prepend(root))
        {
            graph.File.CancellationToken.ThrowIfCancellationRequested();
            foreach (var key in new[] { "First", "Last", "Next", "Prev", "Parent", "Count" }) entry.Dictionary.Remove(key);
        }
        foreach (var entry in entries.Select(e => e.Node).Prepend(root))
        {
            if (entry.Children.Count == 0) continue;
            entry.Dictionary["First"] = entry.Children[0].Reference; entry.Dictionary["Last"] = entry.Children[^1].Reference;
            for (var i = 0; i < entry.Children.Count; i++)
            {
                var child = entry.Children[i]; child.Dictionary["Parent"] = entry.Reference;
                if (i > 0) child.Dictionary["Prev"] = entry.Children[i - 1].Reference;
                if (i + 1 < entry.Children.Count) child.Dictionary["Next"] = entry.Children[i + 1].Reference;
            }
        }
        var visible = new Dictionary<OutlineNode, int>(ReferenceEqualityComparer.Instance);
        foreach (var node in entries.Select(e => e.Node).Reverse().Append(root))
        {
            var count = node.Children.Sum(child => 1 + (child.IsOpen ? visible.GetValueOrDefault(child) : 0));
            visible[node] = count;
            if (node.Children.Count != 0) node.Dictionary["Count"] = new PdfNumber(node.IsOpen || node.Parent is null ? count : -count);
        }
        if (root.Children.Count == 0) graph.File.Catalog.Remove("Outlines");
        else graph.File.Catalog["Outlines"] = root.Reference;
    }

    public Task<PdfDocumentNavigation> ReadNavigationAsync(PdfSnapshot source, PdfNavigationOptions? options = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(source);
        var settings = options ?? new PdfNavigationOptions();
        if (settings.MaximumEntries is < 1 or > 100_000 || settings.MaximumDepth is < 1 or > 512) throw new ArgumentOutOfRangeException(nameof(options));
        return Task.Run(() => ReadNavigation(Open(source, cancellationToken), source.Id, settings), cancellationToken);
    }

    private static PdfDocumentNavigation ReadNavigation(PdfGraph graph, Guid revision, PdfNavigationOptions settings)
    {
        Dictionary<string, PdfObject>? names = null;
        var pageNumbers = graph.Pages.Select((page, index) => (page.Dictionary, Number: index + 1)).ToDictionary(p => p.Dictionary, p => p.Number);
        PdfNavigationTarget Unsupported(string reason) => new(UnsupportedReason: reason);
        PdfNavigationTarget ResolveDestination(PdfObject value, HashSet<string> visited, int depth)
        {
            if (depth >= 32) return Unsupported("Destination resolution depth exceeded");
            graph.File.CancellationToken.ThrowIfCancellationRequested();
            value = graph.Resolve(value);
            var name = value switch { PdfString s => s.Text, PdfName n => n.Value, _ => null };
            if (name is not null)
            {
                if (!visited.Add(name)) return Unsupported("Cyclic named destination");
                names ??= Names(graph, "Dests");
                var resolved = names.GetValueOrDefault(name) ?? graph.OptionalDictionary(graph.File.Catalog["Dests"])[name];
                return resolved is PdfNull ? Unsupported("Named destination not found") : ResolveDestination(resolved, visited, depth + 1);
            }
            if (value is PdfDictionary d) return d.Contains("D") ? ResolveDestination(d["D"], visited, depth + 1) : Unsupported("Malformed destination dictionary");
            if (value is not PdfArray array || array.Count < 2 || graph.Resolve(array[0]) is not PdfDictionary pageDictionary || !pageNumbers.TryGetValue(pageDictionary, out var pageNumber))
                return Unsupported("Destination page is outside this document or malformed");
            var transform = graph.Page(pageNumber).Transform;
            double? Number(int index) => index < array.Count && graph.Resolve(array[index]) is PdfNumber number ? number.Value : null;
            var mode = (graph.Resolve(array[1]) as PdfName)?.Value;
            if (mode is "Fit" or "FitB") return new PdfNavigationTarget(pageNumber);
            if (mode == "XYZ")
            {
                var left = Number(2); var top = Number(3);
                var point = transform.ToView(new PdfPoint(left ?? transform.CropBox.X, top ?? transform.CropBox.Bottom));
                var swap = transform.Rotation is 90 or 270; var zoom = Number(4);
                return new PdfNavigationTarget(pageNumber, PdfDestinationFit.KeepZoom,
                    (swap ? top : left).HasValue ? point.X : null, (swap ? left : top).HasValue ? point.Y : null, zoom is > 0 ? zoom : null);
            }
            if (mode is "FitH" or "FitBH")
            {
                var top = Number(2); var point = transform.ToView(new PdfPoint(transform.CropBox.X, top ?? transform.CropBox.Bottom));
                return transform.Rotation is 90 or 270
                    ? new PdfNavigationTarget(pageNumber, PdfDestinationFit.Height, X: top.HasValue ? point.X : null)
                    : new PdfNavigationTarget(pageNumber, PdfDestinationFit.Width, Y: top.HasValue ? point.Y : null);
            }
            if (mode is "FitV" or "FitBV")
            {
                var left = Number(2); var point = transform.ToView(new PdfPoint(left ?? transform.CropBox.X, transform.CropBox.Bottom));
                return transform.Rotation is 90 or 270
                    ? new PdfNavigationTarget(pageNumber, PdfDestinationFit.Width, Y: left.HasValue ? point.Y : null)
                    : new PdfNavigationTarget(pageNumber, PdfDestinationFit.Height, X: left.HasValue ? point.X : null);
            }
            if (mode == "FitR" && Number(2) is { } x && Number(3) is { } y && Number(4) is { } right && Number(5) is { } topEdge && right > x && topEdge > y)
                return new PdfNavigationTarget(pageNumber, PdfDestinationFit.Rectangle, Region: transform.ToView(new PdfRect(x, y, right - x, topEdge - y)));
            return Unsupported("Destination fit mode is unsupported or malformed");
        }
        PdfNavigationTarget Target(PdfDictionary entry)
        {
            if (entry.Contains("Dest")) return ResolveDestination(entry["Dest"], new HashSet<string>(StringComparer.Ordinal), 0);
            if (graph.Resolve(entry["A"]) is not PdfDictionary action) return Unsupported("No destination");
            if (action.Contains("Next")) return Unsupported("Chained actions are disabled");
            return action.Name("S") switch
            {
                "GoTo" => ResolveDestination(action["D"], new HashSet<string>(StringComparer.Ordinal), 0),
                "URI" => PdfUriPolicy.TryNormalize(action.Text("URI"), out var uri) ? new PdfNavigationTarget(ExternalUri: uri!.AbsoluteUri) : Unsupported("URI scheme or credentials are not permitted"),
                "Named" => action.Name("N") is "FirstPage" or "LastPage" or "NextPage" or "PrevPage" ? new PdfNavigationTarget(NamedAction: action.Name("N")) : Unsupported("Unsupported named action"),
                _ => Unsupported("Remote, launch, JavaScript and multimedia actions are disabled")
            };
        }
        var bookmarks = OutlineEntries(ReadOutlineTree(graph, settings)).Select(entry => new PdfNavigationBookmark(entry.Path,
            entry.Node.Dictionary.Text("Title", "Untitled bookmark"), entry.Depth, entry.Node.IsOpen, Target(entry.Node.Dictionary))).ToArray();
        var links = new List<PdfNavigationLink>();
        for (var number = 1; number <= graph.Pages.Count; number++)
        {
            graph.File.CancellationToken.ThrowIfCancellationRequested(); var page = graph.Page(number);
            foreach (var (raw, annotation) in graph.Annotations(page))
            {
                if (annotation.Name("Subtype") != "Link") continue;
                if (bookmarks.Length + links.Count >= settings.MaximumEntries) throw new InvalidDataException("Navigation entry limit exceeded.");
                links.Add(new PdfNavigationLink(number, PdfGraph.AnnotationId(raw, annotation), page.Transform.ToView(PdfGraph.Rectangle(graph.File, annotation["Rect"])), Target(annotation)));
            }
        }
        return new PdfDocumentNavigation(revision, Array.AsReadOnly(bookmarks), links.AsReadOnly());
    }
}
