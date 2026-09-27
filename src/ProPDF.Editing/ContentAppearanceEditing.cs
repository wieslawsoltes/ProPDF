using System.Text;
using ProPDF.Core;
using ProPDF.Kernel;
using static ProPDF.Kernel.PdfValues;
using static ProPDF.Editing.PdfGraph;

namespace ProPDF.Editing;

public sealed partial class ManagedPdfEditor
{
    private static bool IsPathPaint(string op) => op is "S" or "s" or "f" or "F" or "f*" or "B" or "B*" or "b" or "b*";
    private static bool IsTextShow(string op) => op is "Tj" or "TJ" or "'" or "\"";

    private static IReadOnlyList<PdfContentInstruction> StyleContent(PdfGraph graph, NativePage page,
        ContentNode node, IReadOnlyList<PdfContentInstruction> original, PdfContentAppearance appearance, CancellationToken token)
    {
        ArgumentNullException.ThrowIfNull(appearance);
        appearance.Validate();
        if (node.Kind is not (PdfContentObjectKind.Text or PdfContentObjectKind.Path or PdfContentObjectKind.Image))
            throw new NotSupportedException("Appearance editing supports text, paths and image invocations. Form groups and shadings need separate appearance semantics.");
        if (node.Kind != PdfContentObjectKind.Path && appearance.PathPaint != PdfPathPaintMode.Preserve)
            throw new ArgumentException("Path painting mode can only be changed for a path.");
        if (node.Kind == PdfContentObjectKind.Image && (appearance.FillColor is not null || appearance.StrokeColor is not null ||
            appearance.StrokeWidth is not null || appearance.LineCap is not null || appearance.LineJoin is not null || appearance.Dash is not null))
            throw new ArgumentException("Images support opacity and blend mode, not path or text colors. Replace the image to change its pixels.");

        var commands = new StringBuilder();
        if (appearance.FillColor is { } fill) commands.Append(Rgb(fill));
        if (appearance.StrokeColor is { } stroke) commands.Append(Rgb(stroke, true));
        if (appearance.StrokeWidth is { } width) commands.Append(F(width)).Append(" w\n");
        if (appearance.LineCap is { } cap) commands.Append((int)cap).Append(" J\n");
        if (appearance.LineJoin is { } join) commands.Append((int)join).Append(" j\n");
        if (appearance.Dash is { } dash)
            commands.Append('[').AppendJoin(' ', dash.Lengths.Select(F)).Append("] ").Append(F(dash.Phase)).Append(" d\n");
        var gs = new PdfDictionary();
        if (appearance.Opacity is not null || appearance.FillColor is not null)
            gs["ca"] = new PdfNumber((appearance.Opacity ?? 1) * (appearance.FillColor?.A ?? 255) / 255d);
        if (appearance.Opacity is not null || appearance.StrokeColor is not null)
            gs["CA"] = new PdfNumber((appearance.Opacity ?? 1) * (appearance.StrokeColor?.A ?? 255) / 255d);
        if (gs.Count != 0) gs["AIS"] = new PdfBoolean(false);
        if (appearance.BlendMode is { } blend) gs["BM"] = new PdfName(blend.ToString());
        if (gs.Count != 0)
            commands.Append('/').Append(graph.Resource(page, "ExtGState", gs)).Append(" gs\n");
        var overrides = PdfContent.Read(Encoding.ASCII.GetBytes(commands.ToString()), cancellationToken: token);
        var result = new List<PdfContentInstruction> { new("q", []) };
        foreach (var instruction in original)
        {
            token.ThrowIfCancellationRequested();
            var isPaint = IsPathPaint(instruction.Operator);
            // Override at paint/show time, not before BT/path construction: original objects may change color/gs inside.
            if (isPaint || IsTextShow(instruction.Operator) || instruction.Operator == "Do") result.AddRange(overrides);
            if (isPaint && appearance.PathPaint != PdfPathPaintMode.Preserve)
            {
                // Keep explicit close-path semantics and the original even-odd versus nonzero fill rule.
                if (instruction.Operator is "s" or "b" or "b*") result.Add(new("h", []));
                var evenOdd = instruction.Operator.EndsWith('*');
                var op = appearance.PathPaint switch
                {
                    PdfPathPaintMode.Fill => evenOdd ? "f*" : "f",
                    PdfPathPaintMode.Stroke => "S",
                    _ => evenOdd ? "B*" : "B"
                };
                result.Add(new(op, []));
            }
            else result.Add(instruction);
        }
        result.Add(new("Q", []));
        // q/Q isolates the override; replay only the original object's persistent state for following objects.
        result.AddRange(node.StateEffects);
        return result;
    }

    private static PdfStream ContentImage(PdfGraph graph, NativePage page, ContentNode node)
    {
        if (node.Kind != PdfContentObjectKind.Image || node.Resource is null)
            throw new ArgumentException("Select an image invocation.");
        return graph.Resolve(graph.OptionalDictionary(graph.OptionalDictionary(page.Dictionary["Resources"])["XObject"])[node.Resource]) as PdfStream
            ?? throw new InvalidDataException("Missing image resource.");
    }

    private static PdfContentInstruction ReplaceImageInvocation(PdfGraph graph, NativePage page,
        ContentNode node, PdfBinaryAsset? image, bool? interpolate, CancellationToken token)
    {
        var old = ContentImage(graph, page, node);
        var useInterpolation = interpolate ?? graph.Resolve(old.Dictionary["Interpolate"]) is PdfBoolean { Value: true };
        PdfObject resource;
        if (image is not null) resource = CreateImageResource(graph, image, useInterpolation, token);
        else
        {
            var dictionary = old.Dictionary.Copy();
            dictionary["Interpolate"] = new PdfBoolean(useInterpolation);
            resource = new PdfStream(dictionary, old.EncodedBytes);
        }
        var name = graph.Resource(page, "XObject", resource);
        return new("Do", [new PdfName(name)]);
    }

    private static PdfContentImageInfo ImageInfo(PdfGraph graph, PdfStream stream)
    {
        var dictionary = stream.Dictionary;
        int Integer(string key, int fallback = 0)
        {
            if (graph.Resolve(dictionary[key]) is not PdfNumber n) return fallback;
            if (!double.IsFinite(n.Value) || n.Value < 0 || n.Value > int.MaxValue || n.Value != Math.Truncate(n.Value))
                throw new InvalidDataException("Invalid image " + key + ".");
            return (int)n.Value;
        }
        var color = graph.Resolve(dictionary["ColorSpace"]);
        var colorName = color is PdfName name ? name.Value : color is PdfArray { Count: > 0 } array && graph.Resolve(array[0]) is PdfName family ? family.Value : "Unspecified";
        return new(Integer("Width"), Integer("Height"), Integer("BitsPerComponent", graph.Resolve(dictionary["ImageMask"]) is PdfBoolean { Value: true } ? 1 : 0),
            colorName, graph.Resolve(dictionary["Interpolate"]) is PdfBoolean { Value: true }, dictionary.Contains("SMask") && dictionary.Name("SMask") != "None");
    }
}
