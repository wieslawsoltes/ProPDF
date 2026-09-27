using System.Globalization;
using System.Text;

namespace ProPDF.Kernel;

/// <summary>Byte-oriented PDF lexical reader. It never searches for object or stream delimiters inside binary payloads.</summary>
public sealed class PdfSyntaxReader
{
    private readonly byte[] _data;
    private readonly int _end;
    private readonly PdfReadLimits _limits;
    private readonly CancellationToken _token;
    private int _nodes;
    public PdfSyntaxReader(byte[] data, int position = 0, PdfReadLimits? limits = null, CancellationToken cancellationToken = default, int? end = null)
    {
        _data = data; _end = end ?? data.Length; _limits = limits ?? new PdfReadLimits(); _token = cancellationToken;
        if (position < 0 || _end < position || _end > data.Length) throw new ArgumentOutOfRangeException(nameof(position));
        Position = position;
    }
    public int Position { get; set; }
    public bool End { get { SkipWhiteSpace(); return Position >= _end; } }
    public static bool IsWhite(byte value) => value is 0 or 9 or 10 or 12 or 13 or 32;
    public static bool IsDelimiter(byte value) => IsWhite(value) || value is (byte)'(' or (byte)')' or (byte)'<' or (byte)'>' or (byte)'[' or (byte)']' or (byte)'/' or (byte)'%';
    public void SkipWhiteSpace()
    {
        _token.ThrowIfCancellationRequested();
        while (Position < _end)
        {
            if (IsWhite(_data[Position])) { Position++; continue; }
            if (_data[Position] != '%') break;
            while (Position < _end && _data[Position] is not 10 and not 13) Position++;
        }
    }
    public bool TryKeyword(string keyword)
    {
        SkipWhiteSpace();
        var bytes = Encoding.ASCII.GetBytes(keyword);
        if (Position + bytes.Length > _end || !_data.AsSpan(Position, bytes.Length).SequenceEqual(bytes) ||
            Position + bytes.Length < _end && !IsDelimiter(_data[Position + bytes.Length])) return false;
        Position += bytes.Length; return true;
    }
    public string ReadKeyword()
    {
        SkipWhiteSpace(); var start = Position;
        while (Position < _end && !IsDelimiter(_data[Position])) Position++;
        if (Position == start || Position - start > _limits.MaximumTokenBytes) throw new InvalidDataException("Invalid or overlong PDF token.");
        return Encoding.ASCII.GetString(_data, start, Position - start);
    }
    public long ReadInteger()
    {
        var value = ReadKeyword();
        if (!long.TryParse(value, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var result)) throw new InvalidDataException("Expected PDF integer.");
        return result;
    }
    public PdfObject ReadObject(int depth = 0)
    {
        SkipWhiteSpace();
        if (Position >= _end) throw new EndOfStreamException("Missing PDF object.");
        if (depth > _limits.MaximumDepth || ++_nodes > _limits.MaximumObjects * 16L) throw new InvalidDataException("PDF object nesting or token budget exceeded.");
        var first = _data[Position++];
        switch (first)
        {
            case (byte)'/':
                using (var name = new MemoryStream())
                {
                    while (Position < _end && !IsDelimiter(_data[Position]))
                    {
                        var b = _data[Position++];
                        if (b == '#')
                        {
                            if (Position + 2 > _end) throw new InvalidDataException("Truncated PDF name escape.");
                            b = (byte)((Hex(_data[Position++]) << 4) | Hex(_data[Position++]));
                        }
                        if (b == 0) throw new InvalidDataException("PDF names cannot contain NUL.");
                        name.WriteByte(b); CheckLength(name.Length);
                    }
                    return new PdfName(Encoding.Latin1.GetString(name.ToArray()));
                }
            case (byte)'(':
                using (var text = new MemoryStream())
                {
                    var nesting = 1;
                    while (Position < _end)
                    {
                        var b = _data[Position++];
                        if (b == '\\')
                        {
                            if (Position >= _end) throw new InvalidDataException("Truncated literal-string escape.");
                            b = _data[Position++];
                            if (b is >= (byte)'0' and <= (byte)'7')
                            {
                                var value = b - '0';
                                for (var i = 0; i < 2 && Position < _end && _data[Position] is >= (byte)'0' and <= (byte)'7'; i++) value = value * 8 + _data[Position++] - '0';
                                text.WriteByte((byte)value);
                            }
                            else if (b is 10 or 13) { if (b == 13 && Position < _end && _data[Position] == 10) Position++; }
                            else text.WriteByte(b switch { (byte)'n' => 10, (byte)'r' => 13, (byte)'t' => 9, (byte)'b' => 8, (byte)'f' => 12, _ => b });
                        }
                        else if (b == '(') { if (++nesting > _limits.MaximumDepth) throw new InvalidDataException("Literal-string nesting exceeded."); text.WriteByte(b); }
                        else if (b == ')') { if (--nesting == 0) return new PdfString(text.ToArray()); text.WriteByte(b); }
                        else if (b == 13) { if (Position < _end && _data[Position] == 10) Position++; text.WriteByte(10); }
                        else text.WriteByte(b);
                        CheckLength(text.Length);
                    }
                    throw new InvalidDataException("Unterminated PDF literal string.");
                }
            case (byte)'<':
                if (Position < _end && _data[Position] == '<')
                {
                    Position++; var dictionary = new PdfDictionary();
                    while (true)
                    {
                        SkipWhiteSpace();
                        if (Position + 1 < _end && _data[Position] == '>' && _data[Position + 1] == '>') { Position += 2; return dictionary; }
                        if (ReadObject(depth + 1) is not PdfName name) throw new InvalidDataException("PDF dictionary key is not a name.");
                        if (dictionary.Contains(name.Value)) throw new InvalidDataException("Duplicate PDF dictionary key.");
                        dictionary[name.Value] = ReadObject(depth + 1);
                    }
                }
                using (var hex = new MemoryStream())
                {
                    var high = -1;
                    while (Position < _end)
                    {
                        var b = _data[Position++];
                        if (b == '>') { if (high >= 0) hex.WriteByte((byte)(high << 4)); return new PdfString(hex.ToArray()); }
                        if (IsWhite(b)) continue;
                        var digit = Hex(b);
                        if (high < 0) high = digit; else { hex.WriteByte((byte)((high << 4) | digit)); high = -1; }
                        CheckLength(hex.Length);
                    }
                    throw new InvalidDataException("Unterminated hexadecimal PDF string.");
                }
            case (byte)'[':
                var array = new PdfArray();
                while (true)
                {
                    SkipWhiteSpace();
                    if (Position < _end && _data[Position] == ']') { Position++; return array; }
                    array.Items.Add(ReadObject(depth + 1));
                }
            default:
                Position--;
                var word = ReadKeyword();
                if (word == "null") return PdfNull.Value;
                if (word == "true" || word == "false") return new PdfBoolean(word == "true");
                var number = new PdfNumber(word);
                var after = Position;
                if (long.TryParse(word, NumberStyles.None, CultureInfo.InvariantCulture, out var id) && id is > 0 and <= int.MaxValue)
                {
                    try
                    {
                        var generation = ReadInteger();
                        if (generation is >= 0 and <= 65535 && TryKeyword("R")) return new PdfReference((int)id, (int)generation);
                    }
                    catch (Exception error) when (error is InvalidDataException or EndOfStreamException) { }
                    Position = after;
                }
                return number;
        }
    }
    public byte[] ReadStreamBytes(int length)
    {
        if (length < 0 || (long)Position + length > _end) throw new InvalidDataException("Invalid PDF stream length.");
        // 'stream' must be followed by an EOL; spaces before that EOL are accepted for interoperability.
        while (Position < _end && _data[Position] is 9 or 32) Position++;
        if (Position < _end && _data[Position] == 13) { Position++; if (Position < _end && _data[Position] == 10) Position++; }
        else if (Position < _end && _data[Position] == 10) Position++;
        else throw new InvalidDataException("Missing stream EOL.");
        if ((long)Position + length > _end) throw new InvalidDataException("Truncated PDF stream.");
        var result = _data.AsSpan(Position, length).ToArray(); Position += length;
        if (!TryKeyword("endstream")) throw new InvalidDataException("PDF stream length does not terminate at endstream.");
        return result;
    }
    private void CheckLength(long length) { if (length > _limits.MaximumTokenBytes) throw new InvalidDataException("PDF token exceeds byte budget."); }
    internal static int Hex(byte b) => b is >= 48 and <= 57 ? b - 48 : b is >= 65 and <= 70 ? b - 55 : b is >= 97 and <= 102 ? b - 87 : throw new InvalidDataException("Invalid hexadecimal digit.");
}

