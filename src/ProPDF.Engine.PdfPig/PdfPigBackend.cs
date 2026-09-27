using System.Text;
using System.Text.RegularExpressions;
using ProPDF.Core;
using ProPDF.Rendering.Skia;
using SkiaSharp;
using UglyToad.PdfPig;
using ProPDF.Engine.PdfPig.Compatibility;
using PigDocument = UglyToad.PdfPig.PdfDocument;

namespace ProPDF.Engine.PdfPig;

/// <summary>Each extraction operation has its own parser; rendering parsers are owned by the renderer's serial worker.</summary>
public sealed class PdfPigBackend : IPdfDocumentLoader, IPdfTextService, ISkiaPdfDocumentFactory
{
    public async Task<PdfSnapshot> OpenAsync(Stream source, PdfOpenOptions? options = null, CancellationToken cancellationToken = default)
    {
        options ??= new PdfOpenOptions();
        options.Validate();
        var bytes = await PdfStreams.ReadBoundedAsync(source, options.MaximumBytes, cancellationToken).ConfigureAwait(false);
        if (bytes.Length < 8 || Encoding.ASCII.GetString(bytes, 0, Math.Min(1024, bytes.Length)).IndexOf("%PDF-", StringComparison.Ordinal) < 0)
            throw new InvalidDataException("The input does not contain a PDF header.");
        return await Task.Run(() =>
        {
            using var document = PigDocument.Open(bytes, Options(options.Password, options.UseLenientParsing));
            if (document.NumberOfPages < 1 || document.NumberOfPages > options.MaximumPages)
                throw new InvalidDataException("PDF page count exceeds the configured limit or the document has no pages.");
            document.AddSkiaPageFactory();
            var pages = new PdfPageInfo[document.NumberOfPages];
            for (var number = 1; number <= pages.Length; number++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                // This factory does not parse page content streams merely to obtain dimensions.
                var size = document.GetPageSize(number);
                pages[number - 1] = new PdfPageInfo(number, new PdfSize(size.Width, size.Height));
            }
            var info = document.Information;
            cancellationToken.ThrowIfCancellationRequested();
            return new PdfSnapshot(bytes, pages, new PdfMetadata(info.Title ?? "", info.Author ?? "", info.Subject ?? "", info.Keywords ?? ""), options.Password);
        }, cancellationToken).ConfigureAwait(false);
    }

    public ISkiaPdfDocument Open(PdfSnapshot snapshot) => new RenderDocument(snapshot);

    public Task<PdfTextPage> GetPageTextAsync(PdfSnapshot source, int pageNumber, CancellationToken cancellationToken = default)
    {
        source.GetPage(pageNumber);
        return Task.Run(() =>
        {
            using var stream = source.OpenRead();
            using var document = PigDocument.Open(stream, Options(source.GetPassword()));
            return Extract(document, pageNumber, cancellationToken);
        }, cancellationToken);
    }

    public Task<IReadOnlyList<PdfSearchHit>> SearchAsync(PdfSnapshot source, string query, PdfSearchOptions? options = null, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(query);
        if (query.Length > 4096) throw new ArgumentOutOfRangeException(nameof(query));
        options ??= new PdfSearchOptions();
        if (options.MaximumResults is < 1 or > 100_000) throw new ArgumentOutOfRangeException(nameof(options));
        var settings = options;
        return Task.Run<IReadOnlyList<PdfSearchHit>>(() =>
        {
            using var stream = source.OpenRead();
            using var document = PigDocument.Open(stream, Options(source.GetPassword()));
            var flags = RegexOptions.CultureInvariant;
            if (!settings.MatchCase) flags |= RegexOptions.IgnoreCase;
            var pattern = settings.RegularExpression ? query : Regex.Escape(query);
            if (settings.WholeWord) pattern = @"(?<![\p{L}\p{N}\p{M}_])(?:" + pattern + @")(?![\p{L}\p{N}\p{M}_])";
            var expression = new Regex(pattern, flags, TimeSpan.FromMilliseconds(100));
            var hits = new List<PdfSearchHit>();
            for (var number = 1; number <= source.Pages.Count; number++)
            {
                var text = Extract(document, number, cancellationToken);
                foreach (Match match in expression.Matches(text.Text))
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    if (match.Length == 0) continue;
                    var bounds = text.Words.Where(word => word.StartIndex < match.Index + match.Length &&
                        word.StartIndex + word.Text.Length > match.Index).Select(word => word.Bounds).ToArray();
                    hits.Add(new PdfSearchHit(number, match.Index, match.Length, match.Value, Array.AsReadOnly(bounds)));
                    if (hits.Count >= settings.MaximumResults) return hits.AsReadOnly();
                }
            }
            return hits.AsReadOnly();
        }, cancellationToken);
    }

    private static PdfTextPage Extract(PigDocument document, int pageNumber, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var page = document.GetPage(pageNumber);
        var text = new StringBuilder();
        var words = new List<PdfWord>();
        foreach (var word in page.GetWords())
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (text.Length != 0) text.Append(' ');
            var bounds = word.BoundingBox;
            words.Add(new PdfWord(word.Text, new PdfRect(bounds.Left, page.Height - bounds.Top,
                Math.Max(0, bounds.Width), Math.Max(0, bounds.Height)), text.Length));
            text.Append(word.Text);
        }
        return new PdfTextPage(pageNumber, text.ToString(), words.AsReadOnly());
    }

    private static ParsingOptions Options(string? password, bool lenient = false) => new()
    {
        Password = password ?? string.Empty,
        UseLenientParsing = lenient,
        SkipMissingFonts = true,
        FilterProvider = SkiaRenderingFilterProvider.Instance
    };

    private sealed class RenderDocument : ISkiaPdfDocument
    {
        private readonly Stream _stream;
        private readonly PigDocument _document;
        public RenderDocument(PdfSnapshot source)
        {
            _stream = source.OpenRead();
            try
            {
                _document = PigDocument.Open(_stream, Options(source.GetPassword()));
                _document.AddSkiaPageFactory();
            }
            catch { _stream.Dispose(); throw; }
        }

        public SKPicture RecordPage(int pageNumber, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var picture = _document.GetPageAsSKPicture(pageNumber, cancellationToken);
            if (cancellationToken.IsCancellationRequested)
            {
                picture.Dispose();
                cancellationToken.ThrowIfCancellationRequested();
            }
            return picture;
        }

        public void Dispose()
        {
            _document.Dispose();
            _stream.Dispose();
        }
    }
}
