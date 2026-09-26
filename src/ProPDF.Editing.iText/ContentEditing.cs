using System.Text;
using iText.IO.Font;
using iText.IO.Font.Constants;
using iText.IO.Image;
using iText.Kernel.Colors;
using iText.Kernel.Font;
using iText.Kernel.Pdf;
using iText.Kernel.Pdf.Canvas;
using iText.Kernel.Pdf.Extgstate;
using iText.PdfCleanup;
using ProPDF.Core;

namespace ProPDF.Editing.iText;

public sealed partial class ITextPdfEditor
{
    private static DeviceRgb Rgb(PdfColor color) => new(color.R, color.G, color.B);

    /// <summary>Wraps pre-existing content and establishes the shared viewer-to-PDF coordinate transform.</summary>
    private static PdfCanvas BeginViewCanvas(PdfPage page)
    {
        var transform = Transform(page);
        var origin = transform.ToPdf(new PdfPoint(0, 0));
        var x = transform.ToPdf(new PdfPoint(1, 0));
        var y = transform.ToPdf(new PdfPoint(0, 1));
        var canvas = new PdfCanvas(page, true);
        canvas.SaveState();
        canvas.ConcatMatrix(x.X - origin.X, x.Y - origin.Y, y.X - origin.X, y.Y - origin.Y, origin.X, origin.Y);
        return canvas;
    }

    private static PdfFont Font(string name, PdfBinaryAsset? embedded, string text)
    {
        PdfFont font;
        if (embedded is not null)
            font = PdfFontFactory.CreateFont(embedded.ToArray(), PdfEncodings.IDENTITY_H, PdfFontFactory.EmbeddingStrategy.FORCE_EMBEDDED);
        else
        {
            var standard = new HashSet<string>(StringComparer.Ordinal)
            {
                StandardFonts.HELVETICA, StandardFonts.HELVETICA_BOLD, StandardFonts.HELVETICA_OBLIQUE,
                StandardFonts.HELVETICA_BOLDOBLIQUE, StandardFonts.TIMES_ROMAN, StandardFonts.TIMES_BOLD,
                StandardFonts.TIMES_ITALIC, StandardFonts.TIMES_BOLDITALIC, StandardFonts.COURIER,
                StandardFonts.COURIER_BOLD, StandardFonts.COURIER_OBLIQUE, StandardFonts.COURIER_BOLDOBLIQUE,
                StandardFonts.SYMBOL, StandardFonts.ZAPFDINGBATS
            };
            if (!standard.Contains(name)) throw new ArgumentException("Supply font bytes for non-standard fonts.", nameof(name));
            font = PdfFontFactory.CreateFont(name);
        }
        foreach (var rune in text.EnumerateRunes())
            if (rune.Value is not 10 and not 13 && !font.ContainsGlyph(rune.Value))
                throw new NotSupportedException($"The chosen font cannot encode U+{rune.Value:X4}. Supply a suitable embeddable font.");
        return font;
    }

    private static void InsertText(PdfDocument document, AddText operation)
    {
        ArgumentNullException.ThrowIfNull(operation.Text);
        if (operation.Text.Length > 1_000_000 || !double.IsFinite(operation.FontSize) || operation.FontSize is < 1 or > 1000 ||
            !double.IsFinite(operation.Baseline.X) || !double.IsFinite(operation.Baseline.Y))
            throw new ArgumentOutOfRangeException(nameof(operation));
        var page = GetPage(document, operation.PageNumber);
        var size = Transform(page).ViewSize;
        if (operation.Baseline.X < 0 || operation.Baseline.Y < 0 || operation.Baseline.X > size.Width || operation.Baseline.Y > size.Height)
            throw new ArgumentOutOfRangeException(nameof(operation.Baseline));
        var font = Font(operation.StandardFont, operation.EmbeddedFont, operation.Text);
        var color = operation.Color ?? PdfColor.Black;
        var canvas = BeginViewCanvas(page);
        try
        {
            canvas.SetFillColor(Rgb(color)).SetExtGState(new PdfExtGState().SetFillOpacity(color.A / 255f));
            var lines = operation.Text.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n').Split('\n');
            for (var index = 0; index < lines.Length; index++)
                canvas.BeginText().SetFontAndSize(font, (float)operation.FontSize)
                    .SetTextMatrix(1, 0, 0, -1, (float)operation.Baseline.X, (float)(operation.Baseline.Y + index * operation.FontSize * 1.2))
                    .ShowText(lines[index]).EndText();
        }
        finally { canvas.RestoreState(); canvas.Release(); }
    }

