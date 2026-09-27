using iText.Kernel.Font;
using iText.Kernel.Geom;
using iText.Kernel.Pdf;
using iText.Kernel.Pdf.Action;
using iText.Kernel.Pdf.Annot;
using iText.Kernel.Pdf.Canvas;
using iText.Kernel.Pdf.Extgstate;
using iText.Kernel.Pdf.Xobject;
using ProPDF.Core;

namespace ProPDF.Editing.iText;

public sealed partial class ITextPdfEditor
{
    internal static string AnnotationId(PdfAnnotation annotation) =>
        annotation.GetPdfObject().GetAsString(PdfName.NM)?.ToUnicodeString() ??
        $"object:{annotation.GetPdfObject().GetIndirectReference()?.GetObjNumber() ?? 0}";

    private static PdfAnnotation FindAnnotation(PdfPage page, string id) => page.GetAnnotations()
        .FirstOrDefault(annotation => AnnotationId(annotation) == id) ?? throw new KeyNotFoundException(id);

    private static void InsertAnnotation(PdfDocument document, AddAnnotation operation)
    {
        var page = GetPage(document, operation.PageNumber);
        ValidateBounds(page, operation.Bounds);
        if (operation.Contents.Length > 1_000_000 || operation.Author.Length > 4096) throw new ArgumentOutOfRangeException(nameof(operation));
        var rectangle = ToRectangle(Transform(page).ToPdf(operation.Bounds));
        var color = operation.Color ?? (operation.Kind is PdfAnnotationKind.Highlight or PdfAnnotationKind.Note ? PdfColor.Yellow : PdfColor.Blue);
        PdfAnnotation annotation;
        switch (operation.Kind)
        {
            case PdfAnnotationKind.Note: annotation = new PdfTextAnnotation(rectangle); break;
            case PdfAnnotationKind.FreeText:
                annotation = new PdfFreeTextAnnotation(rectangle, new PdfString(operation.Contents));
                break;
            case PdfAnnotationKind.Highlight:
            case PdfAnnotationKind.Underline:
            case PdfAnnotationKind.StrikeOut:
                var quads = new[] { rectangle.GetLeft(), rectangle.GetTop(), rectangle.GetRight(), rectangle.GetTop(),
                    rectangle.GetLeft(), rectangle.GetBottom(), rectangle.GetRight(), rectangle.GetBottom() };
                annotation = operation.Kind switch
                {
                    PdfAnnotationKind.Highlight => PdfTextMarkupAnnotation.CreateHighLight(rectangle, quads),
                    PdfAnnotationKind.Underline => PdfTextMarkupAnnotation.CreateUnderline(rectangle, quads),
                    _ => PdfTextMarkupAnnotation.CreateStrikeout(rectangle, quads)
                };
                break;
            case PdfAnnotationKind.Rectangle: annotation = new PdfSquareAnnotation(rectangle); break;
            case PdfAnnotationKind.Ellipse: annotation = new PdfCircleAnnotation(rectangle); break;
            case PdfAnnotationKind.Link:
                if (!System.Uri.TryCreate(operation.Uri, UriKind.Absolute, out var uri) || uri.Scheme is not ("https" or "http" or "mailto"))
                    throw new ArgumentException("Links must use https, http or mailto.", nameof(operation.Uri));
                annotation = new PdfLinkAnnotation(rectangle).SetAction(PdfAction.CreateURI(uri.AbsoluteUri));
                break;
            default: throw new ArgumentOutOfRangeException(nameof(operation.Kind));
        }
        annotation.SetContents(new PdfString(operation.Contents)).SetColor(Rgb(color)).SetFlags(PdfAnnotation.PRINT);
        annotation.GetPdfObject().Put(PdfName.NM, new PdfString(Guid.NewGuid().ToString("N")));
        annotation.GetPdfObject().Put(PdfName.T, new PdfString(operation.Author));
        annotation.GetPdfObject().Put(PdfName.M, new PdfDate().GetPdfObject());
        annotation.SetNormalAppearance(CreateAppearance(document, rectangle, operation, color).GetPdfObject());
        page.AddAnnotation(annotation);
    }

