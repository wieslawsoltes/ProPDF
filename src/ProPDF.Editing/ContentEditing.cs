using System.Text;
using ProPDF.Core;
using ProPDF.Kernel;
using static ProPDF.Editing.PdfGraph;
using static ProPDF.Kernel.PdfValues;

namespace ProPDF.Editing;

public sealed partial class ManagedPdfEditor
{
    private static string Rgb(PdfColor color, bool stroke = false) => $"{F(color.R / 255d)} {F(color.G / 255d)} {F(color.B / 255d)} {(stroke ? "RG" : "rg")}\n";
    private static void InsertText(PdfGraph graph, AddText text)
    {
        ArgumentNullException.ThrowIfNull(text.Text);
        if (text.Text.Length > 1_000_000 || !double.IsFinite(text.FontSize) || text.FontSize is < 1 or > 1000 ||
            !double.IsFinite(text.Baseline.X) || !double.IsFinite(text.Baseline.Y)) throw new ArgumentOutOfRangeException(nameof(text));
        var page = graph.Page(text.PageNumber); var size = page.Transform.ViewSize;
        if (text.Baseline.X < 0 || text.Baseline.Y < 0 || text.Baseline.X > size.Width || text.Baseline.Y > size.Height) throw new ArgumentOutOfRangeException(nameof(text.Baseline));
        var font = PdfFonts.Create(graph, text.StandardFont, text.EmbeddedFont, text.Text); var name = graph.Resource(page, "Font", font.Reference);
        var color = text.Color ?? PdfColor.Black;
        var content = new StringBuilder("q\n").Append(page.ViewMatrix()).Append(Rgb(color));
        if (color.A != 255)
        {
            var state = graph.Resource(page, "ExtGState", Dictionary(("Type", new PdfName("ExtGState")), ("ca", new PdfNumber(color.A / 255d))));
            content.Append('/').Append(state).Append(" gs\n");
        }
        var lines = text.Text.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n').Split('\n');
        for (var i = 0; i < lines.Length; i++)
            content.Append("BT\n/").Append(name).Append(' ').Append(F(text.FontSize)).Append(" Tf\n1 0 0 -1 ").Append(F(text.Baseline.X)).Append(' ')
                .Append(F(text.Baseline.Y + i * text.FontSize * 1.2)).Append(" Tm\n").Append(Hex(font.Encode(lines[i]))).Append(" Tj\nET\n");
        content.Append("Q\n"); graph.Append(page, content.ToString());
    }
    private static void InsertImage(PdfGraph graph, AddImage image, CancellationToken token)
    {
        var page = graph.Page(image.PageNumber); page.Validate(image.Bounds);
        var name = graph.Resource(page, "XObject", CreateImageResource(graph, image.Image, false, token));
        var b = image.Bounds;
        graph.Append(page, "q\n" + page.ViewMatrix() + $"{F(b.Width)} 0 0 {F(-b.Height)} {F(b.X)} {F(b.Bottom)} cm\n/{name} Do\nQ\n");
    }
    private static string Ellipse(double x, double y, double width, double height)
    {
        var rx = width / 2; var ry = height / 2; var cx = x + rx; var cy = y + ry; const double k = .5522847498307936;
        return $"{F(cx+rx)} {F(cy)} m\n{F(cx+rx)} {F(cy+ry*k)} {F(cx+rx*k)} {F(cy+ry)} {F(cx)} {F(cy+ry)} c\n" +
            $"{F(cx-rx*k)} {F(cy+ry)} {F(cx-rx)} {F(cy+ry*k)} {F(cx-rx)} {F(cy)} c\n" +
            $"{F(cx-rx)} {F(cy-ry*k)} {F(cx-rx*k)} {F(cy-ry)} {F(cx)} {F(cy-ry)} c\n" +
            $"{F(cx+rx*k)} {F(cy-ry)} {F(cx+rx)} {F(cy-ry*k)} {F(cx+rx)} {F(cy)} c\nh\n";
    }
    private static void InsertShape(PdfGraph graph, AddShape shape)
    {
        var page = graph.Page(shape.PageNumber); page.Validate(shape.Bounds);
        if (!double.IsFinite(shape.StrokeWidth) || shape.StrokeWidth is <= 0 or > 1000) throw new ArgumentOutOfRangeException(nameof(shape.StrokeWidth));
        var color = shape.Stroke ?? PdfColor.Blue; var b = shape.Bounds;
        var content = "q\n" + page.ViewMatrix() + Rgb(color, true) + $"{F(shape.StrokeWidth)} w\n";
        if (shape.Fill is { } fill) content += Rgb(fill);
        if (color.A != 255 || shape.Fill is { A: < 255 })
        {
            var state = graph.Resource(page, "ExtGState", Dictionary(("CA", new PdfNumber(color.A / 255d)), ("ca", new PdfNumber((shape.Fill?.A ?? 255) / 255d))));
            content += $"/{state} gs\n";
        }
        content += shape.Kind switch { PdfShapeKind.Rectangle => $"{F(b.X)} {F(b.Y)} {F(b.Width)} {F(b.Height)} re\n", PdfShapeKind.Ellipse => Ellipse(b.X,b.Y,b.Width,b.Height), _ => throw new ArgumentOutOfRangeException(nameof(shape.Kind)) };
        graph.Append(page, content + (shape.Fill.HasValue ? "B\n" : "S\n") + "Q\n");
    }
}