    private static void InsertImage(PdfDocument document, AddImage operation)
    {
        var page = GetPage(document, operation.PageNumber);
        ValidateBounds(page, operation.Bounds);
        var canvas = BeginViewCanvas(page);
        try
        {
            var bounds = operation.Bounds;
            canvas.AddImageWithTransformationMatrix(ImageDataFactory.Create(operation.Image.ToArray()),
                (float)bounds.Width, 0, 0, (float)-bounds.Height, (float)bounds.X, (float)bounds.Bottom, false);
        }
        finally { canvas.RestoreState(); canvas.Release(); }
    }

    private static void InsertShape(PdfDocument document, AddShape operation)
    {
        var page = GetPage(document, operation.PageNumber);
        ValidateBounds(page, operation.Bounds);
        if (!double.IsFinite(operation.StrokeWidth) || operation.StrokeWidth is <= 0 or > 1000)
            throw new ArgumentOutOfRangeException(nameof(operation.StrokeWidth));
        var canvas = BeginViewCanvas(page);
        try
        {
            var stroke = operation.Stroke ?? PdfColor.Blue;
            canvas.SetStrokeColor(Rgb(stroke)).SetLineWidth((float)operation.StrokeWidth);
            var state = new PdfExtGState().SetStrokeOpacity(stroke.A / 255f);
            if (operation.Fill is { } fill)
            {
                canvas.SetFillColor(Rgb(fill));
                state.SetFillOpacity(fill.A / 255f);
            }
            canvas.SetExtGState(state);
            var bounds = operation.Bounds;
            if (operation.Kind == PdfShapeKind.Ellipse) canvas.Ellipse(bounds.X, bounds.Y, bounds.Right, bounds.Bottom);
            else if (operation.Kind == PdfShapeKind.Rectangle) canvas.Rectangle(bounds.X, bounds.Y, bounds.Width, bounds.Height);
            else throw new ArgumentOutOfRangeException(nameof(operation.Kind));
            if (operation.Fill.HasValue) canvas.FillStroke();
            else canvas.Stroke();
        }
        finally { canvas.RestoreState(); canvas.Release(); }
    }

    private static void ApplyRedaction(PdfDocument document, RedactRegion operation, CancellationToken cancellationToken)
    {
        var page = GetPage(document, operation.PageNumber);
        ValidateBounds(page, operation.Bounds);
        // Tagged ActualText and structure-tree preservation need a dedicated qualification suite.
        // Refuse rather than expose visually removed text through an unqualified semantic side channel.
        if (document.GetCatalog().GetPdfObject().ContainsKey(PdfName.StructTreeRoot))
            throw new NotSupportedException("Redaction of tagged PDFs is not yet qualified and is disabled.");
        var bounds = Transform(page).ToPdf(operation.Bounds);
        foreach (var annotation in page.GetAnnotations().ToArray())
        {
            var rectangle = annotation.GetRectangle().ToRectangle();
            var region = new PdfRect(rectangle.GetX(), rectangle.GetY(), rectangle.GetWidth(), rectangle.GetHeight());
            if (!region.Intersects(bounds)) continue;
            if (annotation.GetSubtype().Equals(PdfName.Widget))
                throw new NotSupportedException("Flatten the intersecting form field before redacting its contents.");
            page.RemoveAnnotation(annotation);
        }
        cancellationToken.ThrowIfCancellationRequested();
        var cleanup = new PdfCleanUpTool(document);
        cleanup.AddCleanupLocation(new PdfCleanUpLocation(operation.PageNumber, ToRectangle(bounds), Rgb(operation.Fill ?? PdfColor.Black)));
        cleanup.CleanUp();
        cancellationToken.ThrowIfCancellationRequested();
    }
}
