using SkiaSharp;
using SkiaSharp.HarfBuzz;
using ProPDF.Engine.PdfPig.Compatibility.Helpers;

namespace ProPDF.Engine.PdfPig;

/// <summary>Owned, bounded cache of substitute-font glyph outlines in PDF em coordinates.
/// Font programs are supplied by the host; this class never loads or bundles font files.</summary>
internal sealed class PdfFallbackGlyphCache : IDisposable
{
    private const float Em = 1000;
    private readonly object _gate = new();
    private readonly SKFont _font;
    private readonly SKShaper _shaper;
    private readonly int _maximumEntries;
    private readonly long _maximumBytes;
    private readonly Dictionary<(string Text, bool Vertical), LinkedListNode<Entry>> _entries = [];
    private readonly LinkedList<Entry> _lru = [];
    private long _bytes, _hits, _misses;
    private bool _disposed;

    public PdfFallbackGlyphCache(SKTypeface typeface, int maximumEntries = 256, long maximumBytes = 512 * 1024)
    {
        ArgumentNullException.ThrowIfNull(typeface);
        if (maximumEntries is < 0 or > 65536 || maximumBytes is < 0 or > 64 * 1024 * 1024)
            throw new ArgumentOutOfRangeException(nameof(maximumEntries));
        _maximumEntries = maximumEntries; _maximumBytes = maximumBytes;
        // Shape at a design-space size, not one device pixel. PDF supplies the final scale.
        _font = new SKFont(typeface, Em) { Hinting = SKFontHinting.None, LinearMetrics = true, Subpixel = true, EmbeddedBitmaps = false };
        try { _shaper = new SKShaper(typeface); }
        catch { _font.Dispose(); throw; }
    }

    internal (int Entries, long Bytes, long Hits, long Misses) Statistics
    { get { lock (_gate) return (_entries.Count, _bytes, _hits, _misses); } }

    public Lease Acquire(string text, bool vertical = false)
    {
        ArgumentNullException.ThrowIfNull(text);
        if (text.Length > 1024) throw new NotSupportedException("A substitute PDF glyph mapping exceeds 1024 UTF-16 code units.");
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            var key = (text, vertical);
            if (_entries.TryGetValue(key, out var node))
            {
                _hits++; _lru.Remove(node); _lru.AddFirst(node);
                return new Lease(node.Value);
            }
            _misses++;
            var entry = Build(key);
            if (_maximumEntries > 0 && entry.Bytes <= _maximumBytes)
            {
                while (_entries.Count >= _maximumEntries || _bytes + entry.Bytes > _maximumBytes) EvictLast();
                _entries.Add(key, _lru.AddFirst(entry)); _bytes += entry.Bytes;
                return new Lease(entry);
            }
            // A large glyph is usable for this call without escaping the cache budget.
            var transient = new Lease(entry); entry.Release(); return transient;
        }
    }

    private Entry Build((string Text, bool Vertical) key)
    {
        var shaped = key.Vertical ? _shaper.ShapeVertical(key.Text, _font) : _shaper.Shape(key.Text, _font);
        if (!float.IsFinite(shaped.Width)) throw new InvalidDataException("Non-finite substitute glyph advance.");
        var combined = new SKPath();
        try
        {
            var complete = true;
            for (var i = 0; i < shaped.Codepoints.Length; i++)
            {
                if (shaped.Codepoints[i] > ushort.MaxValue) throw new InvalidDataException("Substitute glyph id is outside the TrueType range.");
                using var glyph = _font.GetGlyphPath((ushort)shaped.Codepoints[i]);
                if (glyph is null) { complete = false; continue; }
                var position = shaped.Points[i];
                if (!float.IsFinite(position.X) || !float.IsFinite(position.Y)) throw new InvalidDataException("Non-finite substitute glyph position.");
                var matrix = SKMatrix.CreateTranslation(position.X, position.Y);
                combined.AddPath(glyph, in matrix);
            }
            // Flip Skia's downwards Y axis once, here. The ordinary PDF vector paint pipeline
            // then owns text transforms, stroke widths, patterns, clipping and soft masks.
            combined.Transform(SKMatrix.CreateScale(1 / Em, -1 / Em));
            var bytes = checked(128L + key.Text.Length * 2L + combined.PointCount * 16L + shaped.Codepoints.Length * 8L);
            if (!complete) { combined.Dispose(); combined = null; }
            return new Entry(key, combined, shaped.Width / Em, bytes);
        }
        catch { combined?.Dispose(); throw; }
    }

    private void EvictLast()
    {
        var node = _lru.Last!; _lru.Remove(node); _entries.Remove(node.Value.Key);
        _bytes -= node.Value.Bytes; node.Value.Release();
    }

    /// <summary>Fit the substitute advance, not its ink box, to the PDF's displacement.
    /// Tc, Tw, TJ, Tz, rise and pen movement remain the interpreter's responsibility.</summary>
    internal static float HorizontalScale(double pdfAdvance, float substituteAdvance, bool vertical)
    {
        if (vertical || !double.IsFinite(pdfAdvance) || !float.IsFinite(substituteAdvance) ||
            pdfAdvance <= 0 || substituteAdvance <= 1e-9f) return 1;
        var ratio = pdfAdvance / substituteAdvance;
        if (!double.IsFinite(ratio) || ratio is < 1e-6 or > 1e6)
            throw new InvalidDataException("Substitute glyph width scaling is outside supported limits.");
        return (float)ratio;
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
            while (_lru.Count > 0) EvictLast();
            _shaper.Dispose(); _font.Dispose();
        }
    }

    internal sealed class Entry((string Text, bool Vertical) key, SKPath? path, float advance, long bytes)
    {
        private int _references = 1;
        internal (string Text, bool Vertical) Key { get; } = key;
        internal SKPath? Path { get; } = path;
        internal float Advance { get; } = advance;
        internal long Bytes { get; } = bytes;
        internal void Retain() => Interlocked.Increment(ref _references);
        internal void Release() { if (Interlocked.Decrement(ref _references) == 0) Path?.Dispose(); }
    }

    /// <summary>Immutable outline loan. Eviction or cache disposal cannot invalidate an active loan.</summary>
    internal sealed class Lease : IDisposable
    {
        private Entry? _entry;
        internal Lease(Entry entry) { _entry = entry; entry.Retain(); }
        internal SKPath? Path => (_entry ?? throw new ObjectDisposedException(nameof(Lease))).Path;
        internal float Advance => (_entry ?? throw new ObjectDisposedException(nameof(Lease))).Advance;
        public void Dispose() => Interlocked.Exchange(ref _entry, null)?.Release();
    }
}
