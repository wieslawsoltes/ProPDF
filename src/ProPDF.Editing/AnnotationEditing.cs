using System.Text;
using ProPDF.Core;
using ProPDF.Kernel;
using static ProPDF.Editing.PdfGraph;
using static ProPDF.Kernel.PdfValues;

namespace ProPDF.Editing;

public sealed partial class ManagedPdfEditor
{
    private static (PdfObject Raw, PdfDictionary Dictionary) FindAnnotation(PdfGraph graph, NativePage page, string id)
    {
        foreach (var item in graph.Annotations(page)) if (AnnotationId(item.Raw, item.Dictionary) == id) return item;
        throw new KeyNotFoundException(id);
    }
    private static PdfReference Appearance(PdfGraph graph, PdfRect bounds, string content, PdfDictionary? resources = null)
    {
        var dictionary = Dictionary(("Type", new PdfName("XObject")), ("Subtype", new PdfName("Form")), ("FormType", new PdfNumber(1L)),
            ("BBox", Numbers(0, 0, bounds.Width, bounds.Height)), ("Resources", resources ?? new PdfDictionary()));
        return graph.File.Add(PdfStream.FromDecoded(Encoding.ASCII.GetBytes(content), dictionary));
    }
    private static void InsertAnnotation(PdfGraph graph, AddAnnotation annotation)
    {
        var page = graph.Page(annotation.PageNumber); page.Validate(annotation.Bounds);
        if (annotation.Contents.Length > 1_000_000 || annotation.Author.Length > 4096) throw new ArgumentOutOfRangeException(nameof(annotation));
        var bounds = page.Transform.ToPdf(annotation.Bounds); var color = annotation.Color ?? (annotation.Kind is PdfAnnotationKind.Highlight or PdfAnnotationKind.Note ? PdfColor.Yellow : PdfColor.Blue);
        var kind = annotation.Kind switch
        {
            PdfAnnotationKind.Note => "Text", PdfAnnotationKind.FreeText => "FreeText", PdfAnnotationKind.Highlight => "Highlight", PdfAnnotationKind.Underline => "Underline",
            PdfAnnotationKind.StrikeOut => "StrikeOut", PdfAnnotationKind.Rectangle => "Square", PdfAnnotationKind.Ellipse => "Circle", PdfAnnotationKind.Link => "Link",
            _ => throw new ArgumentOutOfRangeException(nameof(annotation.Kind))
        };
        var dictionary = Dictionary(("Subtype", new PdfName(kind)), ("Rect", Numbers(bounds.X,bounds.Y,bounds.Right,bounds.Bottom)), ("F", new PdfNumber(4L)),
            ("Contents", new PdfString(annotation.Contents)), ("T", new PdfString(annotation.Author)), ("C", Numbers(color.R/255d,color.G/255d,color.B/255d)),
            ("M", new PdfString(DateTime.UtcNow.ToString("'D:'yyyyMMddHHmmss'Z'", System.Globalization.CultureInfo.InvariantCulture))));
        var resources = new PdfDictionary(); var content = "q\n" + Rgb(color) + Rgb(color,true) + "1 w\n";
        var width = bounds.Width; var height = bounds.Height;
        switch (annotation.Kind)
        {
            case PdfAnnotationKind.Highlight:
                resources["ExtGState"] = Dictionary(("PPAlpha", graph.File.Add(Dictionary(("ca", new PdfNumber(.3)), ("BM", new PdfName("Multiply"))))));
                content += $"/PPAlpha gs\n0 0 {F(width)} {F(height)} re f\n"; break;
            case PdfAnnotationKind.Underline: content += $"0 1 m {F(width)} 1 l S\n"; break;
            case PdfAnnotationKind.StrikeOut: content += $"0 {F(height/2)} m {F(width)} {F(height/2)} l S\n"; break;
            case PdfAnnotationKind.Ellipse: content += Ellipse(.5,.5,Math.Max(0,width-1),Math.Max(0,height-1)) + "S\n"; break;
            case PdfAnnotationKind.Note: content += $".5 .5 {F(Math.Max(0,width-1))} {F(Math.Max(0,height-1))} re B\n"; dictionary["Name"] = new PdfName("Comment"); break;
            case PdfAnnotationKind.FreeText:
                var font = PdfFonts.Create(graph, "Helvetica", annotation.EmbeddedFont, annotation.Contents); resources["Font"] = Dictionary(("PPFont",font.Reference));
                dictionary["DA"] = new PdfString("/PPFont 12 Tf 0 g");
                content += $"0 0 {F(width)} {F(height)} re W n\n0 g\n";
                var lines = annotation.Contents.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n');
                for (var i = 0; i < lines.Length && height - 14 - i*14 >= 0; i++)
                    content += $"BT /PPFont 12 Tf 1 0 0 1 3 {F(height-14-i*14)} Tm {Hex(font.Encode(lines[i]))} Tj ET\n";
                break;
            default: content += $".5 .5 {F(Math.Max(0,width-1))} {F(Math.Max(0,height-1))} re S\n"; break;
        }
        if (annotation.Kind is PdfAnnotationKind.Highlight or PdfAnnotationKind.Underline or PdfAnnotationKind.StrikeOut)
            dictionary["QuadPoints"] = Numbers(bounds.X,bounds.Bottom,bounds.Right,bounds.Bottom,bounds.X,bounds.Y,bounds.Right,bounds.Y);
        if (annotation.Kind == PdfAnnotationKind.Link)
        {
            if (!PdfUriPolicy.TryNormalize(annotation.Uri, out var uri)) throw new ArgumentException("Link URI is not allowed.");
            dictionary["A"] = Dictionary(("S",new PdfName("URI")), ("URI",new PdfString(uri!.AbsoluteUri))); dictionary["Border"] = Numbers(0,0,1);
        }
        dictionary["AP"] = Dictionary(("N", Appearance(graph,bounds,content+"Q\n",resources))); graph.AddAnnotation(page,dictionary);
    }
    private static void InsertInk(PdfGraph graph, AddInkAnnotation ink)
    {
        if (ink.Points.Length is < 2 or > 100_000 || !double.IsFinite(ink.Width) || ink.Width is <= 0 or > 100) throw new ArgumentOutOfRangeException(nameof(ink));
        var page = graph.Page(ink.PageNumber); var transform = page.Transform;
        foreach (var p in ink.Points) if (!double.IsFinite(p.X) || !double.IsFinite(p.Y) || p.X < 0 || p.Y < 0 || p.X > transform.ViewSize.Width || p.Y > transform.ViewSize.Height) throw new ArgumentOutOfRangeException(nameof(ink.Points));
        var points = ink.Points.Select(transform.ToPdf).ToArray(); var half = ink.Width/transform.UserUnit/2;
        var x = points.Min(p=>p.X)-half; var y = points.Min(p=>p.Y)-half;
        var bounds = new PdfRect(x,y,points.Max(p=>p.X)-x+half,points.Max(p=>p.Y)-y+half);
        var content = new StringBuilder("q\n").Append(Rgb(ink.Color,true)).Append(F(ink.Width/transform.UserUnit)).Append(" w 1 J 1 j\n");
        content.Append(F(points[0].X-x)).Append(' ').Append(F(points[0].Y-y)).Append(" m\n");
        foreach (var p in points.Skip(1)) content.Append(F(p.X-x)).Append(' ').Append(F(p.Y-y)).Append(" l\n");
        content.Append("S Q\n");
        graph.AddAnnotation(page, Dictionary(("Subtype",new PdfName("Ink")), ("F",new PdfNumber(4L)), ("Rect",Numbers(bounds.X,bounds.Y,bounds.Right,bounds.Bottom)),
            ("InkList",new PdfArray((PdfObject)Numbers(points.SelectMany(p=>new[]{p.X,p.Y}).ToArray()))), ("C",Numbers(ink.Color.R/255d,ink.Color.G/255d,ink.Color.B/255d)),
            ("BS",Dictionary(("W",new PdfNumber(ink.Width/transform.UserUnit)))), ("AP",Dictionary(("N",Appearance(graph,bounds,content.ToString()))))));
    }
    private static void InsertInternalLink(PdfGraph graph, AddInternalLink link)
    {
        var page = graph.Page(link.PageNumber); page.Validate(link.Bounds); var target = graph.Page(link.DestinationPage); var b = page.Transform.ToPdf(link.Bounds);
        PdfArray destination;
        if (link.DestinationPoint is { } point)
        {
            if (!double.IsFinite(point.X) || !double.IsFinite(point.Y) || point.X < 0 || point.Y < 0 || point.X > target.Transform.ViewSize.Width || point.Y > target.Transform.ViewSize.Height) throw new ArgumentOutOfRangeException(nameof(link.DestinationPoint));
            var raw = target.Transform.ToPdf(point); destination = new PdfArray(target.Reference,new PdfName("XYZ"),new PdfNumber(raw.X),new PdfNumber(raw.Y),PdfNull.Value);
        }
        else destination = new PdfArray(target.Reference,new PdfName("Fit"));
        graph.AddAnnotation(page, Dictionary(("Subtype",new PdfName("Link")), ("Rect",Numbers(b.X,b.Y,b.Right,b.Bottom)), ("Dest",destination), ("Border",Numbers(0,0,0)), ("F",new PdfNumber(4L))));
    }
}
