using System.Text;
using ProPDF.Core;
using ProPDF.Kernel;
using static ProPDF.Editing.PdfGraph;

namespace ProPDF.Editing;

public sealed partial class ManagedPdfEditor
{
    private readonly record struct Affine(double A, double B, double C, double D, double E, double F)
    {
        public static Affine Identity => new(1, 0, 0, 1, 0, 0);
        public Affine ThenLocal(Affine local) => new(A * local.A + C * local.B, B * local.A + D * local.B,
            A * local.C + C * local.D, B * local.C + D * local.D, A * local.E + C * local.F + E, B * local.E + D * local.F + F);
        public PdfPoint Point(double x, double y) => new(A * x + C * y + E, B * x + D * y + F);
        public PdfRect Bounds(PdfRect bounds)
        {
            var points = new[] { Point(bounds.X, bounds.Y), Point(bounds.Right, bounds.Y), Point(bounds.Right, bounds.Bottom), Point(bounds.X, bounds.Bottom) };
            return new PdfRect(points.Min(p => p.X), points.Min(p => p.Y), points.Max(p => p.X) - points.Min(p => p.X), points.Max(p => p.Y) - points.Min(p => p.Y));
        }
        public double MaximumScale => Math.Sqrt(A * A + B * B + C * C + D * D);
    }
    private sealed record RedactionFont(bool Composite, PdfRect Bounds, Func<int, double> Width);
    private sealed class RedactionState
    {
        public Affine Ctm = Affine.Identity;
        public double LineWidth = 1, MiterLimit = 10, FontSize = 0, CharacterSpace, WordSpace, HorizontalScale = 1, Leading, Rise;
        public string? FontName;
        public RedactionFont? Font;
        public RedactionState Copy() => (RedactionState)MemberwiseClone();
    }
    private sealed class TextGroup(int start)
    {
        public int Start { get; } = start;
        public bool Intersects;
        public HashSet<string> Fonts { get; } = new(StringComparer.Ordinal);
        public List<int> Shows { get; } = [];
    }

