using System.Collections.Frozen;
using System.Text;
using iText.Forms;
using iText.Kernel.Geom;
using iText.Kernel.Pdf;
using iText.Kernel.Pdf.Filespec;
using iText.Kernel.Pdf.Navigation;
using iText.Signatures;
using ProPDF.Core;

namespace ProPDF.Editing.iText;

public sealed record ITextEditorOptions(long MaximumOutputBytes = 256L * 1024 * 1024, int MaximumPages = 100_000);

/// <summary>Native PDF editing backend. Transactions are rewritten, reopened and validated before publication.</summary>
public sealed partial class ITextPdfEditor : IPdfEditor, IPdfDocumentInspector
{
    private readonly IPdfDocumentLoader _validator;
    private readonly ITextEditorOptions _options;
    public ITextPdfEditor(IPdfDocumentLoader validator, ITextEditorOptions? options = null)
    {
        _validator = validator ?? throw new ArgumentNullException(nameof(validator));
        _options = options ?? new ITextEditorOptions();
        if (_options.MaximumOutputBytes is <= 0 or > int.MaxValue || _options.MaximumPages < 1)
            throw new ArgumentOutOfRangeException(nameof(options));
    }

    public IReadOnlySet<PdfCapability> Capabilities { get; } = new[]
    {
        PdfCapability.PageOrganization, PdfCapability.ContentInsertion, PdfCapability.ContentReplacement,
        PdfCapability.Annotations, PdfCapability.Forms, PdfCapability.Redaction, PdfCapability.Metadata,
        PdfCapability.Attachments, PdfCapability.Encryption, PdfCapability.Bookmarks
    }.ToFrozenSet();

    public async Task<PdfSnapshot> ApplyAsync(PdfSnapshot source, IReadOnlyList<IPdfEditOperation> operations, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(operations);
        var batch = operations.ToArray();
        if (batch.Length == 0) return source;
        if (batch.Length > 100_000) throw new ArgumentOutOfRangeException(nameof(operations));
        if (batch.Any(operation => operation is null || !Capabilities.Contains(operation.Capability)))
            throw new NotSupportedException("The transaction contains an unsupported operation.");
        var encryptionChanges = batch.OfType<ChangeEncryption>().ToArray();
        if (encryptionChanges.Length > 1) throw new ArgumentException("A transaction can change encryption only once.");
        var change = encryptionChanges.SingleOrDefault();
        var password = change is null ? source.GetPassword() : change.Settings?.GetOwnerPassword();
        var bytes = await Task.Run(() => Rewrite(source, batch, change, cancellationToken), cancellationToken).ConfigureAwait(false);
        return await ValidateAsync(bytes, password, cancellationToken).ConfigureAwait(false);
    }

    private byte[] Rewrite(PdfSnapshot source, IPdfEditOperation[] operations, ChangeEncryption? encryption, CancellationToken cancellationToken)
    {
        using var input = source.OpenRead();
        using var output = new BoundedPdfOutput(_options.MaximumOutputBytes, cancellationToken);
        using var reader = CreateReader(input, source.GetPassword());
        var writerProperties = new WriterProperties().SetCompressionLevel(9);
        if (encryption?.Settings is { } settings)
        {
            var permissions = (settings.AllowPrinting ? EncryptionConstants.ALLOW_PRINTING : 0) |
                              (settings.AllowCopy ? EncryptionConstants.ALLOW_COPY : 0);
            writerProperties.SetStandardEncryption(Encoding.UTF8.GetBytes(settings.GetUserPassword()),
                Encoding.UTF8.GetBytes(settings.GetOwnerPassword()), permissions, EncryptionConstants.ENCRYPTION_AES_256);
        }
        using var writer = new PdfWriter(output, writerProperties);
        writer.SetCloseStream(false);
        var stamping = new StampingProperties();
        if (encryption is null) stamping.PreserveEncryption();
        using (var document = new PdfDocument(reader, writer, stamping))
        {
            if (!reader.IsOpenedWithFullPermission()) throw new UnauthorizedAccessException("Editing requires the PDF owner password.");
            if (new SignatureUtil(document).GetSignatureNames().Count != 0)
                throw new NotSupportedException("This document is signed. Rewriting it would invalidate signatures. Use the separate append-mode signing service or edit an unsigned original.");
            document.SetFlushUnusedObjects(false);
            foreach (var operation in operations)
            {
                cancellationToken.ThrowIfCancellationRequested();
                Apply(document, operation, cancellationToken);
                if (document.GetNumberOfPages() is < 1 || document.GetNumberOfPages() > _options.MaximumPages)
                    throw new InvalidOperationException("An edit would exceed the page limit or remove every page.");
            }
        }
        cancellationToken.ThrowIfCancellationRequested();
        return output.ToArray();
    }