public static class PdfObjectWriter
{
    public static byte[] Serialize(PdfObject value, int maximumDepth = 128)
    {
        using var output = new MemoryStream(); Write(output, value, maximumDepth); return output.ToArray();
    }
    internal static void Write(Stream output, PdfObject value, int maximumDepth, int depth = 0)
    {
        if (depth > maximumDepth) throw new InvalidDataException("Cyclic or excessively nested direct PDF objects.");
        switch (value)
        {
            case PdfNull: Text(output, "null"); break;
            case PdfBoolean b: Text(output, b.Value ? "true" : "false"); break;
            case PdfNumber n: Text(output, n.Lexeme); break;
            case PdfName n:
                output.WriteByte((byte)'/');
                foreach (var c in n.Value)
                {
                    if (c > 255 || c == 0) throw new InvalidDataException("PDF names must contain non-NUL byte values.");
                    var b = (byte)c;
                    if (b < 33 || b > 126 || PdfSyntaxReader.IsDelimiter(b) || b == '#') Text(output, "#" + b.ToString("X2", CultureInfo.InvariantCulture));
                    else output.WriteByte(b);
                }
                break;
            case PdfString s: Text(output, "<" + Convert.ToHexString(s.Bytes) + ">"); break;
            case PdfReference r: Text(output, r.ToString()); break;
            case PdfArray a:
                Text(output, "[");
                foreach (var item in a) { Write(output, item, maximumDepth, depth + 1); Text(output, " "); }
                Text(output, "]"); break;
            case PdfDictionary d:
                Text(output, "<<");
                foreach (var item in d.OrderBy(p => p.Key, StringComparer.Ordinal))
                { Write(output, new PdfName(item.Key), maximumDepth, depth + 1); Text(output, " "); Write(output, item.Value, maximumDepth, depth + 1); Text(output, "\n"); }
                Text(output, ">>"); break;
            case PdfStream s:
                var dict = s.Dictionary.Copy(); dict["Length"] = new PdfNumber(s.EncodedBytes.Length);
                Write(output, dict, maximumDepth, depth + 1); Text(output, "\nstream\n"); output.Write(s.EncodedBytes); Text(output, "\nendstream"); break;
            default: throw new NotSupportedException("Unknown PDF object implementation.");
        }
    }
    internal static void Text(Stream output, string text) => output.Write(Encoding.ASCII.GetBytes(text));
}