    /// <summary>Conservative native object removal. Intersecting text objects, paths and XObject invocations are removed in full.</summary>
    private static void Redact(PdfGraph graph, RedactRegion operation, CancellationToken token)
    {
        var page = graph.Page(operation.PageNumber); page.Validate(operation.Bounds);
        if (graph.File.Catalog.Contains("StructTreeRoot") || page.Dictionary.Contains("StructParents"))
            throw new NotSupportedException("Tagged content redaction requires structure/ActualText qualification and is disabled.");
        var region = page.Transform.ToPdf(operation.Bounds);
        var input = PdfContent.Read(graph.Content(page), cancellationToken: token);
        var output = input.Cast<PdfContentInstruction?>().ToArray();
        var resources = graph.OptionalDictionary(page.Dictionary["Resources"]);
        var fontResources = graph.OptionalDictionary(resources["Font"]); var xobjects = graph.OptionalDictionary(resources["XObject"]);
        var gsResources = graph.OptionalDictionary(resources["ExtGState"]); var shadings = graph.OptionalDictionary(resources["Shading"]);
        var usedFonts = new HashSet<string>(StringComparer.Ordinal); var usedObjects = new HashSet<string>(StringComparer.Ordinal); var usedShadings = new HashSet<string>(StringComparer.Ordinal);
        var fonts = new Dictionary<string, RedactionFont>(StringComparer.Ordinal);
        var state = new RedactionState(); var stack = new Stack<RedactionState>();
        var textMatrix = Affine.Identity; var lineMatrix = Affine.Identity; TextGroup? text = null;
        var pathInstructions = new List<int>(); var pathPoints = new List<PdfPoint>(); var clipping = false;
        var markedDepth = 0; var compatibility = 0;
        static bool Touches(PdfRect a, PdfRect b) => a.X <= b.Right && a.Right >= b.X && a.Y <= b.Bottom && a.Bottom >= b.Y;
        static string Name(PdfObject operand) => operand is PdfName name ? name.Value : throw new InvalidDataException("Expected name operand.");
        static double Number(PdfObject operand) => operand is PdfNumber number ? number.Value : throw new InvalidDataException("Expected numeric operand.");
        static void Count(PdfContentInstruction instruction, int count)
        { if (instruction.Operands.Count != count) throw new InvalidDataException($"Wrong operand count for {instruction.Operator}."); }
        void RequireText() { if (text is null) throw new InvalidDataException("Text operator outside BT/ET."); }
        void RequireGraphics() { if (text is not null) throw new NotSupportedException("Graphics painting inside a text object is not qualified for redaction."); }
        void NextLine() { lineMatrix = lineMatrix.ThenLocal(new Affine(1, 0, 0, 1, 0, -state.Leading)); textMatrix = lineMatrix; }
        void Show(PdfObject value)
        {
            RequireText();
            var font = state.Font ?? throw new InvalidDataException("Text has no font.");
            if (state.FontSize == 0) throw new InvalidDataException("Text has no nonzero font size.");
            var bytes = (value as PdfString)?.ToArray() ?? throw new InvalidDataException("Text show operand is not a string.");
            if (font.Composite && bytes.Length % 2 != 0) throw new InvalidDataException("Odd Identity-H character code length.");
            text!.Fonts.Add(state.FontName!);
            for (var i = 0; i < bytes.Length; i++)
            {
                if (i % 512 == 0) token.ThrowIfCancellationRequested();
                var code = font.Composite ? (bytes[i++] << 8) | bytes[i] : bytes[i];
                var matrix = state.Ctm.ThenLocal(textMatrix).ThenLocal(new Affine(state.FontSize * state.HorizontalScale / 1000, 0, 0, state.FontSize / 1000, 0, state.Rise));
                if (Touches(matrix.Bounds(font.Bounds), region)) text.Intersects = true;
                var advance = (font.Width(code) / 1000 * state.FontSize + state.CharacterSpace + (code == 32 && !font.Composite ? state.WordSpace : 0)) * state.HorizontalScale;
                textMatrix = textMatrix.ThenLocal(new Affine(1, 0, 0, 1, advance, 0));
            }
        }
        for (var index = 0; index < input.Count; index++)
        {
            token.ThrowIfCancellationRequested(); var instruction = input[index]; var p = instruction.Operands;
            double N(int i) => Number(p[i]);
            void PathPoint(double x, double y) { pathPoints.Add(state.Ctm.Point(x, y)); if (pathPoints.Count > 1_000_000) throw new InvalidDataException("Path budget exceeded."); }
            switch (instruction.Operator)
            {
                case "q": RequireGraphics(); Count(instruction, 0); if (stack.Count >= 128) throw new InvalidDataException("Graphics state nesting exceeded."); stack.Push(state.Copy()); break;
                case "Q": RequireGraphics(); Count(instruction, 0); if (!stack.TryPop(out var restored)) throw new InvalidDataException("Unbalanced graphics state."); state = restored; break;
                case "cm":
                    RequireGraphics(); Count(instruction, 6); state.Ctm = state.Ctm.ThenLocal(new Affine(N(0), N(1), N(2), N(3), N(4), N(5))); break;
                case "w": Count(instruction, 1); state.LineWidth = Math.Abs(N(0)); break;
                case "M": Count(instruction, 1); state.MiterLimit = Math.Max(1, Math.Abs(N(0))); break;
                case "gs":
                    Count(instruction, 1); var gs = graph.Dictionary(gsResources[Name(p[0])]);
                    if (gs.Contains("SMask") && gs.Name("SMask") != "None") throw new NotSupportedException("Soft-mask redaction is not qualified; use explicit raster flattening in a separate workflow.");
                    if (gs.Contains("Font")) throw new NotSupportedException("ExtGState font overrides require a separate redaction qualification path.");
                    if (gs.Contains("LW")) state.LineWidth = Math.Abs(gs.Number("LW")); if (gs.Contains("ML")) state.MiterLimit = Math.Max(1, Math.Abs(gs.Number("ML"))); break;
                case "m": case "l": RequireGraphics(); Count(instruction, 2); PathPoint(N(0), N(1)); pathInstructions.Add(index); break;
                case "c": RequireGraphics(); Count(instruction, 6); for (var j = 0; j < 6; j += 2) PathPoint(N(j), N(j + 1)); pathInstructions.Add(index); break;
                case "v": case "y": RequireGraphics(); Count(instruction, 4); for (var j = 0; j < 4; j += 2) PathPoint(N(j), N(j + 1)); pathInstructions.Add(index); break;
                case "re":
                    RequireGraphics(); Count(instruction, 4); PathPoint(N(0), N(1)); PathPoint(N(0) + N(2), N(1)); PathPoint(N(0) + N(2), N(1) + N(3)); PathPoint(N(0), N(1) + N(3)); pathInstructions.Add(index); break;
                case "h": RequireGraphics(); Count(instruction, 0); pathInstructions.Add(index); break;
                case "W": case "W*": RequireGraphics(); Count(instruction, 0); clipping = true; break;
                case "S": case "s": case "f": case "F": case "f*": case "B": case "B*": case "b": case "b*": case "n":
                    RequireGraphics(); Count(instruction, 0);
                    if (pathPoints.Count > 0 && instruction.Operator != "n")
                    {
                        var x = pathPoints.Min(v => v.X); var y = pathPoints.Min(v => v.Y);
                        var stroke = instruction.Operator is "S" or "s" or "B" or "B*" or "b" or "b*";
                        var margin = stroke ? Math.Max(1, state.LineWidth) * state.Ctm.MaximumScale * Math.Max(2, state.MiterLimit) : 0;
                        var bounds = new PdfRect(x - margin, y - margin, pathPoints.Max(v => v.X) - x + margin * 2, pathPoints.Max(v => v.Y) - y + margin * 2);
                        if (Touches(bounds, region))
                        {
                            if (clipping) throw new NotSupportedException("An intersecting path simultaneously used as a clip cannot be safely removed by this native redaction path.");
                            foreach (var pathIndex in pathInstructions) output[pathIndex] = null;
                            output[index] = null;
                        }
                    }
                    pathInstructions.Clear(); pathPoints.Clear(); clipping = false; break;
                case "BT":
                    Count(instruction, 0); if (text is not null) throw new InvalidDataException("Nested BT text object.");
                    text = new TextGroup(index); textMatrix = lineMatrix = Affine.Identity; break;
                case "ET":
                    Count(instruction, 0); RequireText();
                    if (text!.Intersects)
                    {
                        foreach (var show in text.Shows)
                        {
                            var original = input[show];
                            // Quote operators also change persistent text state. Preserve that state, but no string operands.
                            if (original.Operator == "\"")
                            {
                                output[show] = new PdfContentInstruction("Tw", [original.Operands[0]]);
                                // Additional state is emitted immediately below when serializing this replacement.
                            }
                            else output[show] = original.Operator == "'" ? new PdfContentInstruction("T*", []) : null;
                        }
                    }
                    else usedFonts.UnionWith(text.Fonts);
                    text = null; break;
                case "Tf":
                    Count(instruction, 2); state.FontName = Name(p[0]); state.FontSize = N(1);
                    if (Math.Abs(state.FontSize) > 1_000_000) throw new InvalidDataException("Excessive font size.");
                    if (!fonts.TryGetValue(state.FontName, out var font)) fonts[state.FontName] = font = ReadRedactionFont(graph, fontResources[state.FontName]);
                    state.Font = font; break;
                case "Tc": Count(instruction, 1); state.CharacterSpace = N(0); break;
                case "Tw": Count(instruction, 1); state.WordSpace = N(0); break;
                case "Tz": Count(instruction, 1); state.HorizontalScale = N(0) / 100; break;
                case "TL": Count(instruction, 1); state.Leading = N(0); break;
                case "Ts": Count(instruction, 1); state.Rise = N(0); break;
                case "Tr": Count(instruction, 1); if (N(0) < 0 || N(0) >= 4 || N(0) != Math.Truncate(N(0))) throw new NotSupportedException("Text clipping render modes are not qualified for native redaction."); break;
                case "Tm": RequireText(); Count(instruction, 6); textMatrix = lineMatrix = new Affine(N(0), N(1), N(2), N(3), N(4), N(5)); break;
                case "Td": case "TD":
                    RequireText(); Count(instruction, 2); if (instruction.Operator == "TD") state.Leading = -N(1);
                    lineMatrix = lineMatrix.ThenLocal(new Affine(1, 0, 0, 1, N(0), N(1))); textMatrix = lineMatrix; break;
                case "T*": RequireText(); Count(instruction, 0); NextLine(); break;
                case "Tj": RequireText(); Count(instruction, 1); Show(p[0]); text!.Shows.Add(index); break;
                case "TJ":
                    RequireText(); Count(instruction, 1); if (p[0] is not PdfArray array) throw new InvalidDataException("TJ expects an array.");
                    foreach (var item in array)
                        if (item is PdfNumber number) textMatrix = textMatrix.ThenLocal(new Affine(1, 0, 0, 1, -number.Value / 1000 * state.FontSize * state.HorizontalScale, 0));
                        else Show(item);
                    text!.Shows.Add(index); break;
                case "'": RequireText(); Count(instruction, 1); NextLine(); Show(p[0]); text!.Shows.Add(index); break;
                case "\"": RequireText(); Count(instruction, 3); state.WordSpace = N(0); state.CharacterSpace = N(1); NextLine(); Show(p[2]); text!.Shows.Add(index); break;
                case "Do":
                    RequireGraphics(); Count(instruction, 1); var name = Name(p[0]);
                    var xobject = graph.Resolve(xobjects[name]) as PdfStream ?? throw new InvalidDataException("Missing image/form XObject.");
                    var matrix = state.Ctm; PdfRect box;
                    if (xobject.Dictionary.Name("Subtype") == "Image") box = new PdfRect(0, 0, 1, 1);
                    else if (xobject.Dictionary.Name("Subtype") == "Form")
                    {
                        box = Rectangle(graph.File, xobject.Dictionary["BBox"]);
                        if (graph.Resolve(xobject.Dictionary["Matrix"]) is PdfArray m)
                        {
                            if (m.Count != 6) throw new InvalidDataException("Invalid form XObject matrix.");
                            matrix = matrix.ThenLocal(new Affine(Number(graph.Resolve(m[0])), Number(graph.Resolve(m[1])), Number(graph.Resolve(m[2])), Number(graph.Resolve(m[3])), Number(graph.Resolve(m[4])), Number(graph.Resolve(m[5]))));
                        }
                    }
                    else throw new NotSupportedException("Unknown XObject subtype cannot be qualified for redaction.");
                    if (Touches(matrix.Bounds(box), region)) output[index] = null; else usedObjects.Add(name); break;
                case "sh":
                    RequireGraphics(); Count(instruction, 1); var shadingName = Name(p[0]); var shadingRaw = graph.Resolve(shadings[shadingName]);
                    var shading = shadingRaw is PdfStream shadingStream ? shadingStream.Dictionary : shadingRaw as PdfDictionary ?? throw new InvalidDataException("Missing shading.");
                    if (!shading.Contains("BBox") || Touches(state.Ctm.Bounds(Rectangle(graph.File, shading["BBox"])), region)) output[index] = null; else usedShadings.Add(shadingName); break;
                case "CS": case "cs":
                    Count(instruction, 1); if (Name(p[0]) == "Pattern" || graph.Resolve(graph.OptionalDictionary(resources["ColorSpace"])[Name(p[0])]) is PdfArray pattern && pattern.Count > 0 && pattern[0] is PdfName { Value: "Pattern" })
                        throw new NotSupportedException("Pattern redaction is not qualified."); break;
                case "SCN": case "scn": if (p.Any(value => value is PdfName)) throw new NotSupportedException("Pattern paint is not qualified for redaction."); break;
                case "BMC": Count(instruction, 1); if (++markedDepth > 128) throw new InvalidDataException("Marked-content nesting exceeded."); break;
                case "EMC": Count(instruction, 0); if (--markedDepth < 0) throw new InvalidDataException("Unbalanced marked content."); break;
                case "MP": Count(instruction, 1); break;
                case "BDC": case "DP": throw new NotSupportedException("Marked-content properties may carry ActualText; semantic redaction is not qualified.");
                case "BX": Count(instruction, 0); compatibility++; break;
                case "EX": Count(instruction, 0); if (--compatibility < 0) throw new InvalidDataException("Unbalanced compatibility section."); break;
                case "J": case "j": case "d": case "ri": case "i": case "SC": case "sc": case "G": case "g": case "RG": case "rg": case "K": case "k": break;
                default: throw new NotSupportedException($"Content operator '{instruction.Operator}' is not qualified for native redaction.");
            }
        }
        if (text is not null || stack.Count != 0 || markedDepth != 0 || compatibility != 0 || pathPoints.Count != 0) throw new InvalidDataException("Unbalanced or unfinished page content.");
        var rewritten = new List<PdfContentInstruction>();
        for (var i = 0; i < output.Length; i++)
        {
            if (output[i] is not { } instruction) continue;
            if (instruction.Operator == "Tf" && !usedFonts.Contains(Name(instruction.Operands[0]))) continue;
            rewritten.Add(instruction);
            if (input[i].Operator == "\"" && instruction.Operator == "Tw")
            { rewritten.Add(new PdfContentInstruction("Tc", [input[i].Operands[1]])); rewritten.Add(new PdfContentInstruction("T*", [])); }
        }
        var pruned = resources.Copy();
        void Prune(string category, HashSet<string> keep)
        { var dictionary = graph.OptionalDictionary(resources[category]); pruned[category] = new PdfDictionary(dictionary.Where(pair => keep.Contains(pair.Key))); }
        Prune("Font", usedFonts); Prune("XObject", usedObjects); Prune("Shading", usedShadings);
        page.Dictionary["Resources"] = pruned;
        page.Dictionary["Contents"] = graph.File.Add(PdfStream.FromDecoded(PdfContent.Write(rewritten, cancellationToken: token)));
        var removed = new HashSet<PdfObject>(ReferenceEqualityComparer.Instance); var removedIds = new HashSet<PdfObjectId>();
        foreach (var (raw, annotation) in graph.Annotations(page).ToArray())
        {
            if (!Touches(Rectangle(graph.File, annotation["Rect"]), region)) continue;
            if (annotation.Name("Subtype") == "Widget") throw new NotSupportedException("Flatten intersecting form widgets before redacting.");
            removed.Add(annotation); if (raw is PdfReference reference) removedIds.Add(reference.Id);
            graph.OptionalArray(page.Dictionary["Annots"]).Items.Remove(raw);
        }
        // Remove associated popups/replies rather than leave a back-reference to a deleted annotation and its appearance.
        bool IsRemoved(PdfObject value) => value is PdfReference reference ? removedIds.Contains(reference.Id) : removed.Contains(value);
        bool changed;
        do
        {
            changed = false;
            foreach (var other in graph.Pages)
                foreach (var (raw, annotation) in graph.Annotations(other).ToArray())
                    if (annotation.Name("Subtype") != "Widget" && (IsRemoved(annotation["Parent"]) || IsRemoved(annotation["IRT"])))
                    {
                        removed.Add(annotation); if (raw is PdfReference reference) removedIds.Add(reference.Id);
                        graph.OptionalArray(other.Dictionary["Annots"]).Items.Remove(raw); changed = true;
                    }
        } while (changed);
        var fill = operation.Fill ?? PdfColor.Black;
        graph.Append(page, $"q {page.ViewMatrix()} {Rgb(fill)} {F(operation.Bounds.X)} {F(operation.Bounds.Y)} {F(operation.Bounds.Width)} {F(operation.Bounds.Height)} re f Q\n");
    }

