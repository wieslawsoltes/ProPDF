using iText.Forms;
using iText.Kernel.Pdf;
using iText.Signatures;
using ProPDF.Core;

namespace ProPDF.Editing.iText;

public sealed partial class ITextPdfEditor
{
    public Task<PdfDocumentInspection> InspectAsync(PdfSnapshot source, CancellationToken cancellationToken = default) => Task.Run(() =>
    {
        using var input = source.OpenRead();
        using var document = new PdfDocument(CreateReader(input, source.GetPassword()));
        var annotations = new List<PdfAnnotationInfo>();
        for (var number = 1; number <= document.GetNumberOfPages(); number++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var page = document.GetPage(number);
            var transform = Transform(page);
            foreach (var annotation in page.GetAnnotations())
            {
                if (annotations.Count >= 100_000) throw new InvalidDataException("Annotation inspection limit exceeded.");
                var rectangle = annotation.GetRectangle().ToRectangle();
                annotations.Add(new PdfAnnotationInfo(number, AnnotationId(annotation), annotation.GetSubtype().GetValue(),
                    transform.ToView(new PdfRect(rectangle.GetX(), rectangle.GetY(), Math.Max(0, rectangle.GetWidth()), Math.Max(0, rectangle.GetHeight()))),
                    annotation.GetContents()?.ToUnicodeString() ?? "", annotation.GetPdfObject().GetAsString(PdfName.T)?.ToUnicodeString() ?? ""));
            }
        }
        var form = PdfAcroForm.GetAcroForm(document, false);
        var fields = form?.GetAllFormFields().Select(pair => new PdfFormFieldInfo(pair.Key,
            pair.Value.GetFormType()?.GetValue() ?? "", pair.Value.GetValueAsString(), pair.Value.IsReadOnly(), pair.Value.IsRequired())).ToArray() ?? [];
        var attachments = document.GetCatalog().GetNameTree(PdfName.EmbeddedFiles).GetNames().Select(pair =>
        {
            var spec = pair.Value is PdfIndirectReference reference ? reference.GetRefersTo() : pair.Value;
            var dictionary = spec as PdfDictionary;
            return new PdfAttachmentInfo(pair.Key.ToUnicodeString(),
                dictionary?.GetAsString(PdfName.UF)?.ToUnicodeString() ?? dictionary?.GetAsString(PdfName.F)?.ToUnicodeString() ?? pair.Key.ToUnicodeString());
        }).ToArray();
        var bookmarks = new List<PdfBookmarkInfo>();
        var outline = document.GetOutlines(false);
        if (outline is not null)
        {
            var stack = new Stack<(PdfOutline Outline, int Depth)>();
            foreach (var child in outline.GetAllChildren().Reverse()) stack.Push((child, 0));
            while (stack.TryPop(out var item))
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (bookmarks.Count >= 100_000 || item.Depth > 512) throw new InvalidDataException("Bookmark inspection limit exceeded.");
                bookmarks.Add(new PdfBookmarkInfo(item.Outline.GetTitle(), item.Depth));
                foreach (var child in item.Outline.GetAllChildren().Reverse()) stack.Push((child, item.Depth + 1));
            }
        }
        return new PdfDocumentInspection(annotations.AsReadOnly(), Array.AsReadOnly(fields), Array.AsReadOnly(attachments),
            bookmarks.AsReadOnly(), document.GetReader().IsEncrypted(), new SignatureUtil(document).GetSignatureNames().Count != 0, form?.HasXfaForm() ?? false);
    }, cancellationToken);

    public Task<PdfBinaryAsset> ReadAttachmentAsync(PdfSnapshot source, string name, CancellationToken cancellationToken = default) => Task.Run(() =>
    {
        using var input = source.OpenRead();
        using var document = new PdfDocument(CreateReader(input, source.GetPassword()));
        if (!document.GetReader().IsOpenedWithFullPermission()) throw new UnauthorizedAccessException("Attachment extraction requires owner-authorized access.");
        var entry = document.GetCatalog().GetNameTree(PdfName.EmbeddedFiles).GetEntry(name) ?? throw new KeyNotFoundException(name);
        if (entry is PdfIndirectReference reference) entry = reference.GetRefersTo();
        var spec = entry as PdfDictionary ?? throw new InvalidDataException("Invalid attachment specification.");
        var embedded = spec.GetAsDictionary(PdfName.EF) ?? throw new InvalidDataException("The attachment is external, not embedded.");
        var stream = embedded.GetAsStream(PdfName.UF) ?? embedded.GetAsStream(PdfName.F) ?? throw new InvalidDataException("Missing attachment stream.");
        cancellationToken.ThrowIfCancellationRequested();
        var bytes = stream.GetBytes();
        cancellationToken.ThrowIfCancellationRequested();
        return new PdfBinaryAsset(bytes);
    }, cancellationToken);
}
