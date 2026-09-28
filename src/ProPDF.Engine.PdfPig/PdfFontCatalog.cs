using SkiaSharp;

namespace ProPDF.Engine.PdfPig;

/// <summary>A caller-supplied font program. Bytes are copied, never exposed, embedded in a PDF,
/// fetched from a network, or obtained from the operating system by this object.</summary>
public sealed class PdfFontFace
{
    private readonly byte[] _program;
    public string Family { get; }
    public bool Bold { get; }
    public bool Italic { get; }
    public int ByteLength => _program.Length;

    public PdfFontFace(string family, ReadOnlySpan<byte> program, bool bold = false, bool italic = false)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(family);
        if (family.Length > 256 || family.Any(char.IsControl)) throw new ArgumentOutOfRangeException(nameof(family));
        if (program.Length is < 12 or > 16 * 1024 * 1024) throw new ArgumentOutOfRangeException(nameof(program));
        Family = family.Trim(); Bold = bold; Italic = italic; _program = program.ToArray();
        // Validate before a catalog can be attached to a rendering document.
        using var face = Open();
    }

    internal SKTypeface Open()
    {
        using var data = SKData.CreateCopy(_program);
        return SKTypeface.FromData(data) ?? throw new InvalidDataException("The supplied font program could not be decoded.");
    }
}

/// <summary>Immutable, instance-scoped fallback fonts for deterministic rendering in hosts without
/// system fonts. Embedded PDF outlines take precedence. This is substitution, not font recovery.</summary>
public sealed class PdfFontCatalog
{
    private readonly Dictionary<(string Family, bool Bold, bool Italic), PdfFontFace> _faces = [];
    private readonly string? _fallbackFamily;
    private readonly Dictionary<string, string> _aliases = new(StringComparer.Ordinal);
    private static readonly string[] StyleSuffixes = ["-BoldItalic", "-BoldOblique", "-Regular", "-Italic", "-Oblique", "-Bold", ",BoldItalic", ",Italic", ",Bold"];
    public int Count => _faces.Count;
    public long ByteLength { get; }

    public PdfFontCatalog(IEnumerable<PdfFontFace> faces, string? fallbackFamily = null,
        IReadOnlyDictionary<string, string>? aliases = null)
    {
        ArgumentNullException.ThrowIfNull(faces);
        long bytes = 0;
        foreach (var face in faces)
        {
            ArgumentNullException.ThrowIfNull(face);
            if (_faces.Count >= 64 || (bytes += face.ByteLength) > 64L * 1024 * 1024)
                throw new ArgumentOutOfRangeException(nameof(faces), "Font catalogs are limited to 64 faces and 64 MiB.");
            if (!_faces.TryAdd((Normalize(face.Family), face.Bold, face.Italic), face))
                throw new ArgumentException("A family/style pair occurs more than once.", nameof(faces));
        }
        ByteLength = bytes;
        if (aliases is not null)
            foreach (var (alias, family) in aliases)
            {
                ArgumentException.ThrowIfNullOrWhiteSpace(alias); ArgumentException.ThrowIfNullOrWhiteSpace(family);
                if (_aliases.Count >= 64 || alias.Length > 256 || family.Length > 256 || alias.Any(char.IsControl) || family.Any(char.IsControl))
                    throw new ArgumentOutOfRangeException(nameof(aliases));
                var normalizedAlias = Normalize(alias); var target = Normalize(family);
                if (!_faces.Keys.Any(key => key.Family == target) || _faces.Keys.Any(key => key.Family == normalizedAlias) || !_aliases.TryAdd(normalizedAlias, target))
                    throw new ArgumentException("Aliases must be unique and point directly to an available family, without replacing it.", nameof(aliases));
            }
        if (fallbackFamily is not null)
        {
            ArgumentException.ThrowIfNullOrWhiteSpace(fallbackFamily);
            _fallbackFamily = Normalize(fallbackFamily);
            if (!_faces.Keys.Any(key => key.Family == _fallbackFamily))
                throw new ArgumentException("The fallback family is not present in this catalog.", nameof(fallbackFamily));
        }
    }

    /// <summary>Resolve an exact family/style or the configured fallback family/style.
    /// A missing style is not silently replaced with regular. Returned data remains immutable.</summary>
    public PdfFontFace? FindFace(string pdfFontName, bool bold = false, bool italic = false)
    {
        ArgumentNullException.ThrowIfNull(pdfFontName);
        var family = Normalize(pdfFontName);
        if (_aliases.TryGetValue(family, out var alias)) family = alias;
        if (_faces.TryGetValue((family, bold, italic), out var face)) return face;
        return _fallbackFamily is not null && _faces.TryGetValue((_fallbackFamily, bold, italic), out face) ? face : null;
    }

    private static string Normalize(string name)
    {
        if (name.Length > 7 && name[6] == '+' && name.Take(6).All(c => c is >= 'A' and <= 'Z')) name = name[7..];
        foreach (var suffix in StyleSuffixes)
            if (name.EndsWith(suffix, StringComparison.OrdinalIgnoreCase)) { name = name[..^suffix.Length]; break; }
        return name.Trim().ToUpperInvariant();
    }
}
