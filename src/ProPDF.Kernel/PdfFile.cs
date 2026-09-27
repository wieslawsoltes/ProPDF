using System.Globalization;
using System.Text;

namespace ProPDF.Kernel;

/// <summary>Single-owner mutable PDF graph. Uses xref entries, never regex scanning, to locate objects.</summary>
public sealed class PdfFile
{
    private readonly byte[] _source;
    private readonly Dictionary<int, XrefEntry> _xref = [];
    private readonly Dictionary<PdfObjectId, PdfObject> _objects = [];
    private readonly HashSet<PdfObjectId> _loading = [];
    private readonly Dictionary<int, Dictionary<int, PdfObject>> _objectStreams = [];
    private readonly CancellationToken _token;
    private long _decoded;
    private int _maximumObject;
    private int _revisionCount;
    private PdfReference? _encryptionReference;
    private PdfSecurity? _security;
    private readonly record struct XrefEntry(int Type, long Offset, int GenerationOrIndex);
    private PdfFile(byte[] source, PdfReadLimits limits, CancellationToken token) { _source = source; Limits = limits; _token = token; }
    public PdfReadLimits Limits { get; }
    public CancellationToken CancellationToken => _token;
    public PdfDictionary Trailer { get; private set; } = new();
    public string Version { get; set; } = "1.7";
    public long LastCrossReferenceOffset { get; private set; }
    public int RevisionCount => Math.Max(1, _revisionCount);
    public long GetObjectOffset(PdfReference reference) => _xref.TryGetValue(reference.Id.Number, out var entry) && entry.Type == 1 && entry.GenerationOrIndex == reference.Id.Generation
        ? entry.Offset : throw new NotSupportedException("This object has no uncompressed source offset.");
    public bool IsEncrypted => _security is not null;
    public bool IsOwnerAuthorized => _security?.OwnerAuthorized ?? true;
    public PdfDictionary Catalog => Dictionary(Trailer["Root"]);
    public ReadOnlyMemory<byte> OriginalBytes => _source;

