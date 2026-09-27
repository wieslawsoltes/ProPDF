using System.Buffers.Binary;
using System.Text;
using ProPDF.Core;
using ProPDF.Editing;
using ProPDF.Engine.PdfPig;
using ProPDF.Kernel;
using ProPDF.Rendering.Skia;
using SkiaSharp;
using Xunit;

namespace ProPDF.Tests;

public sealed class FontEmbeddingTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task GeneratedTrueTypeProgramsEmbedAndRenderWithIndependentUnicodeMapping(bool format4)
    {
        // Generate a small geometric test font in memory. No external font files or copyrighted glyphs are included.
        var font = new PdfBinaryAsset(TinyTrueType.Create(format4: format4));
        var backend = new PdfPigBackend();
        var editor = new ManagedPdfEditor(backend);
        var source = await editor.CreateAsync(size: new PdfSize(200, 200));
        var text = format4 ? "AΩ" : "AΩ😀";
        var document = await editor.ApplyAsync(source, [new AddText(1, new PdfPoint(20, 70), text, 40, EmbeddedFont: font)]);
        Assert.Equal(text, (await backend.GetPageTextAsync(document, 1)).Text);
        using var input = document.OpenRead();
        var file = PdfFile.Open(await PdfStreams.ReadBoundedAsync(input, 1024 * 1024));
        var embedded = file.EnumerateObjects().Select(x => x.Value).OfType<PdfDictionary>()
            .Single(x => x.Name("Subtype") == "Type0");
        var mapping = Encoding.ASCII.GetString(file.Decode((PdfStream)file.Resolve(embedded["ToUnicode"])));
        Assert.Contains("<0002> <03A9>", mapping);
        if (!format4) Assert.Contains("<0003> <D83DDE00>", mapping);
        await using var renderer = new SkiaPdfRenderer(backend);
        using var tile = await renderer.RenderTileAsync(document, new SkiaTileRequest(1, new PdfRect(0, 0, 150, 100), 1));
        using var pixels = SKBitmap.FromImage(tile.Image);
        Assert.True(pixels.GetPixel(30, 50).Red < 20, "Embedded geometric glyph was not rendered.");
        Assert.Equal(SKColors.White, pixels.GetPixel(5, 5));
    }

    [Theory]
    [InlineData(2)]
    [InlineData(512)]
    public async Task EmbeddingRestrictionsAreEnforcedBeforePublishing(int restriction)
    {
        var editor = new ManagedPdfEditor();
        var source = await editor.CreateAsync();
        await Assert.ThrowsAsync<NotSupportedException>(() => editor.ApplyAsync(source,
            [new AddText(1, new PdfPoint(30, 60), "A", EmbeddedFont: new PdfBinaryAsset(TinyTrueType.Create((ushort)restriction)))]));
    }

    [Fact]
    public async Task GlyphAliasesPreserveDistinctUnicodeValuesAndMissingGlyphFails()
    {
        var backend = new PdfPigBackend();
        var editor = new ManagedPdfEditor(backend);
        var source = await editor.CreateAsync();
        var font = new PdfBinaryAsset(TinyTrueType.Create());
        // The generated format-12 cmap maps both A and the emoji to glyph 1: the PDF still needs different semantic CIDs.
        var result = await editor.ApplyAsync(source, [new AddText(1, new PdfPoint(30, 60), "A😀A", EmbeddedFont: font)]);
        Assert.Equal("A😀A", (await backend.GetPageTextAsync(result, 1)).Text);
        await Assert.ThrowsAsync<NotSupportedException>(() => editor.ApplyAsync(source,
            [new AddText(1, new PdfPoint(30, 60), "B", EmbeddedFont: font)]));
    }

    private static class TinyTrueType
    {
        public static byte[] Create(ushort embeddingFlags = 0, bool format4 = false)
        {
            var tables = new SortedDictionary<string, byte[]>(StringComparer.Ordinal);
            var head = new byte[54]; U32(head, 0, 0x10000); U32(head, 4, 0x10000); U32(head, 12, 0x5f0f3cf5);
            U16(head, 16, 3); U16(head, 18, 1000); U16(head, 40, 500); U16(head, 42, 700); U16(head, 46, 8);
            U16(head, 48, 2); U16(head, 50, 1); tables["head"] = head;
            var hhea = new byte[36]; U32(hhea, 0, 0x10000); U16(hhea, 4, 800); U16(hhea, 6, unchecked((ushort)-200));
            U16(hhea, 10, 600); U16(hhea, 16, 500); U16(hhea, 18, 1); U16(hhea, 34, 3); tables["hhea"] = hhea;
            var maxp = new byte[32]; U32(maxp, 0, 0x10000); U16(maxp, 4, 3); U16(maxp, 6, 4); U16(maxp, 8, 1); U16(maxp, 14, 1); tables["maxp"] = maxp;
            var hmtx = new byte[12]; for (var i = 0; i < 3; i++) { U16(hmtx, i * 4, 600); U16(hmtx, i * 4 + 2, 50); }
            tables["hmtx"] = hmtx;
            var glyph = new byte[36]; U16(glyph, 0, 1); U16(glyph, 2, 50); U16(glyph, 6, 500); U16(glyph, 8, 700);
            U16(glyph, 10, 3); // Four on-curve points, no instructions, explicit signed deltas.
            glyph[14] = glyph[15] = glyph[16] = glyph[17] = 1;
            U16(glyph, 18, 50); U16(glyph, 20, 450); U16(glyph, 22, 0); U16(glyph, 24, unchecked((ushort)-450));
            U16(glyph, 26, 0); U16(glyph, 28, 0); U16(glyph, 30, 700); U16(glyph, 32, 0);
            tables["glyf"] = glyph.Concat(glyph).Concat(glyph).ToArray();
            var loca = new byte[16]; for (var i = 0; i < 4; i++) U32(loca, i * 4, (uint)(i * glyph.Length)); tables["loca"] = loca;
            var cmap = new byte[format4 ? 52 : 64]; U16(cmap, 2, 1); U16(cmap, 4, 3); U16(cmap, 6, format4 ? 1 : 10); U32(cmap, 8, 12);
            if (format4)
            {
                U16(cmap, 12, 4); U16(cmap, 14, 40); U16(cmap, 18, 6); U16(cmap, 20, 4); U16(cmap, 22, 1); U16(cmap, 24, 2);
                var codes = new[] { 65, 937, 65535 };
                for (var i = 0; i < 3; i++)
                {
                    U16(cmap, 26 + i * 2, codes[i]); U16(cmap, 34 + i * 2, codes[i]);
                    U16(cmap, 40 + i * 2, unchecked((ushort)((i == 2 ? 0 : i + 1) - codes[i])));
                }
            }
            else
            {
                U16(cmap, 12, 12); U32(cmap, 16, 52); U32(cmap, 24, 3);
                var codes = new uint[] { 65, 937, 0x1f600 };
                for (var i = 0; i < 3; i++) { U32(cmap, 28 + i * 12, codes[i]); U32(cmap, 32 + i * 12, codes[i]); U32(cmap, 36 + i * 12, (uint)(i == 1 ? 2 : 1)); }
            }
            tables["cmap"] = cmap;
            var os2 = new byte[78]; U16(os2, 2, 600); U16(os2, 4, 400); U16(os2, 6, 5); U16(os2, 8, embeddingFlags);
            Encoding.ASCII.GetBytes("PPDF").CopyTo(os2, 58); U16(os2, 62, 64); U16(os2, 64, 65); U16(os2, 66, 937);
            U16(os2, 68, 800); U16(os2, 70, unchecked((ushort)-200)); U16(os2, 74, 800); U16(os2, 76, 200); tables["OS/2"] = os2;
            var post = new byte[32]; U32(post, 0, 0x30000); tables["post"] = post;
            var name = Encoding.BigEndianUnicode.GetBytes("ProPDF Synthetic");
            var names = new byte[18 + name.Length]; U16(names, 2, 1); U16(names, 4, 18); U16(names, 6, 3); U16(names, 8, 1);
            U16(names, 10, 0x409); U16(names, 12, 1); U16(names, 14, name.Length); name.CopyTo(names, 18); tables["name"] = names;
            var offset = 12 + tables.Count * 16; var data = new byte[offset + tables.Values.Sum(t => (t.Length + 3) & ~3)];
            U32(data, 0, 0x10000); U16(data, 4, tables.Count); U16(data, 6, 128); U16(data, 8, 3); U16(data, 10, tables.Count * 16 - 128);
            var index = 0; var headOffset = 0;
            foreach (var (tag, bytes) in tables)
            {
                var entry = 12 + index++ * 16; Encoding.ASCII.GetBytes(tag).CopyTo(data, entry);
                U32(data, entry + 4, Sum(bytes)); U32(data, entry + 8, (uint)offset); U32(data, entry + 12, (uint)bytes.Length);
                if (tag == "head") headOffset = offset;
                bytes.CopyTo(data, offset); offset += (bytes.Length + 3) & ~3;
            }
            U32(data, headOffset + 8, unchecked(0xb1b0afba - Sum(data)));
            return data;
        }
        private static uint Sum(byte[] bytes)
        {
            uint sum = 0;
            for (var i = 0; i < bytes.Length; i += 4)
            {
                uint value = 0; for (var j = 0; j < 4; j++) value = (value << 8) | (i + j < bytes.Length ? bytes[i + j] : 0u);
                sum = unchecked(sum + value);
            }
            return sum;
        }
        private static void U16(byte[] bytes, int offset, int value) => BinaryPrimitives.WriteUInt16BigEndian(bytes.AsSpan(offset), checked((ushort)value));
        private static void U32(byte[] bytes, int offset, uint value) => BinaryPrimitives.WriteUInt32BigEndian(bytes.AsSpan(offset), value);
    }
}
