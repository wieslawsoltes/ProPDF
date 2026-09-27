using System.Buffers.Binary;
using System.IO.Compression;
using System.Text;
using ProPDF.Core;
using ProPDF.Editing;
using ProPDF.Engine.PdfPig;
using ProPDF.Kernel;
using Xunit;
using static ProPDF.Kernel.PdfValues;

namespace ProPDF.Tests;

public sealed class KernelTests
{
    [Fact]
    public void ReachabilityWriterLinksFreeEntriesAndAdvancesRemovedGenerations()
    {
        var file = PdfFile.Create();
        var removed = file.Add(new PdfString("unreachable"));
        file.Catalog["Temporary"] = removed;
        var loaded = PdfFile.Open(file.Save());
        loaded.Catalog.Remove("Temporary");
        var bytes = loaded.Save(incremental: true);
        var text = Encoding.Latin1.GetString(bytes);
        var tail = text[(text.LastIndexOf("\nxref\n", StringComparison.Ordinal) + 1)..];
        var rows = tail.Split('\n');
        Assert.StartsWith(removed.Id.Number.ToString("D10") + " 65535 f", rows[2]);
        Assert.StartsWith("0000000000 00001 f", rows[2 + removed.Id.Number]);
        Assert.IsType<PdfNull>(PdfFile.Open(bytes).Resolve(removed));
    }

    [Fact]
    public void LexerPreservesEscapesBinaryStringsAndExactLargeIntegers()
    {
        var reader = new PdfSyntaxReader(Encoding.ASCII.GetBytes("% comment\n << /A#20B (first\\(x\\)\\n\\101\\777) /Odd <ABC> /Number 9223372036854775807 /Ref 12 7 R /Values [true false null -0.125 +17] >>"));
        var d = Assert.IsType<PdfDictionary>(reader.ReadObject()); Assert.True(reader.End);
        Assert.Equal(Encoding.Latin1.GetBytes("first(x)\nAÿ"), Assert.IsType<PdfString>(d["A B"]).ToArray());
        Assert.Equal(new byte[] { 0xab, 0xc0 }, Assert.IsType<PdfString>(d["Odd"]).ToArray());
        Assert.Equal(long.MaxValue, Assert.IsType<PdfNumber>(d["Number"]).Integer);
        Assert.Equal(new PdfObjectId(12, 7), Assert.IsType<PdfReference>(d["Ref"]).Id);
        var copy = Assert.IsType<PdfDictionary>(new PdfSyntaxReader(PdfObjectWriter.Serialize(d)).ReadObject());
        Assert.Equal(long.MaxValue, Assert.IsType<PdfNumber>(copy["Number"]).Integer);
        Assert.Equal(-0.125, Assert.IsType<PdfNumber>(Assert.IsType<PdfArray>(copy["Values"])[3]).Value);
    }

    [Theory]
    [InlineData("<< /A 1 /A 2 >>")]
    [InlineData("(unclosed")]
    [InlineData("<AZ>")]
    [InlineData("/Invalid#0Z")]
    [InlineData("1e3")]
    [InlineData("NaN")]
    [InlineData("[1 2")]
    public void MalformedObjectsAreRejected(string text) => Assert.ThrowsAny<Exception>(() => new PdfSyntaxReader(Encoding.ASCII.GetBytes(text)).ReadObject());

    [Fact]
    public void LexerDepthAndTokenBudgetsAreEnforced()
    {
        Assert.Throws<InvalidDataException>(() => new PdfSyntaxReader(Encoding.ASCII.GetBytes("[[[[0]]]]"), limits: new PdfReadLimits(MaximumDepth: 2)).ReadObject());
        Assert.Throws<InvalidDataException>(() => new PdfSyntaxReader(Encoding.ASCII.GetBytes("(abcdef)"), limits: new PdfReadLimits(MaximumTokenBytes: 3)).ReadObject());
        Assert.ThrowsAny<OperationCanceledException>(() => new PdfSyntaxReader("[1]"u8.ToArray(), cancellationToken: new CancellationToken(true)).ReadObject());
    }

