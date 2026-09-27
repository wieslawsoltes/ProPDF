using System.Buffers.Binary;
using System.Runtime.CompilerServices;
using System.Text;
using ProPDF.Core;
using ProPDF.Kernel;
using static ProPDF.Kernel.PdfValues;

namespace ProPDF.Editing;

internal sealed record OwnedFont(PdfReference Reference, Func<string, byte[]> Encode);

/// <summary>Own TrueType cmap/hmtx reader and PDF Type0/CIDFont/ToUnicode writer. Full font embedding, not outline substitution.</summary>
internal static class PdfFonts
{
    private static readonly ConditionalWeakTable<PdfGraph, Dictionary<string, OwnedFont>> Cache = new();
    public static OwnedFont Create(PdfGraph graph, string name, PdfBinaryAsset? embedded, string text)
    {
        if (embedded is null)
        {
            var cache = Cache.GetOrCreateValue(graph);
            if (cache.TryGetValue(name, out var known)) { _ = known.Encode(text); return known; }
            if (name is not ("Helvetica" or "Helvetica-Bold" or "Helvetica-Oblique" or "Helvetica-BoldOblique" or
                "Times-Roman" or "Times-Bold" or "Times-Italic" or "Times-BoldItalic" or "Courier" or "Courier-Bold" or "Courier-Oblique" or "Courier-BoldOblique"))
                throw new NotSupportedException("Use a Standard 14 Latin font or supply a TrueType font asset. Symbol/Zapf encoding needs a dedicated mapping.");
            var dictionary = Dictionary(("Type", new PdfName("Font")), ("Subtype", new PdfName("Type1")), ("BaseFont", new PdfName(name)), ("Encoding", new PdfName("WinAnsiEncoding")));
            var result = new OwnedFont(graph.File.Add(dictionary), EncodeLatin); _ = result.Encode(text); cache[name] = result; return result;
        }
        var bytes = embedded.ToArray(); var ttf = new TrueType(bytes);
        var characters = text.EnumerateRunes().Where(r => r.Value is not (10 or 13)).Select(r => r.Value).Distinct().ToArray();
        if (characters.Length > 65534) throw new ArgumentOutOfRangeException(nameof(text), "Too many distinct glyphs for one embedded font.");
        var ids = characters.Select((r, i) => (r, id: i + 1)).ToDictionary(x => x.r, x => x.id);
        var glyphMap = new byte[(characters.Length + 1) * 2]; var widths = new PdfArray();
        var mappings = new List<string>();
        foreach (var codepoint in characters)
        {
            var glyph = ttf.Glyph(codepoint); if (glyph == 0) throw new NotSupportedException($"Embedded font has no glyph for U+{codepoint:X4}.");
            var cid = ids[codepoint]; BinaryPrimitives.WriteUInt16BigEndian(glyphMap.AsSpan(cid * 2), glyph);
            widths.Items.Add(new PdfNumber(cid)); widths.Items.Add(Numbers(ttf.Width(glyph)));
            mappings.Add($"<{cid:X4}> <{Convert.ToHexString(Encoding.BigEndianUnicode.GetBytes(char.ConvertFromUtf32(codepoint)))}>\n");
        }
        var fontName = "ProPDFFont" + Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(bytes))[..12];
        var program = graph.File.Add(PdfStream.FromDecoded(bytes, Dictionary(("Length1", new PdfNumber(bytes.Length)))));
        var descriptor = graph.File.Add(Dictionary(("Type", new PdfName("FontDescriptor")), ("FontName", new PdfName(fontName)), ("Flags", new PdfNumber(32L)),
            ("FontBBox", Numbers(ttf.XMin, ttf.YMin, ttf.XMax, ttf.YMax)), ("ItalicAngle", new PdfNumber(0L)),
            ("Ascent", new PdfNumber(ttf.Ascent)), ("Descent", new PdfNumber(ttf.Descent)), ("CapHeight", new PdfNumber(ttf.Ascent)),
            ("StemV", new PdfNumber(80L)), ("FontFile2", program)));
        var cidFont = graph.File.Add(Dictionary(("Type", new PdfName("Font")), ("Subtype", new PdfName("CIDFontType2")), ("BaseFont", new PdfName(fontName)),
            ("CIDSystemInfo", Dictionary(("Registry", new PdfString("Adobe")), ("Ordering", new PdfString("Identity")), ("Supplement", new PdfNumber(0L)))),
            ("FontDescriptor", descriptor), ("DW", new PdfNumber(1000L)), ("W", widths), ("CIDToGIDMap", graph.File.Add(PdfStream.FromDecoded(glyphMap)))));
        var cmap = new StringBuilder("/CIDInit /ProcSet findresource begin\n12 dict begin\nbegincmap\n/CIDSystemInfo << /Registry (Adobe) /Ordering (UCS) /Supplement 0 >> def\n/CMapName /ProPDFUnicode def\n/CMapType 2 def\n1 begincodespacerange\n<0000> <FFFF>\nendcodespacerange\n");
        foreach (var group in mappings.Chunk(100)) { cmap.Append(group.Length).Append(" beginbfchar\n"); foreach (var mapping in group) cmap.Append(mapping); cmap.Append("endbfchar\n"); }
        cmap.Append("endcmap\nCMapName currentdict /CMap defineresource pop\nend\nend\n");
        var font = graph.File.Add(Dictionary(("Type", new PdfName("Font")), ("Subtype", new PdfName("Type0")), ("BaseFont", new PdfName(fontName)), ("Encoding", new PdfName("Identity-H")),
            ("DescendantFonts", new PdfArray(cidFont)), ("ToUnicode", graph.File.Add(PdfStream.FromDecoded(Encoding.ASCII.GetBytes(cmap.ToString()))))));
        byte[] Encode(string value)
        {
            using var output = new MemoryStream();
            foreach (var rune in value.EnumerateRunes())
            {
                if (rune.Value is 10 or 13) continue;
                if (!ids.TryGetValue(rune.Value, out var cid)) throw new ArgumentException("Text uses a character not mapped by this font instance.");
                output.WriteByte((byte)(cid >> 8)); output.WriteByte((byte)cid);
            }
            return output.ToArray();
        }
        return new OwnedFont(font, Encode);
    }
    public static byte[] EncodeLatin(string text)
    {
        var special = "€\uFFFD‚ƒ„…†‡ˆ‰Š‹Œ\uFFFDŽ\uFFFD\uFFFD‘’“”•–—˜™š›œ\uFFFDžŸ";
        using var output = new MemoryStream();
        foreach (var rune in text.EnumerateRunes())
        {
            if (rune.Value is 10 or 13) continue;
            if (rune.Value is >= 32 and <= 126 or >= 160 and <= 255) output.WriteByte((byte)rune.Value);
            else if (rune.Value <= 65535 && rune.Value != 0xfffd && special.IndexOf((char)rune.Value) is var index && index >= 0) output.WriteByte((byte)(128 + index));
            else throw new NotSupportedException($"Standard font cannot encode U+{rune.Value:X4}; supply a suitable embeddable TrueType font.");
        }
        return output.ToArray();
    }
    internal sealed class TrueType
    {
        private readonly byte[] _bytes;
        private readonly Dictionary<string, (int Offset, int Length)> _tables = [];
        private readonly int _units;
        private readonly int _metrics;
        private readonly int _glyphs;
        private readonly int _cmap;
        private readonly int _cmapLength;
        private readonly int _format;
        public TrueType(byte[] bytes)
        {
            _bytes = bytes;
            if (bytes.Length < 12 || U32(0) is not (0x00010000 or 0x74727565)) throw new NotSupportedException("Only standalone TrueType-outline fonts are supported; CFF/collections require another embedding implementation.");
            var count = U16(4); if (12 + count * 16 > bytes.Length) throw new InvalidDataException("Truncated font directory.");
            for (var i = 0; i < count; i++)
            {
                var position = 12 + i * 16; var tag = Encoding.ASCII.GetString(bytes, position, 4); var offset = checked((int)U32(position + 8)); var length = checked((int)U32(position + 12));
                if (offset < 0 || length < 0 || (long)offset + length > bytes.Length || !_tables.TryAdd(tag, (offset, length))) throw new InvalidDataException("Invalid font table.");
            }
            var head = Table("head", 54); _units = U16(head + 18); if (_units < 16) throw new InvalidDataException("Invalid font em size.");
            var hhea = Table("hhea", 36); _metrics = U16(hhea + 34); _glyphs = U16(Table("maxp", 6) + 4);
            if (_metrics < 1 || _metrics > _glyphs) throw new InvalidDataException("Invalid font horizontal metrics.");
            Table("hmtx", _metrics * 4 + (_glyphs - _metrics) * 2);
            if (_tables.ContainsKey("OS/2"))
            {
                var flags = U16(Table("OS/2", 10) + 8);
                if ((flags & 0x202) != 0) throw new NotSupportedException("Font embedding flags prohibit scalable document embedding.");
            }
            XMin = Scale(I16(head + 36)); YMin = Scale(I16(head + 38)); XMax = Scale(I16(head + 40)); YMax = Scale(I16(head + 42));
            Ascent = Scale(I16(hhea + 4)); Descent = Scale(I16(hhea + 6));
            var cmap = Table("cmap", 4); var subCount = U16(cmap + 2); if (4 + subCount * 8 > _tables["cmap"].Length) throw new InvalidDataException("Invalid cmap directory.");
            var chosen = -1; var best = -1;
            for (var i = 0; i < subCount; i++)
            {
                var entry = cmap + 4 + i * 8; var platform = U16(entry); var encoding = U16(entry + 2); var relative = checked((int)U32(entry + 4));
                if (relative < 0 || relative + 2 > _tables["cmap"].Length) throw new InvalidDataException("cmap offset outside table.");
                var offset = cmap + relative; var format = U16(offset);
                var score = format == 12 && (platform == 0 || platform == 3 && encoding == 10) ? 3 : format == 4 && (platform == 0 || platform == 3 && encoding == 1) ? 2 : -1;
                if (score > best) { best = score; chosen = offset; }
            }
            if (chosen < 0) throw new NotSupportedException("Font has no supported Unicode cmap.");
            _cmap = chosen; _format = U16(chosen); _cmapLength = checked((int)(_format == 12 ? U32(chosen + 4) : U16(chosen + 2)));
            if (_cmapLength < 16 || (long)_cmap + _cmapLength > (long)cmap + _tables["cmap"].Length) throw new InvalidDataException("Invalid cmap length.");
        }
        public double XMin { get; } public double YMin { get; } public double XMax { get; } public double YMax { get; }
        public double Ascent { get; } public double Descent { get; }
        private double Scale(int value) => value * 1000d / _units;
        public double Width(ushort glyph)
        { if (glyph >= _glyphs) throw new InvalidDataException("Glyph outside font."); return Scale(U16(Table("hmtx", 4) + Math.Min(glyph, _metrics - 1) * 4)); }
        public ushort Glyph(int codepoint)
        {
            if (_format == 12)
            {
                var count = U32(_cmap + 12); if (16L + count * 12L > _cmapLength) throw new InvalidDataException("Truncated format-12 cmap.");
                long low = 0, high = count;
                while (low < high)
                {
                    var middle = (low + high) / 2; var entry = checked(_cmap + 16 + (int)middle * 12); var start = U32(entry); var end = U32(entry + 4);
                    if (start > end) throw new InvalidDataException("Invalid cmap range.");
                    if (codepoint < start) high = middle; else if (codepoint > end) low = middle + 1;
                    else { var glyph = U32(entry + 8) + (uint)codepoint - start; return glyph < _glyphs ? (ushort)glyph : throw new InvalidDataException("cmap glyph outside font."); }
                }
                return 0;
            }
            if (codepoint > 65535) return 0;
            var segments = U16(_cmap + 6) / 2; if (16 + segments * 8 > _cmapLength) throw new InvalidDataException("Truncated format-4 cmap.");
            for (var i = 0; i < segments; i++)
            {
                var end = U16(_cmap + 14 + i * 2); if (codepoint > end) continue;
                var start = U16(_cmap + 16 + segments * 2 + i * 2); if (codepoint < start) return 0;
                var delta = I16(_cmap + 16 + segments * 4 + i * 2); var rangePosition = _cmap + 16 + segments * 6 + i * 2; var range = U16(rangePosition);
                var glyph = range == 0 ? (codepoint + delta) & 65535 : 0;
                if (range != 0)
                {
                    var position = rangePosition + range + (codepoint - start) * 2;
                    if (position < _cmap || position + 2 > _cmap + _cmapLength) throw new InvalidDataException("cmap glyph offset outside table.");
                    glyph = U16(position); if (glyph != 0) glyph = (glyph + delta) & 65535;
                }
                return glyph < _glyphs ? (ushort)glyph : throw new InvalidDataException("cmap glyph outside font.");
            }
            return 0;
        }
        private int Table(string name, int minimum) => _tables.TryGetValue(name, out var value) && value.Length >= minimum ? value.Offset : throw new InvalidDataException("Missing or short TrueType table " + name);
        private ushort U16(int offset) => offset >= 0 && offset + 2L <= _bytes.Length ? BinaryPrimitives.ReadUInt16BigEndian(_bytes.AsSpan(offset, 2)) : throw new InvalidDataException("Truncated font.");
        private short I16(int offset) => unchecked((short)U16(offset));
        private uint U32(int offset) => offset >= 0 && offset + 4L <= _bytes.Length ? BinaryPrimitives.ReadUInt32BigEndian(_bytes.AsSpan(offset, 4)) : throw new InvalidDataException("Truncated font.");
    }
}