    private void Apply(PdfDocument document, IPdfEditOperation operation, CancellationToken cancellationToken)
    {
        switch (operation)
        {
            case PdfFormDataEdit formData: ApplyFormDataEdit(document, formData, cancellationToken); break;
            case PdfOutlineEdit outline: ApplyOutlineEdit(document, outline, cancellationToken); break;
            case AddInternalLink link: InsertInternalLink(document, link); break;
            case RotatePage rotate:
                if (rotate.ClockwiseDegrees % 90 != 0) throw new ArgumentException("Rotation must be a multiple of 90 degrees.");
                var page = GetPage(document, rotate.PageNumber);
                page.SetRotation(((page.GetRotation() + rotate.ClockwiseDegrees) % 360 + 360) % 360);
                break;
            case DeletePage delete:
                GetPage(document, delete.PageNumber);
                if (document.GetNumberOfPages() <= 1) throw new InvalidOperationException("A PDF must retain at least one page.");
                document.RemovePage(delete.PageNumber);
                break;
            case MovePage move:
                GetPage(document, move.PageNumber);
                GetPage(document, move.Destination);
                if (move.PageNumber != move.Destination)
                    document.MovePage(move.PageNumber, move.Destination > move.PageNumber ? move.Destination + 1 : move.Destination);
                break;
            case InsertBlankPage insert:
                ValidateInsertion(document, insert.BeforePage);
                if (insert.Size.Width <= 0 || insert.Size.Height <= 0) throw new ArgumentOutOfRangeException(nameof(insert.Size));
                document.AddNewPage(insert.BeforePage, new PageSize((float)insert.Size.Width, (float)insert.Size.Height));
                break;
            case CropPage crop:
                var cropPage = GetPage(document, crop.PageNumber);
                ValidateBounds(cropPage, crop.ViewBounds);
                cropPage.SetCropBox(ToRectangle(Transform(cropPage).ToPdf(crop.ViewBounds)));
                break;
            case InsertDocumentPages insert:
                ValidateInsertion(document, insert.BeforePage);
                if (insert.Pages.IsEmpty || insert.Pages.Length + document.GetNumberOfPages() > _options.MaximumPages)
                    throw new ArgumentException("Invalid inserted page selection.");
                foreach (var number in insert.Pages) insert.Document.GetPage(number);
                using (var stream = insert.Document.OpenRead())
                using (var other = new PdfDocument(CreateReader(stream, insert.Document.GetPassword())))
                {
                    if (!other.GetReader().IsOpenedWithFullPermission()) throw new UnauthorizedAccessException("Importing pages requires owner-authorized access.");
                    other.CopyPagesTo(insert.Pages.ToList(), document, insert.BeforePage, new PdfPageFormCopier());
                }
                break;
            case AddText text: InsertText(document, text); break;
            case AddImage image: InsertImage(document, image); break;
            case AddShape shape: InsertShape(document, shape); break;
            case AddAnnotation annotation: InsertAnnotation(document, annotation); break;
            case AddInkAnnotation ink: InsertInk(document, ink); break;
            case DeleteAnnotation delete:
                var annotationPage = GetPage(document, delete.PageNumber);
                annotationPage.RemoveAnnotation(FindAnnotation(annotationPage, delete.Id));
                break;
            case UpdateAnnotationComment update:
                var existing = FindAnnotation(GetPage(document, update.PageNumber), update.Id);
                if (existing.GetSubtype().Equals(PdfName.FreeText))
                    throw new NotSupportedException("Replace a free-text annotation to change its visible text and appearance together.");
                existing.SetContents(new PdfString(update.Contents));
                existing.GetPdfObject().Put(PdfName.T, new PdfString(update.Author));
                break;
            case AddFormField field: InsertField(document, field); break;
            case SetFormValue value: SetFieldValue(document, value); break;
            case RemoveFormField field:
                if (!GetForm(document).RemoveField(field.Name)) throw new KeyNotFoundException(field.Name);
                break;
            case FlattenForms flatten:
                var form = GetForm(document);
                if (flatten.Name is not null)
                {
                    if (form.GetField(flatten.Name) is null) throw new KeyNotFoundException(flatten.Name);
                    form.PartialFormFlattening(flatten.Name);
                }
                form.FlattenFields();
                break;
            case RedactRegion redaction: ApplyRedaction(document, redaction, cancellationToken); break;
            case ReplaceRegionText replace:
                ApplyRedaction(document, new RedactRegion(replace.PageNumber, replace.Bounds, PdfColor.White), cancellationToken);
                InsertText(document, new AddText(replace.PageNumber, new PdfPoint(replace.Bounds.X + 2, replace.Bounds.Y + replace.FontSize + 2),
                    replace.Text, replace.FontSize, EmbeddedFont: replace.EmbeddedFont));
                break;
            case SetDocumentMetadata metadata:
                document.GetDocumentInfo().SetTitle(metadata.Metadata.Title).SetAuthor(metadata.Metadata.Author)
                    .SetSubject(metadata.Metadata.Subject).SetKeywords(metadata.Metadata.Keywords);
                break;
            case AddAttachment attachment:
                ValidateAttachmentName(attachment.Name);
                var tree = document.GetCatalog().GetNameTree(PdfName.EmbeddedFiles);
                if (tree.GetEntry(attachment.Name) is not null) throw new ArgumentException("An attachment with that name already exists.");
                var specification = PdfFileSpec.CreateEmbeddedFileSpec(document, attachment.Data.ToArray(), attachment.Name,
                    attachment.Name, new PdfName(attachment.MediaType), null, PdfName.Unspecified);
                document.AddFileAttachment(attachment.Name, specification);
                break;
            case RemoveAttachment attachment:
                var names = document.GetCatalog().GetNameTree(PdfName.EmbeddedFiles);
                if (names.GetEntry(attachment.Name) is null) throw new KeyNotFoundException(attachment.Name);
                names.RemoveEntry(new PdfString(attachment.Name));
                break;
            case AddBookmark bookmark:
                ArgumentException.ThrowIfNullOrWhiteSpace(bookmark.Title);
                document.GetOutlines(false).AddOutline(bookmark.Title).AddDestination(PdfExplicitDestination.CreateFit(GetPage(document, bookmark.PageNumber)));
                break;
            case RemoveBookmark bookmark:
                var children = document.GetOutlines(false).GetAllChildren();
                if (bookmark.RootIndex < 0 || bookmark.RootIndex >= children.Count) throw new ArgumentOutOfRangeException(nameof(bookmark.RootIndex));
                children[bookmark.RootIndex].RemoveOutline();
                break;
            case ChangeEncryption: break;
            default: throw new NotSupportedException($"Unsupported operation: {operation.GetType().Name}");
        }
    }

