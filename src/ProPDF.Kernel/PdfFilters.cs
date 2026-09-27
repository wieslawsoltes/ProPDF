using System.IO.Compression;

namespace ProPDF.Kernel;

/// <summary>Lossless PDF filters implemented in ProPDF; Flate uses the platform zlib stream.</summary>
public static class PdfFilters
{
    public static byte[] Deflate(ReadOnlySpan<byte> bytes)
    {
        using var output = new MemoryStream();
        using (var compressor = new ZLibStream(output, CompressionLevel.Optimal, true)) compressor.Write(bytes);
        return output.ToArray();
    }
    public static byte[] Decode(PdfStream stream, Func<PdfObject, PdfObject>? resolve = null,
        int maximumBytes = 128 * 1024 * 1024, CancellationToken cancellationToken = default)
    {
        if (maximumBytes < 1) throw new ArgumentOutOfRangeException(nameof(maximumBytes));
        resolve ??= value => value;
        var filter = resolve(stream.Dictionary["Filter"]);
        var names = filter is PdfArray array ? array.Items : filter is PdfNull ? [] : new List<PdfObject> { filter };
        if (names.Count > 16) throw new InvalidDataException("Too many PDF stream filters.");
        var parameters = resolve(stream.Dictionary["DecodeParms"]);
        var bytes = stream.EncodedBytes;
        foreach (var (item, index) in names.Select((item, index) => (item, index)))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var name = resolve(item) as PdfName ?? throw new InvalidDataException("Invalid PDF filter name.");
            var param = resolve(parameters is PdfArray p && index < p.Count ? p[index] : parameters) as PdfDictionary ?? new PdfDictionary();
            bytes = name.Value switch
            {
                "FlateDecode" or "Fl" => Inflate(bytes, maximumBytes, cancellationToken),
                "ASCIIHexDecode" or "AHx" => AsciiHex(bytes, maximumBytes),
                "ASCII85Decode" or "A85" => Ascii85(bytes, maximumBytes),
                "RunLengthDecode" or "RL" => RunLength(bytes, maximumBytes),
                "LZWDecode" or "LZW" => Lzw(bytes, (int)param.Number("EarlyChange", 1), maximumBytes, cancellationToken),
                _ => throw new NotSupportedException($"Filter /{name.Value} is preserved as encoded data but cannot be decoded by this lossless filter service.")
            };
            if (name.Value is "FlateDecode" or "Fl" or "LZWDecode" or "LZW") bytes = Predictor(bytes, param, maximumBytes);
            Check(bytes.Length, maximumBytes);
        }
        Check(bytes.Length, maximumBytes);
        return (byte[])bytes.Clone();
    }
    private static byte[] Inflate(byte[] bytes, int maximum, CancellationToken token)
    {
        using var input = new MemoryStream(bytes, false); using var decoder = new ZLibStream(input, CompressionMode.Decompress);
        using var output = new MemoryStream(); var buffer = new byte[81920];
        while (true)
        {
            token.ThrowIfCancellationRequested(); var count = decoder.Read(buffer); if (count == 0) break;
            Check(output.Length + count, maximum); output.Write(buffer, 0, count);
        }
        return output.ToArray();
    }
    private static byte[] AsciiHex(byte[] bytes, int maximum)
    {
        using var output = new MemoryStream(); var high = -1; var terminated = false;
        foreach (var value in bytes)
        {
            if (value == '>') { terminated = true; break; }
            if (PdfSyntaxReader.IsWhite(value)) continue;
            var digit = PdfSyntaxReader.Hex(value);
            if (high < 0) high = digit; else { output.WriteByte((byte)(high * 16 + digit)); high = -1; }
            Check(output.Length, maximum);
        }
        if (!terminated) throw new InvalidDataException("ASCIIHex stream has no end marker.");
        if (high >= 0) output.WriteByte((byte)(high * 16)); Check(output.Length, maximum);
        return output.ToArray();
    }
    private static byte[] Ascii85(byte[] bytes, int maximum)
    {
        using var output = new MemoryStream(); ulong group = 0; var count = 0; var terminated = false;
        void Emit(int length)
        {
            if (group > uint.MaxValue) throw new InvalidDataException("Overflow in ASCII85 group.");
            Check(output.Length + length, maximum);
            for (var j = 0; j < length; j++) output.WriteByte((byte)(group >> (24 - 8 * j)));
            group = 0; count = 0;
        }
        for (var i = 0; i < bytes.Length; i++)
        {
            var b = bytes[i]; if (PdfSyntaxReader.IsWhite(b)) continue;
            if (b == '~')
            {
                if (++i >= bytes.Length || bytes[i] != '>') throw new InvalidDataException("Invalid ASCII85 end marker.");
                terminated = true; break;
            }
            if (b == 'z') { if (count != 0) throw new InvalidDataException("ASCII85 z inside group."); Emit(4); continue; }
            if (b is < 33 or > 117) throw new InvalidDataException("Invalid ASCII85 character.");
            group = group * 85 + b - 33; if (++count == 5) Emit(4);
        }
        if (!terminated || count == 1) throw new InvalidDataException("Truncated ASCII85 stream.");
        if (count > 1) { var length = count - 1; while (count++ < 5) group = group * 85 + 84; Emit(length); }
        return output.ToArray();
    }
    private static byte[] RunLength(byte[] bytes, int maximum)
    {
        using var output = new MemoryStream();
        for (var i = 0; i < bytes.Length;)
        {
            var n = bytes[i++]; if (n == 128) return output.ToArray();
            var length = n < 128 ? n + 1 : 257 - n; Check(output.Length + length, maximum);
            if (n < 128) { if (i + length > bytes.Length) throw new InvalidDataException("Truncated RunLength literal."); output.Write(bytes, i, length); i += length; }
            else { if (i >= bytes.Length) throw new InvalidDataException("Truncated RunLength repetition."); for (var j = 0; j < length; j++) output.WriteByte(bytes[i]); i++; }
        }
        throw new InvalidDataException("RunLength stream has no end marker.");
    }
    private static byte[] Lzw(byte[] bytes, int early, int maximum, CancellationToken token)
    {
        if (early is not (0 or 1)) throw new InvalidDataException("Invalid LZW EarlyChange.");
        var dictionary = new byte[4096][]; for (var i = 0; i < 256; i++) dictionary[i] = [(byte)i];
        var width = 9; var next = 258; long position = 0; byte[]? previous = null;
        using var output = new MemoryStream();
        while (position + width <= bytes.LongLength * 8)
        {
            token.ThrowIfCancellationRequested(); var code = 0;
            for (var i = 0; i < width; i++, position++) code = (code << 1) | ((bytes[position >> 3] >> (7 - (int)(position & 7))) & 1);
            if (code == 257) return output.ToArray();
            if (code == 256) { width = 9; next = 258; previous = null; continue; }
            byte[] entry;
            if (code < next && dictionary[code] is { } known) entry = known;
            else if (code == next && previous is not null) entry = [.. previous, previous[0]];
            else throw new InvalidDataException("Invalid LZW code.");
            Check(output.Length + entry.Length, maximum); output.Write(entry);
            if (previous is not null && next < 4096)
            {
                dictionary[next++] = [.. previous, entry[0]];
                if (width < 12 && next + early == 1 << width) width++;
            }
            previous = entry;
        }
        throw new InvalidDataException("Truncated LZW stream.");
    }
    private static byte[] Predictor(byte[] bytes, PdfDictionary parameters, int maximum)
    {
        var predictor = (int)parameters.Number("Predictor", 1); if (predictor == 1) return bytes;
        var colors = (int)parameters.Number("Colors", 1); var bits = (int)parameters.Number("BitsPerComponent", 8); var columns = (int)parameters.Number("Columns", 1);
        if (colors < 1 || colors > 32 || columns < 1 || bits is not (1 or 2 or 4 or 8 or 16)) throw new InvalidDataException("Invalid predictor dimensions.");
        var rowLong = ((long)colors * bits * columns + 7) / 8; Check(rowLong, maximum); var row = (int)rowLong;
        if (predictor == 2)
        {
            if (bytes.Length % row != 0) throw new InvalidDataException("Truncated TIFF predictor row.");
            var mask = (1 << bits) - 1;
            for (var offset = 0; offset < bytes.Length; offset += row)
                for (var component = colors; component < columns * colors; component++)
                {
                    int Read(int index)
                    {
                        var value = 0;
                        for (var b = 0; b < bits; b++) { var p = index * bits + b; value = (value << 1) | ((bytes[offset + p / 8] >> (7 - p % 8)) & 1); }
                        return value;
                    }
                    var value = (Read(component) + Read(component - colors)) & mask;
                    for (var b = 0; b < bits; b++)
                    {
                        var p = component * bits + b; var bit = 1 << (7 - p % 8);
                        bytes[offset + p / 8] = (byte)((bytes[offset + p / 8] & ~bit) | (((value >> (bits - b - 1)) & 1) * bit));
                    }
                }
            return bytes;
        }
        if (predictor is < 10 or > 15 || bytes.Length % (row + 1) != 0) throw new InvalidDataException("Invalid PNG predictor row.");
        var rows = bytes.Length / (row + 1); Check((long)row * rows, maximum); var result = new byte[row * rows]; var bpp = Math.Max(1, (colors * bits + 7) / 8);
        for (var y = 0; y < rows; y++)
        {
            var filter = bytes[y * (row + 1)]; if (filter > 4) throw new InvalidDataException("Invalid PNG predictor filter.");
            for (var x = 0; x < row; x++)
            {
                var left = x < bpp ? 0 : result[y * row + x - bpp]; var up = y == 0 ? 0 : result[(y - 1) * row + x];
                var corner = y == 0 || x < bpp ? 0 : result[(y - 1) * row + x - bpp];
                var estimate = filter switch { 0 => 0, 1 => left, 2 => up, 3 => (left + up) / 2, _ => Paeth(left, up, corner) };
                result[y * row + x] = (byte)(bytes[y * (row + 1) + x + 1] + estimate);
            }
        }
        return result;
    }
    private static int Paeth(int a, int b, int c)
    { var p = a + b - c; var pa = Math.Abs(p - a); var pb = Math.Abs(p - b); var pc = Math.Abs(p - c); return pa <= pb && pa <= pc ? a : pb <= pc ? b : c; }
    private static void Check(long length, long maximum) { if (length < 0 || length > maximum) throw new InvalidDataException("Decoded PDF stream exceeds configured byte budget."); }
}