    [Fact]
    public void ContentTokenizerDoesNotMistakeStringsCommentsOrNamesForOperators()
    {
        var data = Encoding.ASCII.GetBytes("BT /Font#31 12 Tf (Tj % not operator) Tj [(left) -25 (right)] TJ ET % ignored\n");
        var instructions = PdfContent.Read(data);
        Assert.Equal(new[] { "BT", "Tf", "Tj", "TJ", "ET" }, instructions.Select(i => i.Operator));
        Assert.Equal("Font1", Assert.IsType<PdfName>(instructions[1].Operands[0]).Value);
        Assert.Equal("Tj % not operator", Assert.IsType<PdfString>(instructions[2].Operands[0]).Text);
        Assert.Equal(instructions.Select(i => i.Operator), PdfContent.Read(PdfContent.Write(instructions)).Select(i => i.Operator));
        Assert.Throws<NotSupportedException>(() => PdfContent.Read("BI /W 1 /H 1 ID x EI"u8));
        Assert.Throws<InvalidDataException>(() => PdfContent.Read("1 2"u8));
        Assert.Throws<InvalidDataException>(() => PdfContent.Read("q Q"u8, new PdfContentLimits(MaximumInstructions: 1)));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task XrefStreamsObjectStreamsAndHybridFilesLoadAndRewrite(bool hybrid)
    {
        var bytes = CompressedFixture(hybrid);
        var file = PdfFile.Open(bytes);
        Assert.Equal("Catalog", file.Catalog.Name("Type"));
        Assert.Equal("Page", file.Dictionary(new PdfReference(3)).Name("Type"));
        using var input = new MemoryStream(bytes); var own = await new ManagedPdfLoader().OpenAsync(input);
        Assert.Single(own.Pages); Assert.Equal(200, own.Pages[0].Size.Width);
        var rewritten = file.Save();
        using var verify = new MemoryStream(rewritten); var independent = await new PdfPigBackend().OpenAsync(verify);
        Assert.Equal(own.Pages, independent.Pages);
        Assert.Equal("Catalog", PdfFile.Open(rewritten).Catalog.Name("Type"));
    }

    [Fact]
    public void IncrementalFreeEntriesOverrideOlderLiveObjectsAndInheritedTrailers()
    {
        var initial = RawFixture(new Dictionary<int, byte[]>
        {
            [1] = "<< /Type /Catalog /Pages 2 0 R >>"u8.ToArray(),
            [2] = "<< /Type /Pages /Kids [3 0 R] /Count 1 >>"u8.ToArray(),
            [3] = "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 100 100] >>"u8.ToArray(),
            [4] = "(deleted secret)"u8.ToArray()
        });
        var original = PdfFile.Open(initial);
        using var output = new MemoryStream(); output.Write(initial); var xref = output.Position;
        Write(output, $"xref\n4 1\n0000000000 00001 f \ntrailer\n<< /Size 5 /Prev {original.LastCrossReferenceOffset} >>\nstartxref\n{xref}\n%%EOF\n");
        var updated = PdfFile.Open(output.ToArray());
        Assert.IsType<PdfNull>(updated.Resolve(new PdfReference(4)));
        Assert.Equal("Catalog", updated.Catalog.Name("Type")); Assert.Equal(2, updated.RevisionCount);
        Assert.DoesNotContain("deleted secret", Encoding.ASCII.GetString(updated.Save()));
    }

    [Fact]
    public void CrossReferenceCyclesWrongOffsetsAndStreamLengthMismatchesFail()
    {
        using var bytes = new MemoryStream(); Write(bytes, "%PDF-1.7\n"); var xref = bytes.Position;
        Write(bytes, $"xref\n0 1\n0000000000 65535 f \ntrailer\n<< /Size 1 /Prev {xref} >>\nstartxref\n{xref}\n%%EOF\n");
        Assert.Throws<InvalidDataException>(() => PdfFile.Open(bytes.ToArray()));
        var badLength = RawFixture(new Dictionary<int, byte[]>
        {
            [1] = "<< /Type /Catalog /Pages 2 0 R /Extra 4 0 R >>"u8.ToArray(),
            [2] = "<< /Type /Pages /Kids [] /Count 0 >>"u8.ToArray(),
            [4] = "<< /Length 2 >>\nstream\nabcdef\nendstream"u8.ToArray()
        });
        Assert.Throws<InvalidDataException>(() => PdfFile.Open(badLength).Resolve(new PdfReference(4)));
        var badOffset = Encoding.ASCII.GetString(badLength).Replace("0000000009 00000 n", "0000000008 00000 n", StringComparison.Ordinal);
        Assert.ThrowsAny<Exception>(() => PdfFile.Open(Encoding.ASCII.GetBytes(badOffset)));
    }

    [Fact]
    public void EncodedStreamsAreLengthDelimitedNotRegexScanned()
    {
        var payload = "binary\nendstream\n99 0 obj\ntrailer\n%%EOF\0tail"u8.ToArray();
        var file = PdfFile.Create(); file.Catalog["Payload"] = file.Add(new PdfStream(new PdfDictionary(), payload));
        var reopened = PdfFile.Open(file.Save()); var stream = Assert.IsType<PdfStream>(reopened.Resolve(reopened.Catalog["Payload"]));
        Assert.Equal(payload, stream.EncodedBytes);
    }

    [Fact]
    public void IndirectStreamLengthResolvesButLengthCyclesFail()
    {
        var objects = new Dictionary<int, byte[]>
        {
            [1] = "<< /Type /Catalog /Pages 2 0 R /Extra 3 0 R >>"u8.ToArray(),
            [2] = "<< /Type /Pages /Kids [] /Count 0 >>"u8.ToArray(),
            [3] = "<< /Length 4 0 R >>\nstream\nabc\nendstream"u8.ToArray(),
            [4] = "3"u8.ToArray()
        };
        var file = PdfFile.Open(RawFixture(objects)); Assert.Equal("abc"u8.ToArray(), Assert.IsType<PdfStream>(file.Resolve(new PdfReference(3))).EncodedBytes);
        objects[4] = "<< /Length 3 0 R >>\nstream\nx\nendstream"u8.ToArray();
        Assert.Throws<InvalidDataException>(() => PdfFile.Open(RawFixture(objects)).Resolve(new PdfReference(3)));
    }

    [Theory]
    [InlineData("ASCIIHexDecode", "61 62 6>", "ab`")]
    [InlineData("ASCII85Decode", "87cURD_*#TDfTZ)+T~>", "Hello, world!")]
    public void TextualStreamFiltersDecodeKnownVectors(string filter, string encoded, string expected)
    {
        var stream = new PdfStream(Dictionary(("Filter", new PdfName(filter))), Encoding.ASCII.GetBytes(encoded));
        Assert.Equal(Encoding.ASCII.GetBytes(expected), PdfFilters.Decode(stream));
    }

    [Fact]
    public void FilterChainsRunLengthAndLzwHaveExplicitBudgets()
    {
        var bytes = "ProPDF chained stream data"u8.ToArray();
        var flate = PdfFilters.Deflate(bytes); var hex = Encoding.ASCII.GetBytes(Convert.ToHexString(flate) + ">");
        Assert.Equal(bytes, PdfFilters.Decode(new PdfStream(Dictionary(("Filter", new PdfArray(new PdfName("ASCIIHexDecode"), new PdfName("FlateDecode")))), hex)));
        Assert.Throws<InvalidDataException>(() => PdfFilters.Decode(PdfStream.FromDecoded(new byte[100_000]), maximumBytes: 1024));
        Assert.Equal("abcxxxxx"u8.ToArray(), PdfFilters.Decode(new PdfStream(Dictionary(("Filter", new PdfName("RunLengthDecode"))), new byte[] { 2, 97, 98, 99, 252, 120, 128 })));
        foreach (var early in new[] { 0, 1 })
        {
            var sample = Enumerable.Range(0, 1200).Select(i => (byte)(i * 17)).ToArray();
            var encoded = EncodeLiteralLzw(sample, early);
            var stream = new PdfStream(Dictionary(("Filter", new PdfName("LZWDecode")), ("DecodeParms", Dictionary(("EarlyChange", new PdfNumber(early))))), encoded);
            Assert.Equal(sample, PdfFilters.Decode(stream));
        }
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(4)]
    [InlineData(8)]
    [InlineData(16)]
    public void TiffPredictionSupportsPackedSamples(int bits)
    {
        const int columns = 7, colors = 3; var mask = (1 << bits) - 1;
        var samples = Enumerable.Range(0, columns * colors).Select(i => (i * i + 3 * i) & mask).ToArray();
        byte[] Pack(int[] values)
        {
            var bytes = new byte[(values.Length * bits + 7) / 8];
            for (var i = 0; i < values.Length; i++) for (var b = 0; b < bits; b++) bytes[(i * bits + b) / 8] |= (byte)(((values[i] >> (bits - b - 1)) & 1) << (7 - (i * bits + b) % 8));
            return bytes;
        }
        var predicted = samples.Select((value, i) => (value - (i < colors ? 0 : samples[i - colors])) & mask).ToArray();
        var stream = PdfStream.FromDecoded(Pack(predicted)); stream.Dictionary["DecodeParms"] = Dictionary(("Predictor", new PdfNumber(2L)), ("Colors", new PdfNumber(colors)), ("Columns", new PdfNumber(columns)), ("BitsPerComponent", new PdfNumber(bits)));
        Assert.Equal(Pack(samples), PdfFilters.Decode(stream));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    public void PngPredictionSupportsAllRowFilters(int filter)
    {
        const int row = 9, rows = 3, colors = 3;
        var original = Enumerable.Range(0, row * rows).Select(i => (byte)(i * 29 + 7)).ToArray(); var encoded = new byte[(row + 1) * rows];
        for (var y = 0; y < rows; y++)
        {
            encoded[y * (row + 1)] = (byte)filter;
            for (var x = 0; x < row; x++)
            {
                var a = x < colors ? 0 : original[y * row + x - colors]; var b = y == 0 ? 0 : original[(y - 1) * row + x]; var c = y == 0 || x < colors ? 0 : original[(y - 1) * row + x - colors];
                var p = a + b - c; var pa = Math.Abs(p - a); var pb = Math.Abs(p - b); var pc = Math.Abs(p - c);
                var predicted = filter switch { 0 => 0, 1 => a, 2 => b, 3 => (a + b) / 2, _ => pa <= pb && pa <= pc ? a : pb <= pc ? b : c };
                encoded[y * (row + 1) + x + 1] = (byte)(original[y * row + x] - predicted);
            }
        }
        var stream = PdfStream.FromDecoded(encoded); stream.Dictionary["DecodeParms"] = Dictionary(("Predictor", new PdfNumber(15L)), ("Colors", new PdfNumber(colors)), ("Columns", new PdfNumber(3L)));
        Assert.Equal(original, PdfFilters.Decode(stream));
    }

    [Fact]
    public void TotalDecodeBudgetAndSaveBudgetAreEnforced()
    {
        var file = PdfFile.Create(new PdfReadLimits(MaximumTotalDecodedBytes: 10)); var stream = PdfStream.FromDecoded(new byte[6]);
        Assert.Equal(6, file.Decode(stream).Length); Assert.Throws<InvalidDataException>(() => file.Decode(stream));
        Assert.Throws<InvalidDataException>(() => file.Save(maximumBytes: 16));
        Assert.Throws<NotSupportedException>(() => PdfFilters.Decode(new PdfStream(Dictionary(("Filter", new PdfName("UnknownDecode"))), [1, 2])));
    }

    internal static byte[] RawFixture(Dictionary<int, byte[]> objects)
    {
        using var stream = new MemoryStream(); Write(stream, "%PDF-1.7\n"); var offsets = new Dictionary<int, long>();
        foreach (var (number, content) in objects.OrderBy(p => p.Key)) { offsets[number] = stream.Position; Write(stream, $"{number} 0 obj\n"); stream.Write(content); Write(stream, "\nendobj\n"); }
        var xref = stream.Position; var size = objects.Keys.Max() + 1; Write(stream, $"xref\n0 {size}\n0000000000 65535 f \n");
        for (var i = 1; i < size; i++) Write(stream, offsets.TryGetValue(i, out var offset) ? $"{offset:D10} 00000 n \n" : "0000000000 00000 f \n");
        Write(stream, $"trailer\n<< /Size {size} /Root 1 0 R >>\nstartxref\n{xref}\n%%EOF\n"); return stream.ToArray();
    }
    private static byte[] CompressedFixture(bool hybrid)
    {
        using var stream = new MemoryStream(); Write(stream, "%PDF-1.7\n");
        var content = "0 0 10 10 re f\n"u8.ToArray(); var contentOffset = stream.Position;
        Write(stream, $"4 0 obj\n<< /Length {content.Length} >>\nstream\n"); stream.Write(content); Write(stream, "\nendstream\nendobj\n");
        var bodies = new[] { "<< /Type /Catalog /Pages 2 0 R >>", "<< /Type /Pages /Kids [3 0 R] /Count 1 >>", "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 200 300] /Contents 4 0 R >>" };
        var header = new StringBuilder(); var offset = 0;
        for (var i = 0; i < bodies.Length; i++) { header.Append($"{i + 1} {offset} "); offset += Encoding.ASCII.GetByteCount(bodies[i]) + 1; }
        var data = Encoding.ASCII.GetBytes(header + string.Join("\n", bodies) + "\n"); var compressed = Deflate(data); var objectOffset = stream.Position;
        Write(stream, $"5 0 obj\n<< /Type /ObjStm /N 3 /First {header.Length} /Filter /FlateDecode /Length {compressed.Length} >>\nstream\n"); stream.Write(compressed); Write(stream, "\nendstream\nendobj\n");
        var xrefOffset = stream.Position; var entries = new byte[49];
        void Entry(int id, int type, long field, int extra) { var b = entries.AsSpan(id * 7, 7); b[0] = (byte)type; BinaryPrimitives.WriteUInt32BigEndian(b[1..], (uint)field); BinaryPrimitives.WriteUInt16BigEndian(b[5..], (ushort)extra); }
        Entry(0, 0, 0, 65535); for (var i = 1; i <= 3; i++) Entry(i, 2, 5, i - 1);
        Entry(4, 1, contentOffset, 0); Entry(5, 1, objectOffset, 0); Entry(6, 1, xrefOffset, 0);
        var xrefData = Deflate(entries);
        Write(stream, $"6 0 obj\n<< /Type /XRef /Root 1 0 R /Size 7 /W [1 4 2] /Index [0 7] /Length {xrefData.Length} /Filter /FlateDecode >>\nstream\n"); stream.Write(xrefData); Write(stream, "\nendstream\nendobj\n");
        if (hybrid)
        {
            var table = stream.Position; Write(stream, "xref\n0 7\n0000000000 65535 f \n0000000000 00000 f \n0000000000 00000 f \n0000000000 00000 f \n");
            Write(stream, $"{contentOffset:D10} 00000 n \n{objectOffset:D10} 00000 n \n{xrefOffset:D10} 00000 n \ntrailer\n<< /Root 1 0 R /Size 7 /XRefStm {xrefOffset} >>\nstartxref\n{table}\n%%EOF\n");
        }
        else Write(stream, $"startxref\n{xrefOffset}\n%%EOF\n");
        return stream.ToArray();
    }
    private static byte[] EncodeLiteralLzw(byte[] data, int early)
    {
        var bits = new List<int>(); var width = 9; var next = 258; var hasPrevious = false;
        void Emit(int code) { for (var b = width - 1; b >= 0; b--) bits.Add((code >> b) & 1); }
        Emit(256);
        foreach (var value in data)
        {
            Emit(value);
            if (hasPrevious && next < 4096) { next++; if (width < 12 && next + early == 1 << width) width++; }
            hasPrevious = true;
        }
        Emit(257); var result = new byte[(bits.Count + 7) / 8];
        for (var i = 0; i < bits.Count; i++) result[i / 8] |= (byte)(bits[i] << (7 - i % 8)); return result;
    }
    private static byte[] Deflate(byte[] data) { using var output = new MemoryStream(); using (var encoder = new ZLibStream(output, CompressionLevel.SmallestSize, true)) encoder.Write(data); return output.ToArray(); }
    private static void Write(Stream stream, string text) => stream.Write(Encoding.ASCII.GetBytes(text));
}
