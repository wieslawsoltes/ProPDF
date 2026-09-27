using System.Collections;
using System.Globalization;
using System.Text;

namespace ProPDF.Kernel;

/// <summary>PDF objects retain unknown dictionary entries and encoded stream bytes during a graph rewrite.</summary>
public abstract class PdfObject { }
public sealed class PdfNull : PdfObject
{
    public static PdfNull Value { get; } = new();
    private PdfNull() { }
}
public sealed class PdfBoolean(bool value) : PdfObject { public bool Value { get; } = value; }
public sealed class PdfNumber : PdfObject
{
    public PdfNumber(double value)
    {
        if (!double.IsFinite(value)) throw new ArgumentOutOfRangeException(nameof(value));
        Lexeme = value.ToString("0.################", CultureInfo.InvariantCulture);
    }
    public PdfNumber(long value) => Lexeme = value.ToString(CultureInfo.InvariantCulture);
    internal PdfNumber(string lexeme)
    {
        if (!double.TryParse(lexeme, NumberStyles.AllowLeadingSign | NumberStyles.AllowDecimalPoint,
            CultureInfo.InvariantCulture, out var number) || !double.IsFinite(number)) throw new InvalidDataException("Invalid PDF number.");
        Lexeme = lexeme;
    }
    public string Lexeme { get; }
    public double Value => double.Parse(Lexeme, CultureInfo.InvariantCulture);
    public long Integer => long.TryParse(Lexeme, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var value)
        ? value : throw new InvalidDataException("An integer PDF value was required.");
}
public sealed class PdfName(string value) : PdfObject
{
    public string Value { get; } = value ?? throw new ArgumentNullException(nameof(value));
    public override string ToString() => "/" + Value;
}
public sealed class PdfString : PdfObject
{
    private readonly byte[] _bytes;
    public PdfString(ReadOnlySpan<byte> bytes) => _bytes = bytes.ToArray();
    public PdfString(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        _bytes = text.All(c => c is >= ' ' and <= '~') ? Encoding.ASCII.GetBytes(text) :
            new byte[] { 254, 255 }.Concat(Encoding.BigEndianUnicode.GetBytes(text)).ToArray();
    }
    public ReadOnlySpan<byte> Bytes => _bytes;
    public byte[] ToArray() => (byte[])_bytes.Clone();
    public string Text => _bytes.AsSpan().StartsWith(new byte[] { 254, 255 }) ? Encoding.BigEndianUnicode.GetString(_bytes, 2, _bytes.Length - 2) :
        _bytes.AsSpan().StartsWith(new byte[] { 239, 187, 191 }) ? Encoding.UTF8.GetString(_bytes, 3, _bytes.Length - 3) :
        new string(_bytes.Select(b => PdfDocEncoding[b]).ToArray());
    private static readonly char[] PdfDocEncoding = CreateEncoding();
    private static char[] CreateEncoding()
    {
        var result = Enumerable.Range(0, 256).Select(i => (char)i).ToArray();
        const string accents = "˘ˇˆ˙˝˛˚˜";
        for (var i = 0; i < accents.Length; i++) result[24 + i] = accents[i];
        const string specials = "•†‡…—–ƒ⁄‹›−‰„“”‘’‚™ﬁﬂŁŒŠŸŽıłœšž";
        for (var i = 0; i < specials.Length; i++) result[128 + i] = specials[i];
        result[127] = result[159] = result[173] = '\uFFFD'; result[160] = '€';
        return result;
    }
}
public readonly record struct PdfObjectId(int Number, int Generation = 0);
public sealed class PdfReference : PdfObject, IEquatable<PdfReference>
{
    public PdfReference(int number, int generation = 0)
    {
        if (number <= 0 || generation is < 0 or > 65535) throw new ArgumentOutOfRangeException(nameof(number));
        Id = new PdfObjectId(number, generation);
    }
    public PdfObjectId Id { get; }
    public bool Equals(PdfReference? other) => other is not null && Id == other.Id;
    public override bool Equals(object? obj) => obj is PdfReference reference && Equals(reference);
    public override int GetHashCode() => Id.GetHashCode();
    public override string ToString() => $"{Id.Number} {Id.Generation} R";
}
public sealed class PdfArray : PdfObject, IReadOnlyList<PdfObject>
{
    public PdfArray(params PdfObject[] values) => Items = [.. values];
    public PdfArray(IEnumerable<PdfObject> values) => Items = [.. values];
    public List<PdfObject> Items { get; }
    public PdfObject this[int index] { get => Items[index]; set => Items[index] = value ?? PdfNull.Value; }
    public int Count => Items.Count;
    public IEnumerator<PdfObject> GetEnumerator() => Items.GetEnumerator();
    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
}
public class PdfDictionary : PdfObject, IEnumerable<KeyValuePair<string, PdfObject>>
{
    private readonly Dictionary<string, PdfObject> _items = new(StringComparer.Ordinal);
    public PdfDictionary() { }
    public PdfDictionary(IEnumerable<KeyValuePair<string, PdfObject>> values) { foreach (var item in values) this[item.Key] = item.Value; }
    public PdfObject this[string name] { get => _items.GetValueOrDefault(name, PdfNull.Value); set => _items[name] = value ?? PdfNull.Value; }
    public int Count => _items.Count;
    public bool Contains(string name) => _items.ContainsKey(name);
    public bool Remove(string name) => _items.Remove(name);
    public string? Name(string name) => this[name] is PdfName n ? n.Value : null;
    public double Number(string name, double fallback = 0) => this[name] is PdfNumber n ? n.Value : fallback;
    public string Text(string name, string fallback = "") => this[name] is PdfString s ? s.Text : fallback;
    public PdfDictionary Copy() => new(_items);
    public IEnumerator<KeyValuePair<string, PdfObject>> GetEnumerator() => _items.GetEnumerator();
    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
}
public sealed class PdfStream : PdfObject
{
    public PdfStream(PdfDictionary dictionary, ReadOnlySpan<byte> encodedBytes)
    { Dictionary = dictionary ?? throw new ArgumentNullException(nameof(dictionary)); EncodedBytes = encodedBytes.ToArray(); }
    public PdfDictionary Dictionary { get; }
    public byte[] EncodedBytes { get; }
    public static PdfStream FromDecoded(ReadOnlySpan<byte> bytes, PdfDictionary? dictionary = null)
    {
        var result = dictionary?.Copy() ?? new PdfDictionary();
        result.Remove("DecodeParms"); result["Filter"] = new PdfName("FlateDecode");
        return new PdfStream(result, PdfFilters.Deflate(bytes));
    }
}
public static class PdfValues
{
    public static PdfDictionary Dictionary(params (string Key, PdfObject Value)[] values) => new(values.Select(v => new KeyValuePair<string, PdfObject>(v.Key, v.Value)));
    public static PdfArray Numbers(params double[] values) => new(values.Select(v => (PdfObject)new PdfNumber(v)));
}

public sealed record PdfReadLimits(int MaximumFileBytes = 256 * 1024 * 1024, int MaximumObjects = 1_000_000,
    int MaximumDepth = 128, int MaximumDecodedStreamBytes = 128 * 1024 * 1024,
    long MaximumTotalDecodedBytes = 512L * 1024 * 1024, int MaximumRevisions = 256,
    int MaximumTokenBytes = 8 * 1024 * 1024)
{
    public void Validate()
    {
        if (MaximumFileBytes < 8 || MaximumObjects < 1 || MaximumDepth is < 1 or > 512 || MaximumDecodedStreamBytes < 1 ||
            MaximumTotalDecodedBytes < 1 || MaximumRevisions < 1 || MaximumTokenBytes < 1) throw new ArgumentOutOfRangeException(nameof(PdfReadLimits));
    }
}
