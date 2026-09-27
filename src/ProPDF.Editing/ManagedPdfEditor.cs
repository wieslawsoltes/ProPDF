using System.Collections.Frozen;
using System.Text;
using ProPDF.Core;
using ProPDF.Kernel;
using static ProPDF.Kernel.PdfValues;

namespace ProPDF.Editing;

public sealed record ManagedPdfEditorOptions(long MaximumOutputBytes = 256L * 1024 * 1024, int MaximumPages = 100_000, PdfReadLimits? ReadLimits = null);

/// <summary>ProPDF-owned editing backend. All transactions write a separate PDF and validate it before publication.</summary>
public sealed partial class ManagedPdfEditor : IPdfEditor, IPdfDocumentInspector, IPdfPageExtractor, IPdfAttachmentReader, IPdfNavigationService
{
    private readonly IPdfDocumentLoader _validator;
    private readonly ManagedPdfEditorOptions _options;
    public ManagedPdfEditor(IPdfDocumentLoader? validator = null, ManagedPdfEditorOptions? options = null)
    {
        _validator = validator ?? new ManagedPdfLoader(); _options = options ?? new ManagedPdfEditorOptions();
        if (_options.MaximumOutputBytes is < 1 or > int.MaxValue || _options.MaximumPages < 1) throw new ArgumentOutOfRangeException(nameof(options));
    }
    public IReadOnlySet<PdfCapability> Capabilities { get; } = new[]
    {
        PdfCapability.PageOrganization, PdfCapability.ContentInsertion, PdfCapability.ContentReplacement,
        PdfCapability.Annotations, PdfCapability.Forms, PdfCapability.Redaction, PdfCapability.Metadata,
        PdfCapability.Attachments, PdfCapability.Encryption, PdfCapability.Bookmarks
    }.ToFrozenSet();
    internal PdfGraph Open(PdfSnapshot source, CancellationToken token)
    {
        using var input = source.OpenRead(); using var bytes = new MemoryStream(); input.CopyTo(bytes);
        return new PdfGraph(PdfFile.Open(bytes.ToArray(), source.GetPassword(), _options.ReadLimits, token), _options.MaximumPages);
    }
    public async Task<PdfSnapshot> ApplyAsync(PdfSnapshot source, IReadOnlyList<IPdfEditOperation> operations, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(source); ArgumentNullException.ThrowIfNull(operations);
        var batch = operations.ToArray(); if (batch.Length == 0) return source;
        if (batch.Any(operation => operation is null || !Capabilities.Contains(operation.Capability))) throw new NotSupportedException("Unsupported edit capability.");
        var changes = batch.OfType<ChangeEncryption>().ToArray(); if (changes.Length > 1) throw new ArgumentException("Only one encryption change is allowed per transaction.");
        var password = changes.Length == 0 ? source.GetPassword() : changes[0].Settings?.GetOwnerPassword();
        var bytes = await Task.Run(() =>
        {
            var graph = Open(source, cancellationToken);
            if (!graph.File.IsOwnerAuthorized) throw new UnauthorizedAccessException("Editing requires the PDF owner password.");
            if (HasSignatures(graph)) throw new NotSupportedException("Rewriting this signed document would invalidate signatures. Use append-mode signing or an unsigned original.");
            foreach (var operation in batch)
            {
                cancellationToken.ThrowIfCancellationRequested(); Apply(graph, operation, cancellationToken);
                if (graph.Pages.Count is < 1 || graph.Pages.Count > _options.MaximumPages) throw new InvalidOperationException("A PDF must have at least one page and remain within the page limit.");
            }
            return graph.File.Save((int)_options.MaximumOutputBytes);
        }, cancellationToken).ConfigureAwait(false);
        return await ValidateAsync(bytes, password, cancellationToken).ConfigureAwait(false);
    }
    public async Task<PdfSnapshot> CreateAsync(int pageCount = 1, PdfSize? size = null, CancellationToken cancellationToken = default)
    {
        if (pageCount < 1 || pageCount > _options.MaximumPages) throw new ArgumentOutOfRangeException(nameof(pageCount));
        var bytes = await Task.Run(() =>
        {
            var graph = new PdfGraph(PdfFile.Create(_options.ReadLimits, cancellationToken), _options.MaximumPages);
            for (var i = 0; i < pageCount; i++) { cancellationToken.ThrowIfCancellationRequested(); graph.InsertBlank(i + 1, size ?? new PdfSize(595.276, 841.89)); }
            SetMetadata(graph, new PdfMetadata("Untitled")); return graph.File.Save((int)_options.MaximumOutputBytes);
        }, cancellationToken).ConfigureAwait(false);
        return await ValidateAsync(bytes, null, cancellationToken).ConfigureAwait(false);
    }
    public async Task<PdfSnapshot> ExtractPagesAsync(PdfSnapshot source, IEnumerable<int> pages, CancellationToken cancellationToken = default)
    {
        var selected = pages.ToArray(); if (selected.Length < 1 || selected.Length > _options.MaximumPages) throw new ArgumentOutOfRangeException(nameof(pages));
        foreach (var page in selected) source.GetPage(page);
        var bytes = await Task.Run(() =>
        {
            var original = Open(source, cancellationToken); var target = new PdfGraph(PdfFile.Create(_options.ReadLimits, cancellationToken), _options.MaximumPages);
            target.InsertPages(original, selected, 1); return target.File.Save((int)_options.MaximumOutputBytes);
        }, cancellationToken).ConfigureAwait(false);
        return await ValidateAsync(bytes, null, cancellationToken).ConfigureAwait(false);
    }
    private async Task<PdfSnapshot> ValidateAsync(byte[] bytes, string? password, CancellationToken token)
    {
        using var input = new MemoryStream(bytes, false);
        return await _validator.OpenAsync(input, new PdfOpenOptions { Password = password, MaximumBytes = _options.MaximumOutputBytes, MaximumPages = _options.MaximumPages }, token).ConfigureAwait(false);
    }
    private void Apply(PdfGraph graph, IPdfEditOperation operation, CancellationToken token)
    {
        switch (operation)
        {
            case RotatePage rotate:
                if (rotate.ClockwiseDegrees % 90 != 0) throw new ArgumentException("Rotation must be a multiple of 90 degrees.");
                var rotated = graph.Page(rotate.PageNumber); rotated.Dictionary["Rotate"] = new PdfNumber((rotated.Transform.Rotation + rotate.ClockwiseDegrees % 360 + 360) % 360); break;
            case DeletePage delete:
                var deleted = graph.Page(delete.PageNumber); if (graph.Pages.Count == 1) throw new InvalidOperationException("A PDF must retain a page.");
                foreach (var (_, annotation) in graph.Annotations(deleted).ToArray()) if (annotation.Name("Subtype") == "Widget") RemoveWidget(graph, deleted, annotation);
                graph.Pages.Remove(deleted); graph.RebuildPages(); break;
            case MovePage move:
                var moved = graph.Page(move.PageNumber); graph.Page(move.Destination);
                graph.Pages.RemoveAt(move.PageNumber - 1); graph.Pages.Insert(move.Destination - 1, moved); graph.RebuildPages(); break;
            case InsertBlankPage insert: graph.InsertBlank(insert.BeforePage, insert.Size); break;
            case CropPage crop:
                var cropped = graph.Page(crop.PageNumber); cropped.Validate(crop.ViewBounds); var box = cropped.Transform.ToPdf(crop.ViewBounds);
                cropped.Dictionary["CropBox"] = Numbers(box.X, box.Y, box.Right, box.Bottom); break;
            case InsertDocumentPages insert: graph.InsertPages(Open(insert.Document, token), insert.Pages, insert.BeforePage); break;
            case AddText text: InsertText(graph, text); break;
            case AddImage image: InsertImage(graph, image); break;
            case AddShape shape: InsertShape(graph, shape); break;
            case AddAnnotation annotation: InsertAnnotation(graph, annotation); break;
            case AddInkAnnotation ink: InsertInk(graph, ink); break;
            case DeleteAnnotation delete:
                var annotationPage = graph.Page(delete.PageNumber); var match = FindAnnotation(graph, annotationPage, delete.Id);
                if (match.Dictionary.Name("Subtype") == "Widget") throw new NotSupportedException("Remove widgets through the forms API.");
                graph.OptionalArray(annotationPage.Dictionary["Annots"]).Items.Remove(match.Raw); break;
            case UpdateAnnotationComment update:
                var target = FindAnnotation(graph, graph.Page(update.PageNumber), update.Id).Dictionary;
                if (target.Name("Subtype") == "FreeText") throw new NotSupportedException("Replace a free-text annotation to update its visible appearance.");
                target["Contents"] = new PdfString(update.Contents); target["T"] = new PdfString(update.Author); break;
            case AddFormField field: InsertField(graph, field); break;
            case SetFormValue value: FillField(graph, value); break;
            case RemoveFormField field: DeleteField(graph, field.Name); break;
            case FlattenForms flatten: Flatten(graph, flatten.Name); break;
            case RedactRegion redact: Redact(graph, redact, token); break;
            case ReplaceRegionText replace:
                Redact(graph, new RedactRegion(replace.PageNumber, replace.Bounds, PdfColor.White), token);
                InsertText(graph, new AddText(replace.PageNumber, new PdfPoint(replace.Bounds.X + 2, replace.Bounds.Y + replace.FontSize + 2), replace.Text, replace.FontSize, EmbeddedFont: replace.EmbeddedFont)); break;
            case SetDocumentMetadata metadata: SetMetadata(graph, metadata.Metadata); break;
            case AddAttachment attachment: Attach(graph, attachment); break;
            case RemoveAttachment attachment:
                var attachments = Names(graph, "EmbeddedFiles"); if (!attachments.Remove(attachment.Name)) throw new KeyNotFoundException(attachment.Name);
                SetNames(graph, "EmbeddedFiles", attachments); break;
            case AddBookmark bookmark: EditOutline(graph, new InsertOutline(bookmark.Title, bookmark.PageNumber)); break;
            case RemoveBookmark bookmark: EditOutline(graph, new DeleteOutline(bookmark.RootIndex.ToString(System.Globalization.CultureInfo.InvariantCulture))); break;
            case PdfOutlineEdit outline: EditOutline(graph, outline); break;
            case AddInternalLink link: InsertInternalLink(graph, link); break;
            case ChangeEncryption encryption:
                graph.File.ChangeEncryption(encryption.Settings?.GetUserPassword(), encryption.Settings?.GetOwnerPassword(), encryption.Settings?.AllowPrinting ?? false, encryption.Settings?.AllowCopy ?? false); break;
            default: throw new NotSupportedException($"Operation {operation.GetType().Name} is not implemented.");
        }
    }
    private static void SetMetadata(PdfGraph graph, PdfMetadata metadata)
    {
        var info = graph.OptionalDictionary(graph.File.Trailer["Info"]).Copy();
        info["Title"] = new PdfString(metadata.Title); info["Author"] = new PdfString(metadata.Author); info["Subject"] = new PdfString(metadata.Subject); info["Keywords"] = new PdfString(metadata.Keywords);
        info["Producer"] = new PdfString("ProPDF owned PDF engine"); graph.File.Trailer["Info"] = graph.File.Add(info);
        // Do not leave inconsistent stale XMP after explicit metadata updates.
        graph.File.Catalog.Remove("Metadata");
    }
    internal static bool HasSignatures(PdfGraph graph) => graph.File.Catalog.Contains("Perms") ||
        graph.File.EnumerateObjects().Any(item => item.Value is PdfDictionary d && (d.Name("Type") == "Sig" || d.Contains("ByteRange")));
    private static void Attach(PdfGraph graph, AddAttachment attachment)
    {
        if (string.IsNullOrWhiteSpace(attachment.Name) || attachment.Name.Length > 255 || attachment.Name.IndexOfAny(['/', '\\', '\0']) >= 0 || attachment.Name is "." or "..")
            throw new ArgumentException("Use a simple attachment file name.");
        var names = Names(graph, "EmbeddedFiles"); if (names.ContainsKey(attachment.Name)) throw new ArgumentException("Attachment name already exists.");
        var stream = PdfStream.FromDecoded(attachment.Data.ToArray(), Dictionary(("Type", new PdfName("EmbeddedFile")), ("Subtype", new PdfName(attachment.MediaType)),
            ("Params", Dictionary(("Size", new PdfNumber(attachment.Data.Length))))));
        var reference = graph.File.Add(stream);
        names[attachment.Name] = graph.File.Add(Dictionary(("Type", new PdfName("Filespec")), ("F", new PdfString(attachment.Name)), ("UF", new PdfString(attachment.Name)),
            ("EF", Dictionary(("F", reference), ("UF", reference))), ("AFRelationship", new PdfName("Unspecified"))));
        SetNames(graph, "EmbeddedFiles", names);
    }
    public Task<PdfBinaryAsset> ReadAttachmentAsync(PdfSnapshot source, string name, CancellationToken cancellationToken = default) => Task.Run(() =>
    {
        var graph = Open(source, cancellationToken); if (!graph.File.IsOwnerAuthorized) throw new UnauthorizedAccessException("Owner authorization is required to extract attachments.");
        var names = Names(graph, "EmbeddedFiles"); if (!names.TryGetValue(name, out var item)) throw new KeyNotFoundException(name);
        var spec = graph.Dictionary(item); var ef = graph.Dictionary(spec["EF"]); var stream = graph.Resolve(ef.Contains("UF") ? ef["UF"] : ef["F"]) as PdfStream ?? throw new InvalidDataException("Attachment is not embedded.");
        return new PdfBinaryAsset(graph.File.Decode(stream));
    }, cancellationToken);
    internal static Dictionary<string, PdfObject> Names(PdfGraph graph, string category)
    {
        var result = new Dictionary<string, PdfObject>(StringComparer.Ordinal); var catalogNames = graph.OptionalDictionary(graph.File.Catalog["Names"]);
        if (graph.Resolve(catalogNames[category]) is not PdfDictionary root) return result;
        var seen = new HashSet<PdfDictionary>(ReferenceEqualityComparer.Instance);
        void Read(PdfDictionary node, int depth)
        {
            if (depth > graph.File.Limits.MaximumDepth || !seen.Add(node) || seen.Count > 100_000) throw new InvalidDataException("Cyclic or over-budget PDF name tree.");
            if (graph.Resolve(node["Names"]) is PdfArray array)
            {
                if (array.Count % 2 != 0) throw new InvalidDataException("Odd-length name tree.");
                for (var i = 0; i < array.Count; i += 2)
                {
                    if (graph.Resolve(array[i]) is not PdfString key || !result.TryAdd(key.Text, array[i + 1]) || result.Count > 100_000) throw new InvalidDataException("Invalid or duplicate name-tree key.");
                }
            }
            foreach (var child in graph.OptionalArray(node["Kids"])) Read(graph.Dictionary(child), depth + 1);
        }
        Read(root, 0); return result;
    }
    private static void SetNames(PdfGraph graph, string category, Dictionary<string, PdfObject> names)
    {
        var root = graph.EnsureDictionary(graph.File.Catalog, "Names");
        var array = new PdfArray(); foreach (var item in names.OrderBy(p => p.Key, StringComparer.Ordinal)) { array.Items.Add(new PdfString(item.Key)); array.Items.Add(item.Value); }
        if (names.Count == 0) root.Remove(category); else root[category] = graph.File.Add(Dictionary(("Names", array)));
    }
}

