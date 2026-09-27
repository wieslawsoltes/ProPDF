using System.Runtime.InteropServices;
using ProPDF.Core;
using ProPDF.Kernel;
using SkiaSharp;
using static ProPDF.Kernel.PdfValues;

namespace ProPDF.Editing;

public sealed partial class ManagedPdfEditor
{
    /// <summary>Decode a bounded static raster to sRGB samples and an owned PDF soft mask. Does not share mutable resources.</summary>
    private static PdfReference CreateImageResource(PdfGraph graph, PdfBinaryAsset image, bool interpolate, CancellationToken token)
    {
        ArgumentNullException.ThrowIfNull(image);
        token.ThrowIfCancellationRequested();
        using var data = SKData.CreateCopy(image.ToArray());
        using var codec = SKCodec.Create(data) ?? throw new InvalidDataException("Unsupported or invalid image.");
        var info = codec.Info;
        if (info.Width <= 0 || info.Height <= 0 || (long)info.Width * info.Height > 24L * 1024 * 1024)
            throw new InvalidDataException("Image exceeds the 24-megapixel decode budget.");
        if (codec.FrameCount > 1) throw new NotSupportedException("Choose a static image; animated images are not silently reduced to one frame.");
        using var colorSpace = SKColorSpace.CreateSrgb();
        using var bitmap = new SKBitmap(new SKImageInfo(info.Width, info.Height, SKColorType.Rgba8888, SKAlphaType.Unpremul, colorSpace));
        if (bitmap.GetPixels() == IntPtr.Zero) throw new InvalidOperationException("Unable to allocate the image buffer.");
        if (codec.GetPixels(bitmap.Info, bitmap.GetPixels(), bitmap.RowBytes, SKCodecOptions.Default) != SKCodecResult.Success)
            throw new InvalidDataException("Image decoding was incomplete.");
        token.ThrowIfCancellationRequested();
        var origin = (int)codec.EncodedOrigin;
        if (origin is < 1 or > 8) throw new InvalidDataException("Unsupported encoded image orientation.");
        var width = origin >= 5 ? info.Height : info.Width;
        var height = origin >= 5 ? info.Width : info.Height;
        var rgb = new byte[checked(width * height * 3)];
        var alpha = new byte[checked(width * height)];
        // Copy one native row at a time instead of allocating a second full RGBA image.
        var row = new byte[checked(info.Width * 4)];
        var opaque = true;
        for (var y = 0; y < info.Height; y++)
        {
            token.ThrowIfCancellationRequested();
            Marshal.Copy(IntPtr.Add(bitmap.GetPixels(), checked(y * bitmap.RowBytes)), row, 0, row.Length);
            for (var x = 0; x < info.Width; x++)
            {
                var (dx, dy) = origin switch
                {
                    1 => (x, y), 2 => (info.Width - 1 - x, y), 3 => (info.Width - 1 - x, info.Height - 1 - y),
                    4 => (x, info.Height - 1 - y), 5 => (y, x), 6 => (info.Height - 1 - y, x),
                    7 => (info.Height - 1 - y, info.Width - 1 - x), _ => (y, info.Width - 1 - x)
                };
                var target = dy * width + dx;
                rgb[target * 3] = row[x * 4]; rgb[target * 3 + 1] = row[x * 4 + 1]; rgb[target * 3 + 2] = row[x * 4 + 2];
                alpha[target] = row[x * 4 + 3]; opaque &= alpha[target] == 255;
            }
        }
        var dictionary = Dictionary(("Type", new PdfName("XObject")), ("Subtype", new PdfName("Image")),
            ("Width", new PdfNumber(width)), ("Height", new PdfNumber(height)), ("BitsPerComponent", new PdfNumber(8)),
            ("ColorSpace", new PdfName("DeviceRGB")), ("Interpolate", new PdfBoolean(interpolate)));
        if (!opaque)
        {
            var mask = dictionary.Copy(); mask["ColorSpace"] = new PdfName("DeviceGray");
            dictionary["SMask"] = graph.File.Add(PdfStream.FromDecoded(alpha, mask));
        }
        token.ThrowIfCancellationRequested();
        return graph.File.Add(PdfStream.FromDecoded(rgb, dictionary));
    }
}
