using System.Globalization;
using System.Text;
using ProPDF.Core;
using ProPDF.Kernel;
using static ProPDF.Kernel.PdfValues;

namespace ProPDF.Editing;

internal sealed class PdfGraph
{
    private readonly int _maximumPages;
    private readonly HashSet<PdfObjectId> _wrapped = [];
    public PdfGraph(PdfFile file, int maximumPages = 100_000)
    {
        File = file; _maximumPages = maximumPages;
        var visited = new HashSet<PdfObjectId>();
        void Read(PdfObject node, PdfDictionary inherited, int depth)
        {
            if (depth > file.Limits.MaximumDepth || node is not PdfReference reference || !visited.Add(reference.Id)) throw new InvalidDataException("Cyclic or invalid PDF page tree.");
            var dictionary = file.Dictionary(node); var effective = inherited.Copy();
            foreach (var key in new[] { "MediaBox", "CropBox", "Rotate", "Resources" }) if (dictionary.Contains(key)) effective[key] = dictionary[key];
            if (dictionary.Name("Type") == "Page")
            {
                if (Pages.Count >= maximumPages) throw new InvalidDataException("Page limit exceeded.");
                foreach (var item in effective) if (!dictionary.Contains(item.Key)) dictionary[item.Key] = item.Value;
                var page = new NativePage(this, reference, dictionary); _ = page.Transform;
                Pages.Add(page);
            }
            else if (dictionary.Name("Type") == "Pages") foreach (var child in file.Array(dictionary["Kids"])) Read(child, effective, depth + 1);
            else throw new InvalidDataException("Unexpected PDF page-tree node.");
        }
        Read(file.Catalog["Pages"], new PdfDictionary(), 0);
    }
    public PdfFile File { get; }
    public List<NativePage> Pages { get; } = [];
    public NativePage Page(int number) => number >= 1 && number <= Pages.Count ? Pages[number - 1] : throw new ArgumentOutOfRangeException(nameof(number));
    public PdfDictionary Dictionary(PdfObject value) => File.Dictionary(value);
    public PdfObject Resolve(PdfObject value) => File.Resolve(value);
    public PdfDictionary OptionalDictionary(PdfObject value) => Resolve(value) as PdfDictionary ?? new PdfDictionary();
    public PdfArray OptionalArray(PdfObject value) => Resolve(value) as PdfArray ?? new PdfArray();
    public PdfDictionary EnsureDictionary(PdfDictionary parent, string key)
    {
        if (Resolve(parent[key]) is PdfDictionary existing) return existing;
        var created = new PdfDictionary(); parent[key] = created; return created;
    }
    public void RebuildPages()
    {
        var root = File.Catalog["Pages"] as PdfReference ?? throw new InvalidDataException("Page root is not indirect.");
        var dictionary = Dictionary(root);
        dictionary["Kids"] = new PdfArray(Pages.Select(p => (PdfObject)p.Reference)); dictionary["Count"] = new PdfNumber(Pages.Count);
        foreach (var page in Pages) page.Dictionary["Parent"] = root;
    }
    public NativePage InsertBlank(int before, PdfSize size)
    {
        if (before < 1 || before > Pages.Count + 1 || Pages.Count >= _maximumPages || size.Width <= 0 || size.Height <= 0) throw new ArgumentOutOfRangeException(nameof(before));
        var dictionary = PdfValues.Dictionary(("Type", new PdfName("Page")), ("MediaBox", Numbers(0, 0, size.Width, size.Height)), ("Resources", new PdfDictionary()));
        var page = new NativePage(this, File.Add(dictionary), dictionary); Pages.Insert(before - 1, page); RebuildPages(); return page;
    }
    public string Resource(NativePage page, string category, PdfObject value)
    {
        var resources = OptionalDictionary(page.Dictionary["Resources"]).Copy(); page.Dictionary["Resources"] = resources;
        var collection = OptionalDictionary(resources[category]).Copy(); resources[category] = collection;
        var index = 1; while (collection.Contains("PP" + index)) index++;
        var name = "PP" + index; collection[name] = value is PdfReference ? value : File.Add(value); return name;
    }
    public void Append(NativePage page, string content)
    {
        var old = Resolve(page.Dictionary["Contents"]);
        var array = old is PdfArray contents ? new PdfArray(contents.Items) : old is PdfNull ? new PdfArray() : new PdfArray(page.Dictionary["Contents"]);
        if (_wrapped.Add(page.Reference.Id) && array.Count > 0)
        {
            array.Items.Insert(0, File.Add(PdfStream.FromDecoded("q\n"u8)));
            array.Items.Add(File.Add(PdfStream.FromDecoded("\nQ\n"u8)));
        }
        array.Items.Add(File.Add(PdfStream.FromDecoded(Encoding.ASCII.GetBytes(content)))); page.Dictionary["Contents"] = array;
    }
    public byte[] Content(NativePage page)
    {
        using var output = new MemoryStream();
        var value = Resolve(page.Dictionary["Contents"]); var streams = value is PdfArray array ? array.Items : value is PdfNull ? [] : new List<PdfObject> { value };
        foreach (var item in streams)
        {
            if (Resolve(item) is not PdfStream stream) throw new InvalidDataException("Invalid page content stream.");
            var bytes = File.Decode(stream);
            if (output.Length + bytes.Length + 1 > File.Limits.MaximumDecodedStreamBytes) throw new InvalidDataException("Page content exceeds byte budget.");
            output.Write(bytes); output.WriteByte(10);
        }
        return output.ToArray();
    }
    public PdfReference AddAnnotation(NativePage page, PdfDictionary annotation)
    {
        var annotations = OptionalArray(page.Dictionary["Annots"]); page.Dictionary["Annots"] = annotations;
        annotation["Type"] = new PdfName("Annot"); annotation["P"] = page.Reference;
        if (!annotation.Contains("NM")) annotation["NM"] = new PdfString(Guid.NewGuid().ToString("N"));
        var reference = File.Add(annotation); annotations.Items.Add(reference); return reference;
    }
    public IEnumerable<(PdfObject Raw, PdfDictionary Dictionary)> Annotations(NativePage page) =>
        OptionalArray(page.Dictionary["Annots"]).Select(raw => (raw, Dictionary(raw)));
    public static string AnnotationId(PdfObject raw, PdfDictionary dictionary) => dictionary["NM"] is PdfString id ? id.Text :
        raw is PdfReference reference ? "object:" + reference.Id.Number : "direct:" + Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(PdfObjectWriter.Serialize(dictionary)))[..16];
    public void InsertPages(PdfGraph source, IEnumerable<int> selected, int before)
    {
        var selection = selected.ToArray();
        if (before < 1 || before > Pages.Count + 1) throw new ArgumentOutOfRangeException(nameof(before));
        if (selection.Length == 0 || selection.Length + Pages.Count > _maximumPages) throw new ArgumentException("Invalid page selection.");
        if (!source.File.IsOwnerAuthorized) throw new UnauthorizedAccessException("Page import requires owner-authorized access.");
        if (source.File.Catalog.Contains("StructTreeRoot")) throw new NotSupportedException("Importing tagged page structure requires a dedicated structure-tree merge and is not yet supported.");
        foreach (var number in selection) source.Page(number);
        var targets = selection.Select(_ => File.Add(PdfNull.Value)).ToArray();
        var mapping = new Dictionary<PdfObjectId, PdfObject>();
        foreach (var page in source.Pages) mapping[page.Reference.Id] = PdfNull.Value;
        for (var i = 0; i < selection.Length; i++) if (mapping[source.Page(selection[i]).Reference.Id] is PdfNull) mapping[source.Page(selection[i]).Reference.Id] = targets[i];
        var imported = new List<NativePage>();
        var existingFieldNames = new HashSet<string>(ManagedPdfEditor.Fields(this).Select(field => field.Name), StringComparer.Ordinal);
        for (var index = 0; index < selection.Length; index++)
        {
            var sourcePage = source.Page(selection[index]);
            var map = new Dictionary<PdfObjectId, PdfObject>(mapping) { [sourcePage.Reference.Id] = targets[index] };
            PdfObject Copy(PdfObject value, int depth = 0)
            {
                if (depth > File.Limits.MaximumDepth) throw new InvalidDataException("Imported object nesting exceeded.");
                if (value is PdfReference reference)
                {
                    if (map.TryGetValue(reference.Id, out var mapped)) return mapped;
                    var destination = File.Add(PdfNull.Value); map[reference.Id] = destination;
                    File.Set(destination, Copy(source.Resolve(reference), depth + 1)); return destination;
                }
                if (value is PdfArray array) return new PdfArray(array.Select(item => Copy(item, depth + 1)));
                if (value is PdfDictionary dictionary)
                {
                    var result = new PdfDictionary();
                    var widget = dictionary.Name("Subtype") == "Widget";
                    foreach (var item in dictionary)
                    {
                        if (item.Key == "Parent" && (dictionary.Name("Type") == "Page" || widget)) continue;
                        if (widget && item.Key == "Kids") continue;
                        result[item.Key] = Copy(item.Value, depth + 1);
                    }
                    if (widget)
                    {
                        var parent = dictionary; var seen = new HashSet<PdfDictionary>(ReferenceEqualityComparer.Instance);
                        while (seen.Add(parent))
                        {
                            foreach (var key in new[] { "FT", "T", "V", "DV", "Ff", "DA", "Opt", "MaxLen", "Q" })
                                if (!result.Contains(key) && parent.Contains(key)) result[key] = Copy(parent[key], depth + 1);
                            if (source.Resolve(parent["Parent"]) is not PdfDictionary next) break;
                            parent = next;
                        }
                        if (result.Name("FT") == "Sig") result.Remove("V");
                    }
                    return result;
                }
                if (value is PdfStream stream) return new PdfStream((PdfDictionary)Copy(stream.Dictionary, depth + 1), stream.EncodedBytes);
                if (value is PdfString text) return new PdfString(text.Bytes);
                return value;
            }
            var dictionary = (PdfDictionary)Copy(sourcePage.Dictionary);
            File.Set(targets[index], dictionary); var page = new NativePage(this, targets[index], dictionary); imported.Add(page);
            foreach (var (raw, annotation) in Annotations(page).ToArray())
                if (annotation.Name("Subtype") == "Widget")
                {
                    if (annotation.Name("FT") == "Sig") { OptionalArray(dictionary["Annots"]).Items.Remove(raw); continue; }
                    var form = EnsureDictionary(File.Catalog, "AcroForm"); var fields = OptionalArray(form["Fields"]); form["Fields"] = fields;
                    var name = annotation.Text("T", "Imported");
                    var suffix = 1; var unique = name; while (!existingFieldNames.Add(unique)) unique = name + "_copy" + suffix++;
                    annotation["T"] = new PdfString(unique); fields.Items.Add(raw);
                }
        }
        Pages.InsertRange(before - 1, imported); RebuildPages();
    }
    public static string F(double value)
    { if (!double.IsFinite(value)) throw new ArgumentOutOfRangeException(nameof(value)); return value.ToString("0.########", CultureInfo.InvariantCulture); }
    public static string Hex(ReadOnlySpan<byte> bytes) => "<" + Convert.ToHexString(bytes) + ">";
    public static PdfRect Rectangle(PdfFile file, PdfObject value)
    {
        var array = file.Array(value);
        if (array.Count != 4) throw new InvalidDataException("Invalid PDF rectangle.");
        var numbers = array.Select(n => file.Resolve(n) is PdfNumber number ? number.Value : throw new InvalidDataException("Invalid PDF rectangle coordinate.")).ToArray();
        return new PdfRect(Math.Min(numbers[0], numbers[2]), Math.Min(numbers[1], numbers[3]), Math.Abs(numbers[2] - numbers[0]), Math.Abs(numbers[3] - numbers[1]));
    }
}

