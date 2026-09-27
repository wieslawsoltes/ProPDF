using ProPDF.Core;
using ProPDF.Kernel;

namespace ProPDF.Editing;

public sealed partial class ManagedPdfEditor
{
    public Task<PdfDocumentInspection> InspectAsync(PdfSnapshot source, CancellationToken cancellationToken = default) => Task.Run(() =>
    {
        var graph = Open(source, cancellationToken); var annotations = new List<PdfAnnotationInfo>();
        for (var number = 1; number <= graph.Pages.Count; number++)
        {
            cancellationToken.ThrowIfCancellationRequested(); var page = graph.Page(number);
            foreach (var (raw, annotation) in graph.Annotations(page))
            {
                if (annotations.Count >= 100_000) throw new InvalidDataException("Annotation inspection limit exceeded.");
                annotations.Add(new PdfAnnotationInfo(number, PdfGraph.AnnotationId(raw, annotation), annotation.Name("Subtype") ?? "Unknown",
                    page.Transform.ToView(PdfGraph.Rectangle(graph.File, annotation["Rect"])), annotation.Text("Contents"), annotation.Text("T")));
            }
        }
        static string Text(PdfObject value) => value switch { PdfString text => text.Text, PdfName name => name.Value, PdfArray array => string.Join(", ", array.Select(Text)), _ => "" };
        var fields = Fields(graph).Select(field => new PdfFormFieldInfo(field.Name, field.Effective.Name("FT") ?? "Unknown", Text(graph.Resolve(field.Effective["V"])),
            ((int)field.Effective.Number("Ff") & 1) != 0, ((int)field.Effective.Number("Ff") & 2) != 0)).ToArray();
        var attachments = Names(graph, "EmbeddedFiles").Select(item =>
        {
            var spec = graph.Dictionary(item.Value); return new PdfAttachmentInfo(item.Key, spec.Text("UF", spec.Text("F", item.Key)));
        }).ToArray();
        var bookmarks = OutlineEntries(ReadOutlineTree(graph, new PdfNavigationOptions())).Select(entry => new PdfBookmarkInfo(entry.Node.Dictionary.Text("Title"), entry.Depth)).ToArray();
        return new PdfDocumentInspection(annotations.AsReadOnly(), Array.AsReadOnly(fields), Array.AsReadOnly(attachments), Array.AsReadOnly(bookmarks),
            graph.File.IsEncrypted, HasSignatures(graph), graph.OptionalDictionary(graph.File.Catalog["AcroForm"]).Contains("XFA"));
    }, cancellationToken);
}