    private static RedactionFont ReadRedactionFont(PdfGraph graph, PdfObject raw)
    {
        var dictionary = graph.Dictionary(raw); var subtype = dictionary.Name("Subtype"); var composite = subtype == "Type0";
        var font = dictionary;
        if (composite)
        {
            if (dictionary.Name("Encoding") != "Identity-H") throw new NotSupportedException("Native redaction currently requires Identity-H for composite fonts.");
            var descendants = graph.OptionalArray(dictionary["DescendantFonts"]);
            if (descendants.Count != 1) throw new InvalidDataException("Invalid composite font.");
            font = graph.Dictionary(descendants[0]);
            if (font.Name("Subtype") is not ("CIDFontType2" or "CIDFontType0")) throw new NotSupportedException("Unsupported CID font.");
        }
        else if (subtype is not ("Type1" or "TrueType" or "MMType1")) throw new NotSupportedException("Type3 or unknown font redaction is not qualified.");
        var descriptor = graph.OptionalDictionary(font["FontDescriptor"]);
        var bounds = descriptor.Contains("FontBBox") ? Rectangle(graph.File, descriptor["FontBBox"]) : new PdfRect(-500, -500, 2500, 2000);
        if (bounds.IsEmpty) throw new InvalidDataException("Empty font bounding box.");
        if (composite)
        {
            var values = graph.OptionalArray(font["W"]); var widths = new Dictionary<int, double>();
            for (var i = 0; i < values.Count;)
            {
                var first = graph.File.Integer(values[i++]); if (i >= values.Count) throw new InvalidDataException("Truncated CID width array.");
                if (graph.Resolve(values[i++]) is PdfArray array)
                {
                    for (var j = 0; j < array.Count; j++)
                    { if (first + j > 65535 || graph.Resolve(array[j]) is not PdfNumber n) throw new InvalidDataException("Invalid CID width."); widths[first + j] = n.Value; }
                }
                else
                {
                    var last = graph.File.Integer(values[i - 1]);
                    if (i >= values.Count || graph.Resolve(values[i++]) is not PdfNumber n || first < 0 || last < first || last > 65535) throw new InvalidDataException("Invalid CID width range.");
                    for (var j = first; j <= last; j++) widths[j] = n.Value;
                }
            }
            var defaultWidth = font.Number("DW", 1000); return new RedactionFont(true, bounds, code => widths.GetValueOrDefault(code, defaultWidth));
        }
        if (graph.Resolve(font["Widths"]) is PdfArray simpleWidths)
        {
            var first = (int)font.Number("FirstChar");
            var values = simpleWidths.Select(value => graph.Resolve(value) is PdfNumber n ? n.Value : throw new InvalidDataException("Invalid simple font width.")).ToArray();
            return new RedactionFont(false, bounds, code => code >= first && code - first < values.Length ? values[code - first] : throw new NotSupportedException("Missing simple-font glyph width."));
        }
        var name = font.Name("BaseFont") ?? "";
        if (graph.Resolve(font["Encoding"]) is PdfDictionary) throw new NotSupportedException("Custom encodings without explicit widths are not qualified for redaction.");
        if (name.StartsWith("Courier", StringComparison.Ordinal)) return new RedactionFont(false, bounds, _ => 600);
        int[] metrics = name switch
        {
            "Helvetica" or "Helvetica-Oblique" => HelveticaWidths,
            "Helvetica-Bold" or "Helvetica-BoldOblique" => HelveticaBoldWidths,
            _ => throw new NotSupportedException("This font needs explicit glyph widths for native redaction.")
        };
        return new RedactionFont(false, bounds, code => code is >= 32 and <= 126 ? metrics[code - 32] : throw new NotSupportedException("Non-ASCII Standard 14 metrics need qualification or an explicitly embedded font."));
    }
    private static readonly int[] HelveticaWidths =
    [278,278,355,556,556,889,667,191,333,333,389,584,278,333,278,278,556,556,556,556,556,556,556,556,556,556,278,278,584,584,584,556,1015,
     667,667,722,722,667,611,778,722,278,500,667,556,833,722,778,667,778,722,667,611,722,667,944,667,667,611,278,278,278,469,556,333,
     556,556,500,556,556,278,556,556,222,222,500,222,833,556,556,556,556,333,500,278,556,500,722,500,500,500,334,260,334,584];
    private static readonly int[] HelveticaBoldWidths =
    [278,333,474,556,556,889,722,238,333,333,389,584,278,333,278,278,556,556,556,556,556,556,556,556,556,556,333,333,584,584,584,611,975,
     722,722,722,722,667,611,778,722,278,556,722,611,833,722,778,667,778,722,667,611,722,667,944,667,667,611,333,278,333,584,556,333,
     556,611,556,611,556,333,611,611,278,278,556,278,889,611,611,611,611,389,556,333,611,556,778,556,556,500,389,280,389,584];
}