    private static PdfFormXObject CreateAppearance(PdfDocument document, Rectangle rectangle, AddAnnotation operation, PdfColor color)
    {
        var width = rectangle.GetWidth();
        var height = rectangle.GetHeight();
        var appearance = new PdfFormXObject(new Rectangle(0, 0, width, height));
        var canvas = new PdfCanvas(appearance, document);
        canvas.SaveState().SetStrokeColor(Rgb(color)).SetFillColor(Rgb(color)).SetLineWidth(1);
        switch (operation.Kind)
        {
            case PdfAnnotationKind.Highlight:
                canvas.SetExtGState(new PdfExtGState().SetFillOpacity(0.3f).SetBlendMode(PdfName.Multiply));
                canvas.Rectangle(0, 0, width, height).Fill();
                break;
            case PdfAnnotationKind.Underline: canvas.MoveTo(0, 1).LineTo(width, 1).Stroke(); break;
            case PdfAnnotationKind.StrikeOut: canvas.MoveTo(0, height / 2).LineTo(width, height / 2).Stroke(); break;
            case PdfAnnotationKind.Ellipse: canvas.Ellipse(1, 1, Math.Max(1, width - 1), Math.Max(1, height - 1)).Stroke(); break;
            case PdfAnnotationKind.Note:
                canvas.Rectangle(0.5, 0.5, Math.Max(0, width - 1), Math.Max(0, height - 1)).FillStroke();
                break;
            case PdfAnnotationKind.FreeText:
                var font = Font("Helvetica", operation.EmbeddedFont, operation.Contents);
                canvas.SetFillColor(Rgb(PdfColor.Black));
                canvas.Rectangle(0, 0, width, height).Clip().EndPath();
                var lines = operation.Contents.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n');
                for (var index = 0; index < lines.Length && height - 14 - index * 14 >= 0; index++)
                    canvas.BeginText().SetFontAndSize(font, 12).MoveText(3, height - 14 - index * 14).ShowText(lines[index]).EndText();
                break;
            default: canvas.Rectangle(0.5, 0.5, Math.Max(0, width - 1), Math.Max(0, height - 1)).Stroke(); break;
        }
        canvas.RestoreState();
        canvas.Release();
        return appearance;
    }

    private static void InsertInk(PdfDocument document, AddInkAnnotation operation)
    {
        if (operation.Points.Length is < 2 or > 100_000 || !double.IsFinite(operation.Width) || operation.Width is <= 0 or > 100)
            throw new ArgumentOutOfRangeException(nameof(operation));
        var page = GetPage(document, operation.PageNumber);
        var transform = Transform(page);
        var viewSize = transform.ViewSize;
        foreach (var point in operation.Points)
            if (!double.IsFinite(point.X) || !double.IsFinite(point.Y) || point.X < 0 || point.Y < 0 || point.X > viewSize.Width || point.Y > viewSize.Height)
                throw new ArgumentOutOfRangeException(nameof(operation.Points));
        var points = operation.Points.Select(transform.ToPdf).ToArray();
        var halfWidth = operation.Width / transform.UserUnit / 2;
        var left = points.Min(p => p.X) - halfWidth;
        var bottom = points.Min(p => p.Y) - halfWidth;
        var width = points.Max(p => p.X) - left + halfWidth;
        var height = points.Max(p => p.Y) - bottom + halfWidth;
        var rectangle = new Rectangle((float)left, (float)bottom, (float)width, (float)height);
        var inkList = new PdfArray();
        var stroke = new PdfArray();
        foreach (var point in points) { stroke.Add(new PdfNumber(point.X)); stroke.Add(new PdfNumber(point.Y)); }
        inkList.Add(stroke);
        var annotation = new PdfInkAnnotation(rectangle, inkList);
        annotation.SetColor(Rgb(operation.Color)).SetFlags(PdfAnnotation.PRINT);
        annotation.GetPdfObject().Put(PdfName.NM, new PdfString(Guid.NewGuid().ToString("N")));
        var appearance = new PdfFormXObject(new Rectangle(0, 0, (float)width, (float)height));
        var canvas = new PdfCanvas(appearance, document);
        canvas.SetStrokeColor(Rgb(operation.Color)).SetLineWidth((float)(operation.Width / transform.UserUnit)).SetLineCapStyle(1).SetLineJoinStyle(1);
        canvas.MoveTo(points[0].X - left, points[0].Y - bottom);
        foreach (var point in points.Skip(1)) canvas.LineTo(point.X - left, point.Y - bottom);
        canvas.Stroke();
        canvas.Release();
        annotation.SetNormalAppearance(appearance.GetPdfObject());
        page.AddAnnotation(annotation);
    }
}
