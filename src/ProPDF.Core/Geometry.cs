namespace ProPDF.Core;

/// <summary>A point in top-left-origin, cropped and rotated PDF view coordinates (1/72 inch).</summary>
public readonly record struct PdfPoint(double X, double Y);

public readonly record struct PdfSize
{
    public PdfSize(double width, double height)
    {
        if (!double.IsFinite(width) || !double.IsFinite(height) || width <= 0 || height <= 0)
            throw new ArgumentOutOfRangeException(nameof(width), "Page dimensions must be finite and positive.");
        Width = width;
        Height = height;
    }

    public double Width { get; }
    public double Height { get; }
}

public readonly record struct PdfRect
{
    public PdfRect(double x, double y, double width, double height)
    {
        if (!double.IsFinite(x) || !double.IsFinite(y) || !double.IsFinite(width) ||
            !double.IsFinite(height) || width < 0 || height < 0 ||
            !double.IsFinite(x + width) || !double.IsFinite(y + height))
            throw new ArgumentOutOfRangeException(nameof(width), "Rectangle coordinates must be finite; extents cannot be negative.");
        X = x;
        Y = y;
        Width = width;
        Height = height;
    }

    public double X { get; }
    public double Y { get; }
    public double Width { get; }
    public double Height { get; }
    public double Right => X + Width;
    public double Bottom => Y + Height;
    public bool IsEmpty => Width == 0 || Height == 0;
    public bool Contains(PdfPoint point) => point.X >= X && point.X <= Right && point.Y >= Y && point.Y <= Bottom;
    public bool Intersects(PdfRect other) => !IsEmpty && !other.IsEmpty && X < other.Right && Right > other.X && Y < other.Bottom && Bottom > other.Y;

    public PdfRect Intersect(PdfRect other)
    {
        var x = Math.Max(X, other.X);
        var y = Math.Max(Y, other.Y);
        return new PdfRect(x, y, Math.Max(0, Math.Min(Right, other.Right) - x), Math.Max(0, Math.Min(Bottom, other.Bottom) - y));
    }

    public static PdfRect FromPoints(PdfPoint a, PdfPoint b) =>
        new(Math.Min(a.X, b.X), Math.Min(a.Y, b.Y), Math.Abs(a.X - b.X), Math.Abs(a.Y - b.Y));
}

/// <summary>Converts a raw PDF crop box into normalized viewer coordinates, including /Rotate and /UserUnit.</summary>
public sealed class PdfPageTransform
{
    public PdfPageTransform(PdfRect cropBox, int rotation = 0, double userUnit = 1)
    {
        if (cropBox.IsEmpty || rotation % 90 != 0 || !double.IsFinite(userUnit) || userUnit <= 0)
            throw new ArgumentOutOfRangeException(nameof(rotation));
        CropBox = cropBox;
        Rotation = ((rotation % 360) + 360) % 360;
        UserUnit = userUnit;
        ViewSize = Rotation is 90 or 270
            ? new PdfSize(cropBox.Height * userUnit, cropBox.Width * userUnit)
            : new PdfSize(cropBox.Width * userUnit, cropBox.Height * userUnit);
    }

    public PdfRect CropBox { get; }
    public int Rotation { get; }
    public double UserUnit { get; }
    public PdfSize ViewSize { get; }

    public PdfPoint ToView(PdfPoint pdf)
    {
        var x = pdf.X - CropBox.X;
        var y = pdf.Y - CropBox.Y;
        var point = Rotation switch
        {
            0 => new PdfPoint(x, CropBox.Height - y),
            90 => new PdfPoint(y, x),
            180 => new PdfPoint(CropBox.Width - x, y),
            _ => new PdfPoint(CropBox.Height - y, CropBox.Width - x)
        };
        return new PdfPoint(point.X * UserUnit, point.Y * UserUnit);
    }

    public PdfPoint ToPdf(PdfPoint view)
    {
        var x = view.X / UserUnit;
        var y = view.Y / UserUnit;
        var point = Rotation switch
        {
            0 => new PdfPoint(x, CropBox.Height - y),
            90 => new PdfPoint(y, x),
            180 => new PdfPoint(CropBox.Width - x, y),
            _ => new PdfPoint(CropBox.Width - y, CropBox.Height - x)
        };
        return new PdfPoint(point.X + CropBox.X, point.Y + CropBox.Y);
    }

    public PdfRect ToPdf(PdfRect view) => Map(view, ToPdf);
    public PdfRect ToView(PdfRect pdf) => Map(pdf, ToView);

    private static PdfRect Map(PdfRect rect, Func<PdfPoint, PdfPoint> map)
    {
        var points = new[]
        {
            map(new PdfPoint(rect.X, rect.Y)), map(new PdfPoint(rect.Right, rect.Y)),
            map(new PdfPoint(rect.Right, rect.Bottom)), map(new PdfPoint(rect.X, rect.Bottom))
        };
        return new PdfRect(points.Min(p => p.X), points.Min(p => p.Y),
            points.Max(p => p.X) - points.Min(p => p.X), points.Max(p => p.Y) - points.Min(p => p.Y));
    }
}
