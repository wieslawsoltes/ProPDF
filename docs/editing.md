# Native editing backend

`ProPDF.Editing.iText` is an optional composition-root dependency. Core, rendering and UI packages do not depend on it. Its iText and pdfSweep dependencies require AGPL compliance or appropriate commercial licenses.

```csharp
var backend = new PdfPigBackend();
var editor = new ITextPdfEditor(backend);
var session = new PdfSession(backend, editor);
await using var source = File.OpenRead("input.pdf");
await session.OpenAsync(source);
await session.ApplyAsync(new AddText(1, new PdfPoint(40, 60), "Reviewed"), session.Current!.Id);
await session.SaveAsAsync("reviewed.pdf");
```

All page indexes are one-based. Geometry uses cropped, rotated, top-left-origin PDF points. A move destination is the final page position. Each transaction modifies a private PDF, closes it, reopens it with the supplied validator, and only then publishes an immutable revision. Failed or cancelled edits preserve the original snapshot. Output bytes and page counts are bounded.

## Implemented operations

Page rotation, insertion, deletion, reordering, crop, duplication, cross-document insertion and extraction; native text/image/vector insertion; region text replacement; native note/free-text/highlight/underline/strikeout/shape/link/ink annotations; deletion and comment updates; AcroForm text/checkbox/choice authoring, existing-value filling, removal and flattening; metadata; embedded attachments; hierarchical outline editing; internal page links; and AES-256 encryption/decryption.

[Navigation and bookmarks](navigation.md) documents outline paths, subtree moves, destination resolution and safe link activation. [Output and comparison](output-comparison.md) covers selected-page extraction and bounded raster/text exports using the shared renderer.

Standard-font insertion checks missing glyphs. Supply `PdfBinaryAsset` font bytes for an embeddable Unicode font. Insertion does not implement complete complex-script shaping, automatic paragraph reflow or font-license validation. Region replacement removes **all content in the selected rectangle** before inserting text; it is not arbitrary text-run/paragraph editing. Page extraction deliberately creates an unsigned, unencrypted document.

## Redaction boundary

`RedactRegion` invokes pdfSweep to remove intersecting page content and removes intersecting non-widget annotations. It is not a painted rectangle. The rewrite excludes unused objects and is independently reopened. Generated tests check extracted text through PdfPig, decoded output streams for fixture secrets, and rendered pixels.

Redaction applies to one page region. It does not remove matching content in metadata, attachments, other pages or source files/backups. Tagged PDF redaction is disabled until semantic/ActualText handling is qualified. Intersecting widgets must be flattened first. Full-file sanitization, hostile-input security qualification, every mask/transparency/codec combination and secure erasure of undo history are **not certified**.

## Signatures and permissions

The editor never enables iText permission bypass. Editing, page import/extraction and attachment extraction require owner-authorized access. Ordinary rewrites of signed documents are rejected rather than silently invalidating signatures.

`PdfSignatureService` signs detached CAdES using a host-supplied `IExternalSignature` and leaf-first certificate chain. `DotNetRsaSignature` invokes a host-owned RSA key without exporting it. Signing appends a revision. Certified DocMDP documents are blocked pending policy qualification. Key/certificate selection, provider access and lifetime belong to the host; ProPDF does not persist private keys.

`VerifyIntegrityAsync` reports cryptographic integrity, current-document coverage and revision indexes. It does not establish certificate trust, revocation, timestamp validity, PAdES-LTV conformance or a legal identity verdict.

## Remaining boundaries

XFA; arbitrary AcroForm JavaScript/calculation; comprehensive appearance fidelity; in-place free-text appearance editing; safe tagged-PDF redaction; arbitrary existing-object/paragraph editing; OCR; Office conversion; PDF/A/PDF/UA; production printing/prepress; multimedia/3D; portfolios; trust/revocation/LTV policy; and enterprise collaboration are not implemented by this alpha adapter.

Official references: [iText .NET](https://api.itextpdf.com/iText/dotnet/9.7.0/), [pdfSweep](https://api.itextpdf.com/pdfSweep/dotnet/latest/), [licensing](https://itextpdf.com/how-buy/AGPLv3-license).
