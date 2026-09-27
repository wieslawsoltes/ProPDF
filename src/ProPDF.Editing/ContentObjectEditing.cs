using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using ProPDF.Core;
using ProPDF.Kernel;
using static ProPDF.Editing.PdfGraph;
using static ProPDF.Kernel.PdfValues;

namespace ProPDF.Editing;

public sealed partial class ManagedPdfEditor : IPdfContentService
{
    private sealed record ContentNode(int First, int Last, PdfContentObjectKind Kind, PdfRect Bounds,
        PdfAffineTransform StartMatrix, IReadOnlyList<PdfContentInstruction> StateEffects, string? Text, string? Resource, string? Reason, PdfContentImageInfo? ImageInfo = null);
    private sealed record ContentAnalysis(string Fingerprint, IReadOnlyList<PdfContentInstruction> Instructions, IReadOnlyList<ContentNode> Nodes);
    private sealed class ObjectGraphicsState
    {
        public PdfAffineTransform Matrix = PdfAffineTransform.Identity;
        public double LineWidth = 1, MiterLimit = 10, FontSize, CharacterSpace, WordSpace, HorizontalScale = 1, Leading, Rise;
        public RedactionFont? Font;
        public Func<int, string>? Decode;
        public string? FontReason, Reason;
        public int TextMode;
        public ObjectGraphicsState Copy() => (ObjectGraphicsState)MemberwiseClone();
    }
    private sealed class ObjectGroup(int first, PdfAffineTransform matrix, string? reason)
    {
        public int First { get; } = first;
        public PdfAffineTransform Matrix { get; } = matrix;
        public PdfRect? Bounds;
        public List<PdfContentInstruction> Effects { get; } = [];
        public StringBuilder Text { get; } = new();
        public bool TextKnown = true;
        public string? Reason = reason;
        public void Include(PdfRect bounds)
        {
            if (Bounds is not { } old) Bounds = bounds;
            else Bounds = new(Math.Min(old.X, bounds.X), Math.Min(old.Y, bounds.Y), Math.Max(old.Right, bounds.Right) - Math.Min(old.X, bounds.X), Math.Max(old.Bottom, bounds.Bottom) - Math.Min(old.Y, bounds.Y));
        }
    }
    private static readonly HashSet<string> StateOperators = new(StringComparer.Ordinal)
    { "w", "J", "j", "M", "d", "ri", "i", "gs", "CS", "cs", "SC", "sc", "SCN", "scn", "G", "g", "RG", "rg", "K", "k", "Tf", "Tc", "Tw", "Tz", "TL", "Tr", "Ts", "cm" };

