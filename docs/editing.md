# Native editing backend

`ProPDF.Editing.iText` is an optional composition-root dependency. It does not leak into Core, rendering or UI package dependencies. Its iText and pdfSweep dependencies require AGPL compliance or appropriate commercial licenses.

```csharp
var backend = new PdfPigBackend();
var editor = new ITextPdfEditor(backend);
var session = new PdfSession(backend, editor);
await using var source = File.OpenRead("input.pdf");
await session.OpenAsync(source);
await session.ApplyAsync(new AddText(1, new PdfPoint(40, 60), "Reviewed"), session.Current!.Id);
await session.SaveAsAsync("reviewed.pdf");
```

All page indexes are one-based. All input geometry uses cropped, rotated, top-left-origin **PDF points**, not screen pixels. A move destination is the final page position. Every transaction operates on a private PDF, closes it, reopens it through the supplied validator, and only then publishes a new snapshot. Failed or cancelled edits never modify the original snapshot. Output bytes and page counts are bounded.

## Implemented operations

Page rotation, insertion, deletion, reordering, crop, cross-document page insertion and extraction; native text, image, rectangle and ellipse content insertion; text-region replacement; native note/free-text/highlight/underline/strikeout/rectangle/ellipse/link/ink annotations; annotation deletion and comment updates; AcroForm text/checkbox/choice authoring, existing field filling, deletion and flattening; title/author/subject/keywords; embedded attachment addition/removal/extraction; root bookmarks; AES-256 encryption and owner-authorized decryption.

Standard-font text is checked for missing glyphs. Supply `PdfBinaryAsset` font bytes for an embeddable Unicode font. Insertion does not implement complex-script shaping, automatic paragraph reflow or font-license validation. Region replacement removes **all** content in the selected rectangle before inserting text; it is not a general existing-paragraph editor. Page extraction deliberately produces a new unsigned, unencrypted PDF, so the caller must decide whether to encrypt the result.

## Redaction boundary

`RedactRegion` invokes pdfSweep to remove intersecting page content and removes intersecting non-widget annotations. It is not a black-box overlay. The rewrite excludes unused objects and is checked by reopening the PDF. Tests verify removed text through PdfPig, inspect all decoded output streams for the fixture's secret, and inspect rendered pixels.

Redaction is local to a page region. It does not automatically remove identical text in metadata, attachments, other pages, optional-content groups or document history stored outside the output PDF. Tagged PDF redaction is disabled until semantic/ActualText handling is qualified. Intersecting form widgets must be flattened first. Full-file sanitization, hostile-document security qualification, and every image/mask/transparency/codec combination are **not** certified. Do not treat these alpha tests as a guarantee for sensitive production documents.

## Signatures and permissions

The editor never uses iText's unethical-reading switch. Editing, page import and attachment extraction require owner-authorized access. Signed documents are rejected by the rewrite editor rather than silently invalidating their signatures.

`PdfSignatureService` supports detached CAdES signing with host-supplied `IExternalSignature` and a leaf-first certificate chain. `DotNetRsaSignature` uses a host-owned RSA key without exporting it. Signing appends a revision; certified DocMDP documents are currently rejected. Keys, certificates and provider lifetimes remain the application's responsibility. No private key is persisted by ProPDF.

`VerifyIntegrityAsync` reports cryptographic integrity, current-document coverage and revision indexes. A valid cryptographic signature is **not** a trusted identity verdict: certificate trust chains, revocation, timestamps, PAdES profiles and LTV evidence require additional policy/validation and are not claimed by this API.

## Not implemented here

XFA, rich AcroForm JavaScript/calculation, comprehensive field appearance fidelity, free-text appearance editing in place, hierarchical bookmark editing, safe tagged-PDF redaction, arbitrary paragraph/image object editing, Office conversion, OCR, PDF/A/PDF/UA certification, prepress/overprint/separation workflows, 3D/multimedia, portfolios and Adobe cloud collaboration are not provided by this alpha adapter.

Official API references: [iText .NET 9.7](https://api.itextpdf.com/iText/dotnet/9.7.0/), [pdfSweep](https://api.itextpdf.com/pdfSweep/dotnet/latest/), [licensing](https://itextpdf.com/how-buy/AGPLv3-license).