    public async Task<PdfSnapshot> CreateAsync(int pageCount = 1, PdfSize? size = null, CancellationToken cancellationToken = default)
    {
        if (pageCount < 1 || pageCount > _options.MaximumPages) throw new ArgumentOutOfRangeException(nameof(pageCount));
        var dimensions = size ?? new PdfSize(595.276, 841.89);
        if (dimensions.Width <= 0 || dimensions.Height <= 0) throw new ArgumentOutOfRangeException(nameof(size));
        var bytes = await Task.Run(() =>
        {
            using var output = new BoundedPdfOutput(_options.MaximumOutputBytes, cancellationToken);
            using (var writer = new PdfWriter(output))
            {
                writer.SetCloseStream(false);
                using var document = new PdfDocument(writer);
                document.GetDocumentInfo().SetTitle("Untitled");
                for (var i = 0; i < pageCount; i++)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    document.AddNewPage(new PageSize((float)dimensions.Width, (float)dimensions.Height));
                }
            }
            return output.ToArray();
        }, cancellationToken).ConfigureAwait(false);
        return await ValidateAsync(bytes, null, cancellationToken).ConfigureAwait(false);
    }

    public async Task<PdfSnapshot> ExtractPagesAsync(PdfSnapshot source, IEnumerable<int> pages, CancellationToken cancellationToken = default)
    {
        var selected = pages.Take(_options.MaximumPages + 1).ToArray();
        if (selected.Length < 1 || selected.Length > _options.MaximumPages) throw new ArgumentOutOfRangeException(nameof(pages));
        foreach (var page in selected) source.GetPage(page);
        var bytes = await Task.Run(() =>
        {
            using var input = source.OpenRead();
            using var document = new PdfDocument(CreateReader(input, source.GetPassword()));
            if (!document.GetReader().IsOpenedWithFullPermission()) throw new UnauthorizedAccessException("Extraction requires owner-authorized access.");
            using var output = new BoundedPdfOutput(_options.MaximumOutputBytes, cancellationToken);
            using (var writer = new PdfWriter(output))
            {
                writer.SetCloseStream(false);
                using var result = new PdfDocument(writer);
                document.CopyPagesTo(selected.ToList(), result, new PdfPageFormCopier());
            }
            cancellationToken.ThrowIfCancellationRequested();
            return output.ToArray();
        }, cancellationToken).ConfigureAwait(false);
        return await ValidateAsync(bytes, null, cancellationToken).ConfigureAwait(false);
    }

    internal async Task<PdfSnapshot> ValidateAsync(byte[] bytes, string? password, CancellationToken cancellationToken)
    {
        using var input = new MemoryStream(bytes, writable: false);
        return await _validator.OpenAsync(input, new PdfOpenOptions
        {
            Password = password, MaximumBytes = _options.MaximumOutputBytes, MaximumPages = _options.MaximumPages
        }, cancellationToken).ConfigureAwait(false);
    }
    internal static PdfReader CreateReader(Stream source, string? password)
    {
        var properties = new ReaderProperties();
        if (password is not null) properties.SetPassword(Encoding.UTF8.GetBytes(password));
        return new PdfReader(source, properties);
    }
    internal static PdfPage GetPage(PdfDocument document, int number) => number >= 1 && number <= document.GetNumberOfPages()
        ? document.GetPage(number) : throw new ArgumentOutOfRangeException(nameof(number));
    private static void ValidateInsertion(PdfDocument document, int number)
    {
        if (number < 1 || number > document.GetNumberOfPages() + 1) throw new ArgumentOutOfRangeException(nameof(number));
    }
    internal static PdfPageTransform Transform(PdfPage page)
    {
        var crop = page.GetCropBox();
        var userUnit = page.GetPdfObject().GetAsNumber(PdfName.UserUnit)?.DoubleValue() ?? 1;
        return new PdfPageTransform(new PdfRect(crop.GetX(), crop.GetY(), crop.GetWidth(), crop.GetHeight()), page.GetRotation(), userUnit);
    }
    internal static Rectangle ToRectangle(PdfRect bounds) => new((float)bounds.X, (float)bounds.Y, (float)bounds.Width, (float)bounds.Height);
    private static void ValidateBounds(PdfPage page, PdfRect bounds)
    {
        var size = Transform(page).ViewSize;
        if (bounds.IsEmpty || bounds.X < 0 || bounds.Y < 0 || bounds.Right > size.Width + 0.01 || bounds.Bottom > size.Height + 0.01)
            throw new ArgumentOutOfRangeException(nameof(bounds), "The region must be inside the visible page.");
    }
    private static void ValidateAttachmentName(string name)
    {
        if (string.IsNullOrWhiteSpace(name) || name.Length > 255 || name.IndexOfAny(['/', '\\', '\0']) >= 0 || name is "." or "..")
            throw new ArgumentException("Use a simple attachment name, not a file path.", nameof(name));
    }
}

internal sealed class BoundedPdfOutput(long maximumBytes, CancellationToken cancellationToken) : MemoryStream
{
    private void Check(long end)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (end < 0 || end > maximumBytes) throw new InvalidDataException("PDF output exceeds the configured size limit.");
    }
    public override void Write(byte[] buffer, int offset, int count) { Check(Position + count); base.Write(buffer, offset, count); }
    public override void Write(ReadOnlySpan<byte> buffer) { Check(Position + buffer.Length); base.Write(buffer); }
    public override void WriteByte(byte value) { Check(Position + 1); base.WriteByte(value); }
    public override void SetLength(long value) { Check(value); base.SetLength(value); }
}
