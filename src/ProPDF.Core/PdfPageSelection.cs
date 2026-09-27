using System.Collections.Immutable;
using System.Globalization;

namespace ProPDF.Core;

/// <summary>Parses one-based page selections such as "1-3,7,last", "odd" or "even". Order is retained and duplicates are removed.</summary>
public static class PdfPageSelection
{
    public static ImmutableArray<int> Parse(string? expression, int pageCount, int maximumSelection = 100_000)
    {
        if (pageCount is < 1 or > 100_000 || maximumSelection is < 1 or > 100_000)
            throw new ArgumentOutOfRangeException(nameof(pageCount));
        expression = string.IsNullOrWhiteSpace(expression) ? "all" : expression.Trim();
        if (expression.Length > 4096) throw new ArgumentOutOfRangeException(nameof(expression));
        var result = ImmutableArray.CreateBuilder<int>();
        var seen = new HashSet<int>();
        void Add(int page)
        {
            if (page < 1 || page > pageCount) throw new ArgumentOutOfRangeException(nameof(expression), $"Page {page} is outside 1–{pageCount}.");
            if (!seen.Add(page)) return;
            if (result.Count == maximumSelection) throw new ArgumentException("The page selection exceeds its configured limit.", nameof(expression));
            result.Add(page);
        }
        int Number(string value) => value.Trim().Equals("last", StringComparison.OrdinalIgnoreCase) ? pageCount :
            int.TryParse(value.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out var number) ? number :
            throw new FormatException($"Invalid page number: {value}");
        foreach (var part in expression.Split(','))
        {
            var token = part.Trim();
            if (token.Equals("all", StringComparison.OrdinalIgnoreCase) || token.Equals("odd", StringComparison.OrdinalIgnoreCase) || token.Equals("even", StringComparison.OrdinalIgnoreCase))
            {
                var odd = token.Equals("odd", StringComparison.OrdinalIgnoreCase);
                var even = token.Equals("even", StringComparison.OrdinalIgnoreCase);
                for (var page = even ? 2 : 1; page <= pageCount; page += odd || even ? 2 : 1) Add(page);
                continue;
            }
            var range = token.Split('-');
            if (range.Length == 1) { Add(Number(token)); continue; }
            if (range.Length != 2) throw new FormatException($"Invalid page range: {token}");
            var first = Number(range[0]);
            var last = Number(range[1]);
            if (first < 1 || last < 1 || first > pageCount || last > pageCount) throw new ArgumentOutOfRangeException(nameof(expression));
            var direction = last >= first ? 1 : -1;
            for (var page = first; ; page += direction)
            {
                Add(page);
                if (page == last) break;
            }
        }
        if (result.Count == 0) throw new ArgumentException("The selection contains no pages.", nameof(expression));
        return result.ToImmutable();
    }
}
