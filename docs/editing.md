# Native editing

`ProPDF.Editing` contains the owned `ManagedPdfEditor`, `ManagedPdfLoader` and `PdfSignatureService`. The kernel and operations are MIT-licensed ProPDF code; no commercial PDF license or copyleft backend is required. Core and the UI packages still depend on contracts, not this concrete editor.

```csharp
var backend = new PdfPigBackend();
var editor = new ManagedPdfEditor(backend);
var session = new PdfSession(backend, editor);
await using var input = File.OpenRead("input.pdf");
await session.OpenAsync(input);
await session.ApplyAsync(new AddText(1, new PdfPoint(40, 60), "Reviewed"), session.Current!.Id);
await session.SaveAsAsync("reviewed.pdf");
```

An independent PdfPig reopen validator is optional; `new ManagedPdfEditor()` uses the owned loader. Every batch edits a private graph, serializes it and reopens it before publication. Cancellation, validation failure or a stale expected revision leaves the session unchanged. Page numbers are one-based and edit geometry uses cropped, rotated, top-left-origin PDF points. Move destinations refer to the final page position.

## Implemented operations

Page rotation, insertion, duplication, deletion, reordering, crop, import and extraction; native text, raster-image, rectangle and ellipse insertion; region replacement; note/free-text/highlight/underline/strikeout/shape/link/ink annotations with appearances and stable IDs; comment update/deletion; AcroForm text/checkbox/choice authoring, filling, field removal and flattening; metadata; attachments; hierarchical bookmark operations and navigation; Standard-security encryption/decryption.

Extraction creates a new unsigned, unencrypted document. Intersecting widgets must be flattened before redaction. XFA, full rich/JavaScript form calculations, certified-document edits and tagged-page imports are not supported. Metadata edits update the Info dictionary and remove stale XMP rather than leaving conflicting old metadata. Full synchronized XMP authoring remains work.

The owned TrueType implementation supports Unicode cmap 4/12, full embedding, widths and ToUnicode mapping, including non-BMP scalars and multiple characters sharing a glyph. Supply an appropriately licensed `PdfBinaryAsset` font. Embedding flags are checked. CFF/collections, complex-script shaping, vertical writing and general existing-paragraph reflow are not implemented. Standard Latin fonts reject unsupported characters rather than silently losing text.

## Native redaction

`RedactRegion` removes supported intersecting native content, removes intersecting non-widget annotations, prunes resources and writes the fill. It does not simply cover old text. The algorithm conservatively removes whole text objects, painted paths and image/form invocations. This can remove material outside the selected region; it is not exact partial-glyph or partial-image redaction. `ReplaceRegionText` applies that removal and inserts replacement text, not paragraph reflow.

The implementation fails explicitly for tagged/ActualText content, inline images, patterns, soft masks, unsupported encodings and other unqualified cases. Original documents, backups and undo snapshots retain original data. Metadata, attachments, alternate information and other pages are not automatically sanitized. This alpha is **not a certified sensitive-document sanitization tool**. See the [owned-engine boundaries](owned-engine.md).

## Encryption and signatures

Protected editing/import/extraction requires owner-authorized access; no permission bypass is provided. R2–R6 Standard-password reading and AES-256 R6 writing (explicit PDF 1.7 extension-level-8 declaration) are implemented. Passwords currently must be printable ASCII; international password preparation is an explicit gap. Ordinary signed-document rewrites are refused.

`PdfSignatureService.SignAsync` takes `IPdfDetachedSignatureProvider`, a leaf-first .NET `X509Certificate2` collection, and a field name. `DotNetRsaSignature` and `DotNetEcdsaSignature` use host-owned keys without exporting or owning them. PDF signature serialization and range handling are implemented in ProPDF; platform CMS supplies the cryptographic container. An additional signature appends a revision and retains the previous file prefix.

`VerifyIntegrityAsync` reports cryptographic integrity, current-document coverage and revision indexes. It is not certificate-chain trust, revocation, timestamp, DocMDP/FieldMDP or PAdES-LTV validation. Private keys and certificate-policy configuration belong to the host. Signing, passwords and permissions still need complete desktop workflows.

## Compatibility

The [migration guide](dependency-compatibility.md) describes the old package/API replacement. The [feature matrix](features.md) tracks remaining Acrobat-level work. Unsupported capabilities and unqualified semantics are not reported as complete merely because low-level PDF dictionaries can preserve them.
