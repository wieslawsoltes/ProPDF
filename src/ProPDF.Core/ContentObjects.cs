namespace ProPDF.Core;

/// <summary>An affine mapping of normalized, top-left PDF-point coordinates. Concat applies the local mapping first.</summary>
public readonly record struct PdfAffineTransform(double A, double B, double C, double D, double E, double F)
{
    public static PdfAffineTransform Identity => new(1, 0, 0, 1, 0, 0);
    public static PdfAffineTransform Translation(double x, double y) => new(1, 0, 0, 1, x, y);
    public static PdfAffineTransform ScaleAt(double x, double y, PdfPoint origin) => new(x, 0, 0, y, origin.X * (1 - x), origin.Y * (1 - y));
    public static PdfAffineTransform RotationAt(double clockwiseDegrees, PdfPoint origin)
    {
        var angle = clockwiseDegrees * Math.PI / 180; var c = Math.Cos(angle); var s = Math.Sin(angle);
        return new(c, s, -s, c, origin.X - c * origin.X + s * origin.Y, origin.Y - s * origin.X - c * origin.Y);
    }
    public PdfPoint Map(PdfPoint p) => new(A * p.X + C * p.Y + E, B * p.X + D * p.Y + F);
    public PdfRect Map(PdfRect r)
    {
        var points = new[] { Map(new PdfPoint(r.X, r.Y)), Map(new PdfPoint(r.Right, r.Y)), Map(new PdfPoint(r.Right, r.Bottom)), Map(new PdfPoint(r.X, r.Bottom)) };
        return new(points.Min(p => p.X), points.Min(p => p.Y), points.Max(p => p.X) - points.Min(p => p.X), points.Max(p => p.Y) - points.Min(p => p.Y));
    }
    public PdfAffineTransform Concat(PdfAffineTransform local) => new(A * local.A + C * local.B, B * local.A + D * local.B,
        A * local.C + C * local.D, B * local.C + D * local.D, A * local.E + C * local.F + E, B * local.E + D * local.F + F);
    public void Validate()
    {
        if (new[] { A, B, C, D, E, F }.Any(v => !double.IsFinite(v) || Math.Abs(v) > 1e9) || Math.Abs(A * D - B * C) < 1e-12)
            throw new ArgumentOutOfRangeException(nameof(PdfAffineTransform), "Transform must be finite, bounded and invertible.");
    }
    public PdfAffineTransform Inverse()
    {
        Validate(); var det = A * D - B * C;
        return new(D / det, -B / det, -C / det, A / det, (C * F - D * E) / det, (B * E - A * F) / det);
    }
}

public enum PdfContentObjectKind { Text, Path, Image, Form, Shading }

/// <summary>A revision-bound handle, not a durable PDF object number. Re-inspect after modifying a page.</summary>
public sealed record PdfContentObjectReference(Guid Revision, int PageNumber, string Fingerprint, int Index);
public sealed record PdfContentObject(PdfContentObjectReference Reference, PdfContentObjectKind Kind, PdfRect Bounds,
    string? Text = null, string? ResourceName = null, string? ReadOnlyReason = null)
{
    public bool CanEdit => ReadOnlyReason is null;
    public override string ToString() => $"{Reference.Index + 1}. {Kind}" + (Text is { Length: > 0 } text ? " — " + text[..Math.Min(48, text.Length)].Replace('\n', ' ') : "");
}
public sealed record PdfContentInspectionOptions(int MaximumObjects = 100_000, int MaximumInstructions = 1_000_000);
public sealed record PdfPageContent(Guid Revision, int PageNumber, IReadOnlyList<PdfContentObject> Objects);
public interface IPdfContentService
{
    Task<PdfPageContent> ReadPageContentAsync(PdfSnapshot source, int pageNumber, PdfContentInspectionOptions? options = null, CancellationToken cancellationToken = default);
}
public abstract record PdfContentObjectEdit(PdfContentObjectReference Object, string Description)
    : PdfEditOperation(PdfCapability.ContentReplacement, Description);
public sealed record TransformContentObject(PdfContentObjectReference Object, PdfAffineTransform Transform)
    : PdfContentObjectEdit(Object, "Transform existing content object");
public sealed record DeleteContentObject(PdfContentObjectReference Object) : PdfContentObjectEdit(Object, "Delete existing content object");
/// <summary>Duplicates an invocation immediately before its original, without modifying shared resources.</summary>
public sealed record DuplicateContentObject(PdfContentObjectReference Object, PdfAffineTransform Transform)
    : PdfContentObjectEdit(Object, "Duplicate existing content object");
/// <summary>Visual clipping only. The underlying content remains present and this must never be used as redaction.</summary>
public sealed record ClipContentObject(PdfContentObjectReference Object, PdfRect Bounds) : PdfContentObjectEdit(Object, "Clip existing content object");
public enum PdfTextAlignment { Left, Center, Right }
/// <summary>Creates native wrapped text in a rectangle. Overflow rejects the transaction rather than silently dropping text.</summary>
public sealed record AddTextBox(int PageNumber, PdfRect Bounds, string Text, double FontSize = 14, double LineSpacing = 1.2,
    PdfTextAlignment Alignment = PdfTextAlignment.Left, string StandardFont = "Helvetica", PdfColor? Color = null, PdfBinaryAsset? EmbeddedFont = null)
    : PdfEditOperation(PdfCapability.ContentInsertion, "Insert wrapped text box");
/// <summary>Replaces an entire text object with a wrapped, axis-aligned text box; other page content is preserved.</summary>
public sealed record ReplaceContentText(PdfContentObjectReference Object, PdfRect Bounds, string Text, double FontSize = 14,
    PdfTextAlignment Alignment = PdfTextAlignment.Left, string StandardFont = "Helvetica", PdfColor? Color = null, PdfBinaryAsset? EmbeddedFont = null)
    : PdfContentObjectEdit(Object, "Replace existing text object");
