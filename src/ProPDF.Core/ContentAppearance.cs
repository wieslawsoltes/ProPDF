using System.Collections.Immutable;

namespace ProPDF.Core;

/// <summary>PDF blend modes. Null in an appearance patch preserves the original blend mode.</summary>
public enum PdfBlendMode
{
    Normal, Multiply, Screen, Overlay, Darken, Lighten, ColorDodge, ColorBurn,
    HardLight, SoftLight, Difference, Exclusion, Hue, Saturation, Color, Luminosity
}
public enum PdfLineCap { Butt, Round, Square }
public enum PdfLineJoin { Miter, Round, Bevel }
public enum PdfPathPaintMode { Preserve, Fill, Stroke, FillAndStroke }

/// <summary>An immutable PDF dash array in local graphics-space units. An empty array selects a solid line.</summary>
public sealed class PdfDashPattern
{
    public PdfDashPattern(IEnumerable<double> lengths, double phase = 0)
    {
        ArgumentNullException.ThrowIfNull(lengths);
        Lengths = lengths.Take(33).ToImmutableArray();
        if (Lengths.Length > 32 || Lengths.Any(x => !double.IsFinite(x) || x is < 0 or > 1_000_000) ||
            !Lengths.IsEmpty && Lengths.All(x => x == 0) || !double.IsFinite(phase) || phase is < 0 or > 1_000_000)
            throw new ArgumentOutOfRangeException(nameof(lengths), "Dash patterns require at most 32 finite nonnegative lengths and a nonnegative phase; a nonempty pattern cannot be all zero.");
        Phase = phase;
    }
    public ImmutableArray<double> Lengths { get; }
    public double Phase { get; }
    public static PdfDashPattern Solid { get; } = new([]);
}

/// <summary>
/// Partial appearance update: null properties preserve original state. Colors replace both RGB and that color's alpha;
/// Opacity multiplies any supplied color alpha, otherwise sets both stroke/fill alpha directly. Width/dashes are local
/// PDF graphics-space units, not screen pixels. PathPaint does not change text rendering modes.
/// </summary>
public sealed record PdfContentAppearance(PdfColor? FillColor = null, PdfColor? StrokeColor = null,
    double? StrokeWidth = null, double? Opacity = null, PdfBlendMode? BlendMode = null,
    PdfPathPaintMode PathPaint = PdfPathPaintMode.Preserve, PdfLineCap? LineCap = null,
    PdfLineJoin? LineJoin = null, PdfDashPattern? Dash = null)
{
    public void Validate()
    {
        if (StrokeWidth is { } width && (!double.IsFinite(width) || width is < 0 or > 1000) ||
            Opacity is { } opacity && (!double.IsFinite(opacity) || opacity is < 0 or > 1) ||
            BlendMode is { } blend && !Enum.IsDefined(blend) || !Enum.IsDefined(PathPaint) ||
            LineCap is { } cap && !Enum.IsDefined(cap) || LineJoin is { } join && !Enum.IsDefined(join))
            throw new ArgumentOutOfRangeException(nameof(PdfContentAppearance), "Invalid appearance value.");
        if (FillColor is null && StrokeColor is null && StrokeWidth is null && Opacity is null && BlendMode is null &&
            PathPaint == PdfPathPaintMode.Preserve && LineCap is null && LineJoin is null && Dash is null)
            throw new ArgumentException("Choose at least one appearance property to change.");
    }
}

/// <summary>Styles one text/path/image invocation, preserving its geometry, resources and following graphics state.</summary>
public sealed record SetContentAppearance(PdfContentObjectReference Object, PdfContentAppearance Appearance)
    : PdfContentObjectEdit(Object, "Change existing content appearance");

/// <summary>Replaces one image invocation, not every use of its resource. Keeps its transform, clipping and painting order.
/// Replacement pixels stretch to the original image's unit square. Null Interpolate preserves the original setting.</summary>
public sealed record ReplaceContentImage(PdfContentObjectReference Object, PdfBinaryAsset Image, bool? Interpolate = null)
    : PdfContentObjectEdit(Object, "Replace existing image");

/// <summary>Copies an image resource and changes interpolation for the selected invocation only.</summary>
public sealed record SetContentImageInterpolation(PdfContentObjectReference Object, bool Interpolate)
    : PdfContentObjectEdit(Object, "Change image interpolation");

/// <summary>Image dictionary metadata, without decoding image pixels or masks.</summary>
public sealed record PdfContentImageInfo(int PixelWidth, int PixelHeight, int BitsPerComponent,
    string ColorSpace, bool Interpolate, bool HasSoftMask);