internal sealed class NativePage(PdfGraph graph, PdfReference reference, PdfDictionary dictionary)
{
    public PdfGraph Graph { get; } = graph;
    public PdfReference Reference { get; } = reference;
    public PdfDictionary Dictionary { get; } = dictionary;
    public PdfPageTransform Transform
    {
        get
        {
            var media = PdfGraph.Rectangle(Graph.File, Dictionary["MediaBox"]);
            var crop = Dictionary["CropBox"] is PdfNull ? media : PdfGraph.Rectangle(Graph.File, Dictionary["CropBox"]).Intersect(media);
            var rotation = Graph.Resolve(Dictionary["Rotate"]) is PdfNumber rotate ? checked((int)rotate.Integer) : 0;
            return new PdfPageTransform(crop, rotation, Dictionary.Number("UserUnit", 1));
        }
    }
    public void Validate(PdfRect bounds)
    {
        var size = Transform.ViewSize;
        if (bounds.IsEmpty || bounds.X < 0 || bounds.Y < 0 || bounds.Right > size.Width + .01 || bounds.Bottom > size.Height + .01)
            throw new ArgumentOutOfRangeException(nameof(bounds), "Region must be inside the visible page.");
    }
    public string ViewMatrix()
    {
        var transform = Transform; var p = transform.ToPdf(new PdfPoint(0, 0)); var x = transform.ToPdf(new PdfPoint(1, 0)); var y = transform.ToPdf(new PdfPoint(0, 1));
        return string.Join(" ", new[] { x.X-p.X, x.Y-p.Y, y.X-p.X, y.Y-p.Y, p.X, p.Y }.Select(PdfGraph.F)) + " cm\n";
    }
}