/// <summary>Snapshot loader backed solely by the owned PDF kernel; PdfPig remains an optional independent renderer/validator.</summary>
public sealed class ManagedPdfLoader : IPdfDocumentLoader
{
    public async Task<PdfSnapshot> OpenAsync(Stream source, PdfOpenOptions? options = null, CancellationToken cancellationToken = default)
    {
        options ??= new PdfOpenOptions(); options.Validate();
        if (options.UseLenientParsing) throw new NotSupportedException("The owned loader does not silently repair malformed PDFs.");
        var bytes = await PdfStreams.ReadBoundedAsync(source, options.MaximumBytes, cancellationToken).ConfigureAwait(false);
        return await Task.Run(() =>
        {
            var graph = new PdfGraph(PdfFile.Open(bytes, options.Password, new PdfReadLimits(MaximumFileBytes: (int)options.MaximumBytes), cancellationToken), options.MaximumPages);
            if (graph.Pages.Count == 0) throw new InvalidDataException("PDF has no pages.");
            var info = graph.OptionalDictionary(graph.File.Trailer["Info"]);
            return new PdfSnapshot(bytes, graph.Pages.Select((page, i) => new PdfPageInfo(i + 1, page.Transform.ViewSize)),
                new PdfMetadata(info.Text("Title"), info.Text("Author"), info.Text("Subject"), info.Text("Keywords")), options.Password);
        }, cancellationToken).ConfigureAwait(false);
    }
}