    public static PdfFile Create(PdfReadLimits? limits = null, CancellationToken cancellationToken = default)
    {
        limits ??= new PdfReadLimits(); limits.Validate();
        var file = new PdfFile([], limits, cancellationToken);
        var pages = file.Add(PdfValues.Dictionary(("Type", new PdfName("Pages")), ("Kids", new PdfArray()), ("Count", new PdfNumber(0L))));
        file.Trailer["Root"] = file.Add(PdfValues.Dictionary(("Type", new PdfName("Catalog")), ("Pages", pages)));
        var id = System.Security.Cryptography.RandomNumberGenerator.GetBytes(16);
        file.Trailer["ID"] = new PdfArray(new PdfString(id), new PdfString(id));
        return file;
    }
    public static PdfFile Open(ReadOnlySpan<byte> bytes, string? password = null, PdfReadLimits? limits = null, CancellationToken cancellationToken = default)
    {
        limits ??= new PdfReadLimits(); limits.Validate(); cancellationToken.ThrowIfCancellationRequested();
        if (bytes.Length < 8 || bytes.Length > limits.MaximumFileBytes) throw new InvalidDataException("PDF file size is outside configured limits.");
        var header = bytes[..Math.Min(1024, bytes.Length)].IndexOf("%PDF-"u8);
        if (header < 0 || header + 8 > bytes.Length) throw new InvalidDataException("Missing PDF header.");
        var file = new PdfFile(bytes.ToArray(), limits, cancellationToken) { Version = Encoding.ASCII.GetString(bytes.Slice(header + 5, 3)) };
        if (file.Version is not ("1.0" or "1.1" or "1.2" or "1.3" or "1.4" or "1.5" or "1.6" or "1.7" or "2.0")) throw new NotSupportedException("Unsupported PDF header version.");
        var tailStart = Math.Max(0, bytes.Length - 1_048_576);
        var marker = bytes[tailStart..].LastIndexOf("startxref"u8);
        if (marker < 0) throw new InvalidDataException("No final startxref marker. Repair is never performed implicitly.");
        file.LastCrossReferenceOffset = new PdfSyntaxReader(file._source, tailStart + marker + 9, limits, cancellationToken).ReadInteger();
        file.ReadCrossReferences(file.LastCrossReferenceOffset, new HashSet<long>());
        file._maximumObject = file._xref.Keys.DefaultIfEmpty(0).Max();
        if (file.Trailer["Encrypt"] is not PdfNull)
        {
            file._encryptionReference = file.Trailer["Encrypt"] as PdfReference;
            var encryption = file.Dictionary(file.Trailer["Encrypt"]);
            var id = file.Resolve(file.Trailer["ID"]) is PdfArray a && a.Count > 0 && a[0] is PdfString s ? s.ToArray() : [];
            file._security = PdfSecurity.Open(encryption, id, password ?? "", cancellationToken);
        }
        if (file.Catalog.Name("Type") != "Catalog") throw new InvalidDataException("Invalid PDF catalog.");
        return file;
    }
    public PdfObject Resolve(PdfObject? value)
    {
        if (value is not PdfReference reference) return value ?? PdfNull.Value;
        _token.ThrowIfCancellationRequested();
        if (_objects.TryGetValue(reference.Id, out var cached)) return cached;
        if (!_xref.TryGetValue(reference.Id.Number, out var entry) || entry.Type == 0) return PdfNull.Value;
        if (entry.Type == 1 && entry.GenerationOrIndex != reference.Id.Generation || entry.Type == 2 && reference.Id.Generation != 0) return PdfNull.Value;
        if (!_loading.Add(reference.Id) || _loading.Count > Limits.MaximumDepth) throw new InvalidDataException("Cyclic indirect resolution or object-stream dependency.");
        try
        {
            PdfObject result;
            if (entry.Type == 1)
            {
                result = ReadIndirect(entry.Offset, reference.Id, allowIndirectLength: true);
                if (_security is not null && !reference.Equals(_encryptionReference) && !(result is PdfStream stream && stream.Dictionary.Name("Type") == "XRef"))
                    result = _security.Transform(result, reference.Id, encrypt: false, Limits.MaximumDepth);
            }
            else
            {
                var streamId = checked((int)entry.Offset);
                if (!_objectStreams.TryGetValue(streamId, out var contents))
                {
                    if (!_xref.TryGetValue(streamId, out var streamEntry) || streamEntry.Type != 1) throw new InvalidDataException("Object stream must be uncompressed.");
                    var container = Resolve(new PdfReference(streamId, streamEntry.GenerationOrIndex)) as PdfStream ?? throw new InvalidDataException("Missing object stream.");
                    if (container.Dictionary.Name("Type") != "ObjStm") throw new InvalidDataException("Wrong object-stream type.");
                    var data = Decode(container); var count = Integer(container.Dictionary["N"]); var first = Integer(container.Dictionary["First"]);
                    if (count < 0 || count > Limits.MaximumObjects || first < 0 || first > data.Length) throw new InvalidDataException("Invalid object-stream header.");
                    var reader = new PdfSyntaxReader(data, 0, Limits, _token, first); var offsets = new List<(int Id, int Offset)>();
                    for (var i = 0; i < count; i++)
                    {
                        var id = checked((int)reader.ReadInteger()); var offset = checked((int)reader.ReadInteger());
                        if (id <= 0 || offset < 0 || (long)first + offset >= data.Length || i > 0 && offset <= offsets[^1].Offset) throw new InvalidDataException("Invalid compressed object index.");
                        offsets.Add((id, offset));
                    }
                    if (!reader.End) throw new InvalidDataException("Unexpected object-stream header data.");
                    contents = [];
                    for (var i = 0; i < offsets.Count; i++)
                    {
                        var end = i + 1 < offsets.Count ? first + offsets[i + 1].Offset : data.Length;
                        var objectReader = new PdfSyntaxReader(data, first + offsets[i].Offset, Limits, _token, end);
                        var item = objectReader.ReadObject();
                        if (!objectReader.End || !contents.TryAdd(offsets[i].Id, item)) throw new InvalidDataException("Invalid or duplicate compressed object.");
                        if (_xref.TryGetValue(offsets[i].Id, out var itemEntry) && itemEntry.Type == 2 && itemEntry.Offset == streamId && itemEntry.GenerationOrIndex != i)
                            throw new InvalidDataException("Compressed object index disagrees with xref.");
                    }
                    _objectStreams[streamId] = contents;
                }
                if (!contents.TryGetValue(reference.Id.Number, out result!)) throw new InvalidDataException("Compressed object is absent from its container.");
            }
            _objects.Add(reference.Id, result); return result;
        }
        finally { _loading.Remove(reference.Id); }
    }
    public PdfDictionary Dictionary(PdfObject value) => Resolve(value) as PdfDictionary ?? throw new InvalidDataException("Expected PDF dictionary.");
    public PdfArray Array(PdfObject value) => Resolve(value) as PdfArray ?? throw new InvalidDataException("Expected PDF array.");
    public int Integer(PdfObject value) => Resolve(value) is PdfNumber number ? checked((int)number.Integer) : throw new InvalidDataException("Expected PDF integer.");
    public byte[] Decode(PdfStream stream)
    {
        var remaining = Math.Min(Limits.MaximumDecodedStreamBytes, Limits.MaximumTotalDecodedBytes - _decoded);
        if (remaining < 1) throw new InvalidDataException("Total decoded-stream budget exhausted.");
        var bytes = PdfFilters.Decode(stream, Resolve, (int)remaining, _token); _decoded += bytes.Length; return bytes;
    }
    public PdfReference Add(PdfObject value)
    {
        if (_maximumObject >= Limits.MaximumObjects) throw new InvalidDataException("PDF object-count budget exceeded.");
        var reference = new PdfReference(++_maximumObject); _objects[reference.Id] = value; return reference;
    }
    public void Set(PdfReference reference, PdfObject value)
    {
        if (reference.Id.Number > Limits.MaximumObjects) throw new InvalidDataException("PDF object-count budget exceeded.");
        _maximumObject = Math.Max(_maximumObject, reference.Id.Number); _objects[reference.Id] = value;
    }
    public IEnumerable<(PdfReference Reference, PdfObject Value)> EnumerateObjects()
    {
        var ids = _xref.Where(p => p.Value.Type is 1 or 2).Select(p => new PdfObjectId(p.Key, p.Value.Type == 1 ? p.Value.GenerationOrIndex : 0))
            .Concat(_objects.Keys).Distinct().ToArray();
        foreach (var id in ids) { var reference = new PdfReference(id.Number, id.Generation); yield return (reference, Resolve(reference)); }
    }
    public void ChangeEncryption(string? userPassword, string? ownerPassword = null, bool allowPrinting = true, bool allowCopy = false)
    {
        if (!IsOwnerAuthorized) throw new UnauthorizedAccessException("The owner password is required to change encryption.");
        // Resolve first, while the original object encryption key is still installed.
        foreach (var item in EnumerateObjects()) { _ = item.Value; }
        _objectStreams.Clear();
        if (userPassword is null) { _security = null; _encryptionReference = null; Trailer.Remove("Encrypt"); return; }
        _security = PdfSecurity.Create(userPassword, ownerPassword ?? throw new ArgumentNullException(nameof(ownerPassword)), allowPrinting, allowCopy, _token);
        _encryptionReference = Add(_security.Dictionary); Trailer["Encrypt"] = _encryptionReference;
        if (string.CompareOrdinal(Version, "1.7") < 0) Version = "1.7";
        if (Version == "1.7")
        {
            // R6 is defined by PDF 1.7 extension level 8 as well as PDF 2.0.
            // Explicitly declare it; retain the base header for independently validated 1.7 readers.
            var extensions = Catalog["Extensions"] is PdfNull ? new PdfDictionary() : Dictionary(Catalog["Extensions"]).Copy();
            var adobe = extensions["ADBE"] is PdfNull ? new PdfDictionary() : Dictionary(extensions["ADBE"]).Copy();
            adobe["BaseVersion"] = new PdfName("1.7");
            adobe["ExtensionLevel"] = new PdfNumber(Math.Max(8, adobe.Number("ExtensionLevel")));
            extensions["ADBE"] = adobe;
            Catalog["Extensions"] = extensions;
        }
    }
    private PdfObject ReadIndirect(long offset, PdfObjectId? expected, bool allowIndirectLength)
    {
        if (offset < 0 || offset >= _source.LongLength) throw new InvalidDataException("Cross-reference offset is outside file.");
        if (_source[(int)offset] is < (byte)'0' or > (byte)'9') throw new InvalidDataException("Cross-reference offset must identify the start of an indirect object header.");
        var reader = new PdfSyntaxReader(_source, (int)offset, Limits, _token);
        var number = reader.ReadInteger(); var generation = reader.ReadInteger();
        if (number <= 0 || number > Limits.MaximumObjects || generation is < 0 or > 65535 || !reader.TryKeyword("obj") ||
            expected is { } id && (number != id.Number || generation != id.Generation)) throw new InvalidDataException("Cross-reference entry does not match object header.");
        var value = reader.ReadObject();
        if (value is PdfDictionary dictionary && reader.TryKeyword("stream"))
        {
            var length = dictionary["Length"];
            if (length is PdfReference && !allowIndirectLength) throw new InvalidDataException("Cross-reference streams require a direct length.");
            var size = length is PdfNumber n ? checked((int)n.Integer) : Integer(length);
            value = new PdfStream(dictionary, reader.ReadStreamBytes(size));
        }
        if (!reader.TryKeyword("endobj")) throw new InvalidDataException("Missing endobj.");
        return value;
    }
    private void ReadCrossReferences(long offset, HashSet<long> visited)
    {
        _revisionCount++;
        _token.ThrowIfCancellationRequested();
        if (offset < 0 || offset >= _source.Length || !visited.Add(offset) || visited.Count > Limits.MaximumRevisions) throw new InvalidDataException("Cyclic, missing or excessive cross-reference revisions.");
        var entries = new Dictionary<int, XrefEntry>(); var reader = new PdfSyntaxReader(_source, (int)offset, Limits, _token);
        PdfDictionary trailer;
        if (reader.TryKeyword("xref"))
        {
            while (!reader.TryKeyword("trailer"))
            {
                var first = reader.ReadInteger(); var count = reader.ReadInteger();
                if (first < 0 || count < 0 || first + count > (long)Limits.MaximumObjects + 1) throw new InvalidDataException("Invalid xref subsection size.");
                for (var i = 0; i < count; i++)
                {
                    var location = reader.ReadInteger(); var generation = reader.ReadInteger(); var marker = reader.ReadKeyword();
                    if (generation is < 0 or > 65535 || location < 0 || marker is not ("n" or "f")) throw new InvalidDataException("Invalid xref table entry.");
                    if (!entries.TryAdd((int)first + i, new XrefEntry(marker == "n" ? 1 : 0, location, (int)generation))) throw new InvalidDataException("Overlapping xref subsections.");
                }
            }
            trailer = reader.ReadObject() as PdfDictionary ?? throw new InvalidDataException("Invalid PDF trailer.");
            if (trailer["XRefStm"] is PdfNumber supplement)
            {
                if (!visited.Add(supplement.Integer) || visited.Count > Limits.MaximumRevisions) throw new InvalidDataException("Cyclic hybrid xref.");
                ReadXrefStream(supplement.Integer, entries);
            }
        }
        else trailer = ReadXrefStream(offset, entries);
        foreach (var item in entries) _xref.TryAdd(item.Key, item.Value);
        foreach (var item in trailer) if (!Trailer.Contains(item.Key)) Trailer[item.Key] = item.Value;
        if (trailer["Prev"] is PdfNumber previous) ReadCrossReferences(previous.Integer, visited);
    }
    private PdfDictionary ReadXrefStream(long offset, Dictionary<int, XrefEntry> entries)
    {
        var stream = ReadIndirect(offset, null, allowIndirectLength: false) as PdfStream ?? throw new InvalidDataException("Expected cross-reference stream.");
        var dict = stream.Dictionary; if (dict.Name("Type") != "XRef") throw new InvalidDataException("Incorrect cross-reference stream type.");
        var widths = dict["W"] as PdfArray ?? throw new InvalidDataException("Missing xref widths.");
        if (widths.Count != 3 || widths.Any(v => v is not PdfNumber n || n.Value < 0 || n.Value > 8 || n.Value != Math.Truncate(n.Value))) throw new InvalidDataException("Invalid xref widths.");
        var w = widths.Select(v => (int)((PdfNumber)v).Integer).ToArray(); if (w.Sum() == 0) throw new InvalidDataException("Empty xref fields.");
        var size = dict["Size"] is PdfNumber sizeNumber ? checked((int)sizeNumber.Integer) : throw new InvalidDataException("Missing xref size.");
        if (size < 1 || size > Limits.MaximumObjects + 1L) throw new InvalidDataException("Invalid xref object limit.");
        var index = dict["Index"] as PdfArray ?? new PdfArray(new PdfNumber(0L), new PdfNumber(size));
        if (index.Count % 2 != 0) throw new InvalidDataException("Invalid xref index.");
        var bytes = Decode(stream); var position = 0;
        long Read(int length)
        {
            ulong value = 0; for (var i = 0; i < length; i++) { if (position >= bytes.Length) throw new InvalidDataException("Truncated cross-reference stream."); value = (value << 8) | bytes[position++]; }
            return value <= long.MaxValue ? (long)value : throw new InvalidDataException("Cross-reference value overflow.");
        }
        var seen = new HashSet<int>();
        for (var i = 0; i < index.Count; i += 2)
        {
            var first = index[i] is PdfNumber a ? checked((int)a.Integer) : -1; var count = index[i + 1] is PdfNumber b ? checked((int)b.Integer) : -1;
            if (first < 0 || count < 0 || (long)first + count > size) throw new InvalidDataException("Cross-reference index exceeds Size.");
            for (var j = 0; j < count; j++)
            {
                var type = w[0] == 0 ? 1 : Read(w[0]); var location = Read(w[1]); var extra = Read(w[2]);
                if (!seen.Add(first + j)) throw new InvalidDataException("Overlapping xref stream subsections.");
                if (extra > int.MaxValue || type is < 0 or > 2 || type is 0 or 1 && extra > 65535) throw new InvalidDataException("Unsupported xref entry.");
                entries[first + j] = new XrefEntry((int)type, location, (int)extra);
            }
        }
        if (position != bytes.Length) throw new InvalidDataException("Trailing cross-reference stream data.");
        return dict;
    }
    public byte[] Save(int? maximumBytes = null, bool incremental = false)
    {
        if (!IsOwnerAuthorized) throw new UnauthorizedAccessException("Rewriting a PDF requires owner-authorized access.");
        if (incremental && _source.Length == 0) throw new InvalidOperationException("An incremental save needs original bytes.");
        if (Version is not ("1.0" or "1.1" or "1.2" or "1.3" or "1.4" or "1.5" or "1.6" or "1.7" or "2.0")) throw new ArgumentException("Unsupported PDF output version.");
        var maxBytes = maximumBytes ?? Limits.MaximumFileBytes;
        if (maxBytes < 1) throw new ArgumentOutOfRangeException(nameof(maximumBytes));
        var trailer = Trailer.Copy();
        foreach (var key in new[] { "Prev", "XRefStm", "Type", "W", "Index", "Length", "Filter", "DecodeParms", "Size" }) trailer.Remove(key);
        var reachable = new SortedDictionary<int, (PdfReference Reference, PdfObject Value)>();
        var pending = new Queue<PdfReference>();
        void Collect(PdfObject value, int depth = 0)
        {
            if (depth > Limits.MaximumDepth) throw new InvalidDataException("Excessive direct-object nesting.");
            if (value is PdfReference reference) pending.Enqueue(reference);
            else if (value is PdfArray array) foreach (var child in array) Collect(child, depth + 1);
            else if (value is PdfDictionary dictionary) foreach (var item in dictionary) Collect(item.Value, depth + 1);
            else if (value is PdfStream stream) foreach (var item in stream.Dictionary) if (item.Key != "Length") Collect(item.Value, depth + 1);
        }
        Collect(trailer);
        while (pending.TryDequeue(out var reference))
        {
            _token.ThrowIfCancellationRequested();
            if (reachable.TryGetValue(reference.Id.Number, out var existing))
            {
                if (reference.Id != existing.Reference.Id) throw new InvalidDataException("Conflicting object generations in reachable graph.");
                continue;
            }
            var value = Resolve(reference); reachable[reference.Id.Number] = (reference, value); Collect(value);
            if (reachable.Count > Limits.MaximumObjects) throw new InvalidDataException("Reachable object budget exceeded.");
        }
        using var output = new PdfBoundedStream(maxBytes, _token);
        if (incremental) { output.Write(_source); PdfObjectWriter.Text(output, "\n"); }
        else { PdfObjectWriter.Text(output, "%PDF-" + Version + "\n%"); output.Write(new byte[] { 226, 227, 207, 211, 10 }); }
        var offsets = new Dictionary<int, (long Offset, int Generation)>();
        foreach (var (number, item) in reachable)
        {
            _token.ThrowIfCancellationRequested(); offsets[number] = (output.Position, item.Reference.Id.Generation);
            PdfObjectWriter.Text(output, $"{number} {item.Reference.Id.Generation} obj\n");
            var value = _security is not null && !item.Reference.Equals(_encryptionReference)
                ? _security.Transform(item.Value, item.Reference.Id, encrypt: true, Limits.MaximumDepth) : item.Value;
            PdfObjectWriter.Write(output, value, Limits.MaximumDepth); PdfObjectWriter.Text(output, "\nendobj\n");
        }
        var xrefOffset = output.Position; var size = _maximumObject + 1;
        // Link every unused object into the free list, including newly unreachable source objects.
        // A freed live object advances its generation; existing free entries retain theirs.
        var free = Enumerable.Range(1, Math.Max(0, size - 1)).Where(number => !offsets.ContainsKey(number)).ToArray();
        var nextFree = free.Select((number, index) => (number, next: index + 1 < free.Length ? free[index + 1] : 0))
            .ToDictionary(item => item.number, item => item.next);
        PdfObjectWriter.Text(output, $"xref\n0 {size}\n" + (free.Length == 0 ? 0 : free[0]).ToString("D10", CultureInfo.InvariantCulture) + " 65535 f \n");
        for (var i = 1; i < size; i++)
        {
            if (offsets.TryGetValue(i, out var item))
                PdfObjectWriter.Text(output, item.Offset.ToString("D10", CultureInfo.InvariantCulture) + " " + item.Generation.ToString("D5", CultureInfo.InvariantCulture) + " n \n");
            else
            {
                var generation = _xref.TryGetValue(i, out var previous)
                    ? previous.Type == 0 ? previous.GenerationOrIndex : Math.Min(65535, previous.Type == 2 ? 1 : previous.GenerationOrIndex + 1)
                    : 0;
                PdfObjectWriter.Text(output, nextFree[i].ToString("D10", CultureInfo.InvariantCulture) + " " + generation.ToString("D5", CultureInfo.InvariantCulture) + " f \n");
            }
        }
        trailer["Size"] = new PdfNumber(size); if (incremental) trailer["Prev"] = new PdfNumber(LastCrossReferenceOffset);
        PdfObjectWriter.Text(output, "trailer\n"); PdfObjectWriter.Write(output, trailer, Limits.MaximumDepth);
        PdfObjectWriter.Text(output, $"\nstartxref\n{xrefOffset}\n%%EOF\n"); return output.ToArray();
    }
}

internal sealed class PdfBoundedStream(long maximum, CancellationToken token) : MemoryStream
{
    private void Check(long length) { token.ThrowIfCancellationRequested(); if (length < 0 || length > maximum) throw new InvalidDataException("PDF output exceeds configured byte budget."); }
    public override void Write(byte[] buffer, int offset, int count) { Check(Position + count); base.Write(buffer, offset, count); }
    public override void Write(ReadOnlySpan<byte> buffer) { Check(Position + buffer.Length); base.Write(buffer); }
    public override void WriteByte(byte value) { Check(Position + 1); base.WriteByte(value); }
    public override void SetLength(long value) { Check(value); base.SetLength(value); }
}
