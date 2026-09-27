using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using ProPDF.Core;
using ProPDF.Kernel;
using static ProPDF.Editing.PdfGraph;
using static ProPDF.Kernel.PdfValues;

namespace ProPDF.Editing;

public sealed partial class ManagedPdfEditor
{
    private static string CreateTextBox(PdfGraph graph, AddTextBox box, CancellationToken token)
    {
        ArgumentNullException.ThrowIfNull(box.Text);
        if (box.Text.Length > 1_000_000 || !double.IsFinite(box.FontSize) || box.FontSize is < 1 or > 1000 ||
            !double.IsFinite(box.LineSpacing) || box.LineSpacing is < .5 or > 10 || !Enum.IsDefined(box.Alignment)) throw new ArgumentOutOfRangeException(nameof(box));
        var page = graph.Page(box.PageNumber); page.Validate(box.Bounds);
        var content = box.Text.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n').Replace("\t", "    ", StringComparison.Ordinal);
        var font = PdfFonts.Create(graph, box.StandardFont, box.EmbeddedFont, content);
        var metrics = ReadRedactionFont(graph, font.Reference);
        var fontDictionary = graph.Dictionary(font.Reference);
        if (fontDictionary.Name("Subtype") == "Type0") fontDictionary = graph.Dictionary(graph.OptionalArray(fontDictionary["DescendantFonts"])[0]);
        var descriptor = graph.OptionalDictionary(fontDictionary["FontDescriptor"]);
        var ascent = Math.Max(0, descriptor.Number("Ascent", 800)) * box.FontSize / 1000;
        var descent = Math.Max(0, -descriptor.Number("Descent", -200)) * box.FontSize / 1000;
        if (!double.IsFinite(ascent + descent) || ascent + descent <= 0) throw new InvalidDataException("Invalid font line metrics.");
        var lineHeight = Math.Max(ascent + descent, box.FontSize * box.LineSpacing);
        var capacity = (int)Math.Max(0, Math.Min(100_000, Math.Floor((box.Bounds.Height - ascent - descent + 1e-7) / lineHeight) + 1));
        var lines = new List<(string Text, double Width)>();
        double Width(string value)
        {
            var bytes = font.Encode(value); double width = 0;
            for (var i = 0; i < bytes.Length; i++) { var code = metrics.Composite ? (bytes[i++] << 8) | bytes[i] : bytes[i]; width += metrics.Width(code) * box.FontSize / 1000; }
            return width;
        }
        void Line(string text, double width)
        {
            if (lines.Count >= capacity) throw new InvalidOperationException("Text does not fit in the box. Enlarge it or reduce the font size; no text has been discarded.");
            lines.Add((text, width));
        }
        foreach (var paragraph in content.Split('\n'))
        {
            token.ThrowIfCancellationRequested(); var current = new StringBuilder(); double currentWidth = 0; var spaces = "";
            foreach (Match match in Regex.Matches(paragraph, @"[^ ]+| +", RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(100)))
            {
                token.ThrowIfCancellationRequested(); var word = match.Value;
                if (word[0] == ' ') { spaces += word; continue; }
                var wordWidth = Width(word); var spaceWidth = current.Length > 0 ? Width(spaces) : 0;
                if (current.Length > 0 && currentWidth + spaceWidth + wordWidth > box.Bounds.Width + 1e-7)
                { Line(current.ToString(), currentWidth); current.Clear(); currentWidth = 0; spaceWidth = 0; }
                if (wordWidth <= box.Bounds.Width + 1e-7)
                {
                    if (current.Length > 0) current.Append(spaces);
                    current.Append(word); currentWidth += spaceWidth + wordWidth;
                }
                else
                {
                    // Split oversized words at grapheme boundaries, never between a surrogate pair or combining sequence.
                    var elements = StringInfo.GetTextElementEnumerator(word);
                    while (elements.MoveNext())
                    {
                        token.ThrowIfCancellationRequested(); var element = elements.GetTextElement(); var width = Width(element);
                        if (width > box.Bounds.Width + 1e-7) throw new InvalidOperationException("A text element is wider than the text box.");
                        if (currentWidth + width > box.Bounds.Width + 1e-7) { Line(current.ToString(), currentWidth); current.Clear(); currentWidth = 0; }
                        current.Append(element); currentWidth += width;
                    }
                }
                spaces = "";
            }
            Line(current.ToString(), currentWidth);
        }
        var name = graph.Resource(page, "Font", font.Reference); var color = box.Color ?? PdfColor.Black;
        var opacity = graph.Resource(page, "ExtGState", Dictionary(("ca", new PdfNumber(color.A / 255d))));
        var output = new StringBuilder("q\n").Append(page.ViewMatrix()).Append(Rgb(color)).Append('/').Append(opacity).Append(" gs\n");
        for (var i = 0; i < lines.Count; i++)
        {
            token.ThrowIfCancellationRequested(); var line = lines[i];
            var x = box.Bounds.X + (box.Alignment == PdfTextAlignment.Center ? (box.Bounds.Width - line.Width) / 2 : box.Alignment == PdfTextAlignment.Right ? box.Bounds.Width - line.Width : 0);
            output.Append("BT\n0 Tc 0 Tw 100 Tz 0 Tr 0 Ts\n/").Append(name).Append(' ').Append(F(box.FontSize)).Append(" Tf\n1 0 0 -1 ")
                .Append(F(x)).Append(' ').Append(F(box.Bounds.Y + ascent + i * lineHeight)).Append(" Tm\n").Append(Hex(font.Encode(line.Text))).Append(" Tj\nET\n");
        }
        return output.Append("Q\n").ToString();
    }
}