    public Task<PdfPageContent> ReadPageContentAsync(PdfSnapshot source, int pageNumber, PdfContentInspectionOptions? options = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(source); source.GetPage(pageNumber);
        return Task.Run(() =>
        {
            var graph = Open(source, cancellationToken); var analysis = AnalyzeContent(graph, pageNumber, options, cancellationToken);
            var authorization = graph.File.IsOwnerAuthorized ? null : "Owner authorization is required for content editing.";
            if (HasSignatures(graph)) authorization = "Signed documents cannot be rewritten.";
            return new PdfPageContent(source.Id, pageNumber, Array.AsReadOnly(analysis.Nodes.Select((node, index) =>
                new PdfContentObject(new(source.Id, pageNumber, analysis.Fingerprint, index), node.Kind, node.Bounds, node.Text, node.Resource, authorization ?? node.Reason, node.ImageInfo)).ToArray()));
        }, cancellationToken);
    }
    private static PdfAffineTransform ViewToPdf(NativePage page)
    {
        var t = page.Transform; var p = t.ToPdf(new PdfPoint(0, 0)); var x = t.ToPdf(new PdfPoint(1, 0)); var y = t.ToPdf(new PdfPoint(0, 1));
        return new(x.X - p.X, x.Y - p.Y, y.X - p.X, y.Y - p.Y, p.X, p.Y);
    }
    private static ContentAnalysis AnalyzeContent(PdfGraph graph, int pageNumber, PdfContentInspectionOptions? options, CancellationToken token)
    {
        options ??= new();
        if (options.MaximumObjects is < 1 or > 100_000 || options.MaximumInstructions is < 1 or > 1_000_000) throw new ArgumentOutOfRangeException(nameof(options));
        var page = graph.Page(pageNumber); var bytes = graph.Content(page);
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256); hash.AppendData(bytes); hash.AppendData(PdfObjectWriter.Serialize(page.Dictionary));
        var fingerprint = Convert.ToHexString(hash.GetHashAndReset());
        var input = PdfContent.Read(bytes, new(MaximumInstructions: options.MaximumInstructions), token);
        var nodes = new List<ContentNode>(); var state = new ObjectGraphicsState(); var stack = new Stack<ObjectGraphicsState>();
        var resources = graph.OptionalDictionary(page.Dictionary["Resources"]); var fontResources = graph.OptionalDictionary(resources["Font"]);
        var fonts = new Dictionary<string, (RedactionFont? Font, Func<int, string>? Decode, string? Reason)>(StringComparer.Ordinal);
        ObjectGroup? text = null, path = null; var textMatrix = PdfAffineTransform.Identity; var lineMatrix = textMatrix;
        var viewToPdf = ViewToPdf(page); var pdfToView = viewToPdf.Inverse();
        var rawPage = page.Transform.ToPdf(new PdfRect(0, 0, page.Transform.ViewSize.Width, page.Transform.ViewSize.Height));
        var tagged = graph.File.Catalog.Contains("StructTreeRoot") || page.Dictionary.Contains("StructParents"); var marked = 0; var compatible = 0;
        string? Reason() => tagged ? "Tagged content requires structure-tree editing." : marked > 0 ? "Marked or optional content is read-only in this object editor." : state.Reason;
        void Add(ObjectGroup group, int last, PdfContentObjectKind kind, string? resource = null, PdfContentImageInfo? imageInfo = null)
        {
            if (group.Bounds is not { } bounds) return;
            if (nodes.Count >= options.MaximumObjects) throw new InvalidDataException("Content-object budget exceeded.");
            string? reason = group.Reason;
            try { group.Matrix.Validate(); } catch (ArgumentOutOfRangeException) { reason ??= "Singular or excessive graphics transform."; }
            nodes.Add(new(group.First, last, kind, pdfToView.Map(bounds), group.Matrix, group.Effects.AsReadOnly(),
                kind == PdfContentObjectKind.Text && group.TextKnown ? group.Text.ToString() : null, resource, reason, imageInfo));
        }
        void TextRequired() { if (text is null) throw new InvalidDataException("Text position/show outside BT/ET."); }
        void NewLine()
        {
            lineMatrix = lineMatrix.Concat(PdfAffineTransform.Translation(0, -state.Leading)); textMatrix = lineMatrix;
            if (text!.Text.Length > 0) text.Text.Append('\n');
        }
        void Show(PdfObject value)
        {
            TextRequired();
            var data = (value as PdfString)?.ToArray() ?? throw new InvalidDataException("Expected text string.");
            if (state.Font is not { } font || state.FontSize == 0)
            {
                text!.Reason ??= state.FontReason ?? "Missing text font or size."; text.Include(rawPage); text.TextKnown = false; return;
            }
            if (font.Composite && data.Length % 2 != 0) throw new InvalidDataException("Odd Identity-H character-code length.");
            // Logical glyph bounds are for selection, not secure redaction or exact ink outlines.
            var descent = Math.Max(-500, font.Bounds.Y); var ascent = Math.Min(1200, font.Bounds.Bottom);
            if (ascent <= descent) { descent = -250; ascent = 1000; }
            for (var offset = 0; offset < data.Length; offset++)
            {
                if (offset % 256 == 0) token.ThrowIfCancellationRequested();
                var code = font.Composite ? (data[offset++] << 8) | data[offset] : data[offset];
                double width;
                try { width = font.Width(code); }
                catch (NotSupportedException error) { text!.Reason ??= error.Message; text.Include(rawPage); text.TextKnown = false; return; }
                var matrix = state.Matrix.Concat(textMatrix).Concat(new PdfAffineTransform(state.FontSize * state.HorizontalScale / 1000, 0, 0, state.FontSize / 1000, 0, state.Rise));
                text!.Include(matrix.Map(new PdfRect(Math.Min(0, width), descent, Math.Max(1, Math.Abs(width)), ascent - descent)));
                if (state.Decode is null) text.TextKnown = false;
                else { var decoded = state.Decode(code); if (decoded == "\uFFFD") text.TextKnown = false; text.Text.Append(decoded); }
                if (text.Text.Length > 1_000_000) throw new InvalidDataException("Text inspection budget exceeded.");
                var advance = (width * state.FontSize / 1000 + state.CharacterSpace + (code == 32 && !font.Composite ? state.WordSpace : 0)) * state.HorizontalScale;
                textMatrix = textMatrix.Concat(PdfAffineTransform.Translation(advance, 0));
            }
        }
        for (var index = 0; index < input.Count; index++)
        {
            token.ThrowIfCancellationRequested(); var instruction = input[index]; var p = instruction.Operands;
            void Count(int n) { if (p.Count != n) throw new InvalidDataException($"Invalid operand count for {instruction.Operator}."); }
            double N(int i) => p[i] is PdfNumber n && double.IsFinite(n.Value) && Math.Abs(n.Value) <= 1e9 ? n.Value : throw new InvalidDataException("Invalid or excessive content number.");
            string Name(int i) => p[i] is PdfName n ? n.Value : throw new InvalidDataException("Expected name operand.");
            void Point(double x, double y)
            {
                if (text is not null) throw new NotSupportedException("Path construction inside a text object is not editable.");
                path ??= new(index, state.Matrix, Reason()); var point = state.Matrix.Map(new PdfPoint(x, y));
                path.Include(new PdfRect(point.X, point.Y, 0, 0));
            }
            if (StateOperators.Contains(instruction.Operator)) { text?.Effects.Add(instruction); path?.Effects.Add(instruction); }
            switch (instruction.Operator)
            {
                case "q":
                    Count(0); if (text is not null || path is not null) throw new NotSupportedException("Nested graphics state inside a paint object is not editable.");
                    if (stack.Count >= 128) throw new InvalidDataException("Graphics-state budget exceeded."); stack.Push(state.Copy()); break;
                case "Q":
                    Count(0); if (text is not null || path is not null || !stack.TryPop(out var previous)) throw new InvalidDataException("Unbalanced graphics state."); state = previous; break;
                case "cm":
                    Count(6); if (text is not null) text.Reason ??= "Matrix changes inside BT/ET are not editable.";
                    state.Matrix = state.Matrix.Concat(new(N(0), N(1), N(2), N(3), N(4), N(5))); break;
                case "w": Count(1); state.LineWidth = Math.Abs(N(0)); break;
                case "M": Count(1); state.MiterLimit = Math.Max(1, Math.Abs(N(0))); break;
                case "gs":
                    Count(1); var gs = graph.Dictionary(graph.OptionalDictionary(resources["ExtGState"])[Name(0)]);
                    if (gs.Contains("Font") || gs.Contains("SMask") && gs.Name("SMask") != "None") state.Reason = "ExtGState font or soft-mask editing requires additional support.";
                    if (gs.Contains("LW")) state.LineWidth = Math.Abs(gs.Number("LW")); if (gs.Contains("ML")) state.MiterLimit = Math.Max(1, Math.Abs(gs.Number("ML")));
                    if (text is not null) text.Reason ??= state.Reason; if (path is not null) path.Reason ??= state.Reason; break;
                case "m": case "l": Count(2); Point(N(0), N(1)); break;
                case "c": Count(6); for (var i = 0; i < 6; i += 2) Point(N(i), N(i + 1)); break;
                case "v": case "y": Count(4); for (var i = 0; i < 4; i += 2) Point(N(i), N(i + 1)); break;
                case "re": Count(4); Point(N(0), N(1)); Point(N(0) + N(2), N(1)); Point(N(0) + N(2), N(1) + N(3)); Point(N(0), N(1) + N(3)); break;
                case "h": Count(0); if (path is null) throw new InvalidDataException("Close path without a path."); break;
                case "W": case "W*": Count(0); if (path is null) throw new InvalidDataException("Clip without a path."); path.Reason = "A path that also changes the clipping region cannot be edited independently."; break;
                case "S": case "s": case "f": case "F": case "f*": case "B": case "B*": case "b": case "b*": case "n":
                    Count(0); if (text is not null) throw new InvalidDataException("Path paint inside BT/ET.");
                    if (path is not null && instruction.Operator != "n")
                    {
                        if (instruction.Operator is "S" or "s" or "B" or "B*" or "b" or "b*" && path.Bounds is { } bounds)
                        {
                            var m = state.Matrix; var margin = state.LineWidth * Math.Sqrt(m.A * m.A + m.B * m.B + m.C * m.C + m.D * m.D) / 2;
                            path.Bounds = new(bounds.X - margin, bounds.Y - margin, bounds.Width + margin * 2, bounds.Height + margin * 2);
                        }
                        Add(path, index, PdfContentObjectKind.Path);
                    }
                    path = null; break;
                case "BT": Count(0); if (text is not null || path is not null) throw new InvalidDataException("Overlapping text/path object."); text = new(index, state.Matrix, Reason()); textMatrix = lineMatrix = PdfAffineTransform.Identity; break;
                case "ET": Count(0); TextRequired(); Add(text!, index, PdfContentObjectKind.Text); text = null; break;
                case "Tf":
                    Count(2); state.FontSize = N(1); var fontName = Name(0);
                    if (!fonts.TryGetValue(fontName, out var font))
                    {
                        try { font = (ReadRedactionFont(graph, fontResources[fontName]), ReadContentDecoder(graph, fontResources[fontName]), null); }
                        catch (NotSupportedException error) { font = (null, null, error.Message); }
                        fonts[fontName] = font;
                    }
                    state.Font = font.Font; state.Decode = font.Decode; state.FontReason = font.Reason; break;
                case "Tc": Count(1); state.CharacterSpace = N(0); break;
                case "Tw": Count(1); state.WordSpace = N(0); break;
                case "Tz": Count(1); state.HorizontalScale = N(0) / 100; break;
                case "TL": Count(1); state.Leading = N(0); break;
                case "Ts": Count(1); state.Rise = N(0); break;
                case "Tr":
                    Count(1); if (N(0) is < 0 or > 7 || N(0) != Math.Truncate(N(0))) throw new InvalidDataException("Invalid text rendering mode.");
                    state.TextMode = (int)N(0); if (text is not null && state.TextMode >= 4) text.Reason ??= "Text used as a clipping path is read-only."; break;
                case "Tm": Count(6); TextRequired(); textMatrix = lineMatrix = new(N(0), N(1), N(2), N(3), N(4), N(5)); break;
                case "Td": case "TD":
                    Count(2); TextRequired(); if (instruction.Operator == "TD") { state.Leading = -N(1); text!.Effects.Add(new("TL", [new PdfNumber(state.Leading)])); }
                    lineMatrix = lineMatrix.Concat(PdfAffineTransform.Translation(N(0), N(1))); textMatrix = lineMatrix;
                    if (text!.Text.Length > 0 && N(1) != 0) text.Text.Append('\n'); break;
                case "T*": Count(0); TextRequired(); NewLine(); break;
                case "Tj": Count(1); Show(p[0]); break;
                case "TJ":
                    Count(1); TextRequired(); if (p[0] is not PdfArray array) throw new InvalidDataException("TJ expects an array.");
                    foreach (var value in array)
                        if (value is PdfNumber n) textMatrix = textMatrix.Concat(PdfAffineTransform.Translation(-n.Value / 1000 * state.FontSize * state.HorizontalScale, 0)); else Show(value);
                    break;
                case "'": Count(1); TextRequired(); NewLine(); Show(p[0]); break;
                case "\"":
                    Count(3); TextRequired(); state.WordSpace = N(0); state.CharacterSpace = N(1);
                    text!.Effects.Add(new("Tw", [p[0]])); text.Effects.Add(new("Tc", [p[1]])); NewLine(); Show(p[2]); break;
                case "Do":
                    Count(1); if (text is not null || path is not null) throw new NotSupportedException("XObject inside an unfinished paint object.");
                    var name = Name(0); var xobject = graph.Resolve(graph.OptionalDictionary(resources["XObject"])[name]) as PdfStream ?? throw new InvalidDataException("Missing XObject.");
                    var subtype = xobject.Dictionary.Name("Subtype"); var kind = subtype switch { "Image" => PdfContentObjectKind.Image, "Form" => PdfContentObjectKind.Form, _ => throw new NotSupportedException("Unknown XObject subtype.") };
                    var objectMatrix = state.Matrix; var objectBounds = new PdfRect(0, 0, 1, 1);
                    if (kind == PdfContentObjectKind.Form)
                    {
                        objectBounds = Rectangle(graph.File, xobject.Dictionary["BBox"]);
                        if (graph.Resolve(xobject.Dictionary["Matrix"]) is PdfArray matrix)
                        {
                            if (matrix.Count != 6 || matrix.Any(v => v is not PdfNumber)) throw new InvalidDataException("Invalid Form matrix.");
                            var v = matrix.Cast<PdfNumber>().Select(n => n.Value).ToArray(); objectMatrix = objectMatrix.Concat(new(v[0], v[1], v[2], v[3], v[4], v[5]));
                        }
                    }
                    var invocation = new ObjectGroup(index, state.Matrix, Reason());
                    if (xobject.Dictionary.Contains("OC") || xobject.Dictionary.Contains("StructParent"))
                        invocation.Reason ??= "Optional-content or structured XObjects require semantic editing support.";
                    invocation.Include(objectMatrix.Map(objectBounds));
                    Add(invocation, index, kind, name, kind == PdfContentObjectKind.Image ? ImageInfo(graph, xobject) : null); break;
                case "sh":
                    Count(1); if (text is not null || path is not null) throw new InvalidDataException("Shading inside an unfinished object.");
                    var shading = new ObjectGroup(index, state.Matrix, "Shading geometry is inspect-only."); shading.Include(rawPage); Add(shading, index, PdfContentObjectKind.Shading, Name(0)); break;
                case "BMC": case "BDC":
                    Count(instruction.Operator == "BMC" ? 1 : 2); if (++marked > 128) throw new InvalidDataException("Marked-content budget exceeded.");
                    if (text is not null) text.Reason = "Marked content is read-only."; if (path is not null) path.Reason = "Marked content is read-only."; break;
                case "EMC": Count(0); if (--marked < 0) throw new InvalidDataException("Unbalanced marked content."); break;
                case "MP": case "DP": break;
                case "BX": Count(0); if (++compatible > 128) throw new InvalidDataException("Compatibility nesting exceeded."); break;
                case "EX": Count(0); if (--compatible < 0) throw new InvalidDataException("Unbalanced compatibility section."); break;
                case "CS": case "cs":
                    Count(1); if (Name(0) == "Pattern") state.Reason = "Pattern-coordinate editing requires additional support.";
                    if (text is not null) text.Reason ??= state.Reason; if (path is not null) path.Reason ??= state.Reason; break;
                case "J": case "j": case "d": case "ri": case "i": case "SC": case "sc": case "SCN": case "scn": case "G": case "g": case "RG": case "rg": case "K": case "k": break;
                default: throw new NotSupportedException($"Content operator '{instruction.Operator}' is not supported by the object editor; the viewer can still render this document.");
            }
            if (text is not null && state.TextMode >= 4) text.Reason ??= "Text used as a clipping path is read-only.";
        }
        if (text is not null || path is not null || stack.Count != 0 || marked != 0 || compatible != 0) throw new InvalidDataException("Unbalanced page content.");
        return new(fingerprint, input, nodes.AsReadOnly());
    }

    private static Func<int, string>? ReadContentDecoder(PdfGraph graph, PdfObject raw)
    {
        var font = graph.Dictionary(raw);
        if (graph.Resolve(font["ToUnicode"]) is PdfStream stream)
        {
            var data = Encoding.ASCII.GetString(graph.File.Decode(stream));
            var map = new Dictionary<int, string>();
            // Restrict extraction to explicit bfchar mappings. Unsupported encodings remain editable as whole objects,
            // but are not offered as guessed replacement text. Never interpret CMap procedures as executable code.
            foreach (Match block in Regex.Matches(data, @"beginbfchar([\s\S]*?)endbfchar", RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(100)))
                foreach (Match pair in Regex.Matches(block.Groups[1].Value, @"<([0-9A-Fa-f]{2,4})>\s*<([0-9A-Fa-f]+)>", RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(100)))
                {
                    var key = Convert.ToInt32(pair.Groups[1].Value, 16); var hex = pair.Groups[2].Value;
                    if (hex.Length % 4 != 0 || hex.Length > 1024 || map.Count >= 65536) return null;
                    map[key] = Encoding.BigEndianUnicode.GetString(Convert.FromHexString(hex));
                }
            if (data.Contains("beginbfrange", StringComparison.Ordinal)) return null;
            return map.Count == 0 ? null : code => map.GetValueOrDefault(code, "\uFFFD");
        }
        var encoding = font.Name("Encoding");
        if (font.Name("Subtype") == "Type0" || font.Contains("Encoding") && encoding is not ("WinAnsiEncoding" or "StandardEncoding")) return null;
        const string special = "€\uFFFD‚ƒ„…†‡ˆ‰Š‹Œ\uFFFDŽ\uFFFD\uFFFD‘’“”•–—˜™š›œ\uFFFDžŸ";
        return code => code is >= 32 and <= 126 ? ((char)code).ToString() : encoding == "WinAnsiEncoding" && code is >= 160 and <= 255 ? ((char)code).ToString() :
            encoding == "WinAnsiEncoding" && code is >= 128 and <= 159 ? special[code - 128].ToString() : "\uFFFD";
    }

    private static string MatrixCommand(PdfAffineTransform m) => $"{F(m.A)} {F(m.B)} {F(m.C)} {F(m.D)} {F(m.E)} {F(m.F)} cm\n";
    private static void EditContentObject(PdfGraph graph, PdfContentObjectEdit edit, CancellationToken token)
    {
        var edits = edit is EditContentObjects selection ? selection.Edits.ToArray() : new[] { edit };
        var handle = edit.Object ?? throw new ArgumentNullException(nameof(edit.Object));
        if (edit is EditContentObjects && handle != edits[0].Object) throw new PdfRevisionConflictException();
        var page = graph.Page(handle.PageNumber);
        var analysis = AnalyzeContent(graph, handle.PageNumber, null, token);
        // Resolve every handle against the original inspection, before allocating replacements.
        var targets = edits.Select(item =>
        {
            var reference = item.Object ?? throw new ArgumentNullException(nameof(item.Object));
            if (reference.Revision != handle.Revision || reference.PageNumber != handle.PageNumber ||
                analysis.Fingerprint != reference.Fingerprint || reference.Index < 0 || reference.Index >= analysis.Nodes.Count)
                throw new PdfRevisionConflictException();
            var target = analysis.Nodes[reference.Index];
            if (target.Reason is not null) throw new NotSupportedException(target.Reason);
            return (Edit: item, Node: target);
        }).OrderBy(item => item.Node.First).ToArray();
        var rewritten = new List<PdfContentInstruction>(); var cursor = 0;
        foreach (var target in targets)
        {
            token.ThrowIfCancellationRequested();
            if (target.Node.First < cursor) throw new ArgumentException("Selected content ranges overlap.");
            while (cursor < target.Node.First) rewritten.Add(analysis.Instructions[cursor++]);
            rewritten.AddRange(BuildContentReplacement(graph, page, analysis, target.Node, target.Edit, token));
            cursor = target.Node.Last + 1;
        }
        while (cursor < analysis.Instructions.Count) rewritten.Add(analysis.Instructions[cursor++]);
        page.Dictionary["Contents"] = graph.File.Add(PdfStream.FromDecoded(PdfContent.Write(rewritten, cancellationToken: token)));
    }
    private static IReadOnlyList<PdfContentInstruction> BuildContentReplacement(PdfGraph graph, NativePage page,
        ContentAnalysis analysis, ContentNode node, PdfContentObjectEdit edit, CancellationToken token)
    {
        var handle = edit.Object;
        var original = analysis.Instructions.Skip(node.First).Take(node.Last - node.First + 1).ToArray();
        var replacement = new List<PdfContentInstruction>();
        void Commands(string commands) => replacement.AddRange(PdfContent.Read(Encoding.ASCII.GetBytes(commands), cancellationToken: token));
        void Transform(PdfAffineTransform mapping)
        {
            mapping.Validate(); var view = ViewToPdf(page);
            var local = node.StartMatrix.Inverse().Concat(view).Concat(mapping).Concat(view.Inverse()).Concat(node.StartMatrix);
            local.Validate(); Commands("q\n" + MatrixCommand(local)); replacement.AddRange(original); Commands("Q\n");
        }
        switch (edit)
        {
            case SetContentAppearance style:
                replacement.AddRange(StyleContent(graph, page, node, original, style.Appearance, token)); break;
            case ReplaceContentImage image:
                ArgumentNullException.ThrowIfNull(image.Image);
                replacement.Add(ReplaceImageInvocation(graph, page, node, image.Image, image.Interpolate, token)); break;
            case SetContentImageInterpolation interpolation:
                replacement.Add(ReplaceImageInvocation(graph, page, node, null, interpolation.Interpolate, token)); break;
            case TransformContentObject transform: Transform(transform.Transform); replacement.AddRange(node.StateEffects); break;
            case DuplicateContentObject duplicate: Transform(duplicate.Transform); replacement.AddRange(original); break;
            case DeleteContentObject: replacement.AddRange(node.StateEffects); break;
            case ClipContentObject clip:
                page.Validate(clip.Bounds); var toLocal = node.StartMatrix.Inverse().Concat(ViewToPdf(page));
                var corners = new[] { new PdfPoint(clip.Bounds.X, clip.Bounds.Y), new PdfPoint(clip.Bounds.Right, clip.Bounds.Y), new PdfPoint(clip.Bounds.Right, clip.Bounds.Bottom), new PdfPoint(clip.Bounds.X, clip.Bounds.Bottom) }.Select(toLocal.Map).ToArray();
                Commands($"q\n{F(corners[0].X)} {F(corners[0].Y)} m\n" + string.Concat(corners.Skip(1).Select(p => $"{F(p.X)} {F(p.Y)} l\n")) + "h W n\n");
                replacement.AddRange(original); Commands("Q\n"); replacement.AddRange(node.StateEffects); break;
            case ReplaceContentText text:
                if (node.Kind != PdfContentObjectKind.Text) throw new ArgumentException("Only a text object can be replaced as text.");
                // Author at the original paint position, under the original clipping stack. Isolate authored state.
                var authored = CreateTextBox(graph, new AddTextBox(handle.PageNumber, text.Bounds, text.Text, text.FontSize, Alignment: text.Alignment,
                    StandardFont: text.StandardFont, Color: text.Color, EmbeddedFont: text.EmbeddedFont), token);
                Commands("q\n" + MatrixCommand(node.StartMatrix.Inverse()) + authored + "Q\n"); replacement.AddRange(node.StateEffects); break;
            default: throw new NotSupportedException("Unknown content-object operation.");
        }
        return replacement;
    }
}
