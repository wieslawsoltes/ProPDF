# Output and comparison

The shared workspace exposes an output toolbar in both Avalonia and WPF. Its commands also work in custom hosts through `PdfWorkspace`, while the underlying services are independently usable without UI.

## Page selections

Enter `all`, `odd`, `even`, individual pages, or comma-separated ranges such as `1-3,7,last`. Descending ranges (`5-2`) preserve requested order. Duplicate pages are removed in first-occurrence order. All indexes are one-based. Invalid/empty selections and configured size limits fail explicitly.

`PdfPageSelection.Parse` is a UI-independent Core API. The toolbar range applies to text export and selected-page PDF extraction; image export uses the current page.

## Raster and text exports

PNG and JPEG toolbar actions export the current page at the requested DPI. The SDK also supports WebP and cropped regions. `PdfRasterExporter` assembles a bounded image from shared renderer tiles, keeping the rendering/cache implementation common to viewer and exporter. It rejects oversized pixel allocations before rendering. Encoding has an output byte limit; it is not a streaming encoder, so allocation during encoding is not a hard process-memory limit.

`PdfTextExporter` streams UTF-8 text in extraction order with form-feed separators between pages and an explicit byte limit. It is not OCR or visual-layout reconstruction. Writing to a caller stream leaves that stream open. File-based exports use same-directory temporary files, flush and atomic replacement; cancelled/failed exports do not overwrite the destination.

Image, text and extracted-page outputs are unencrypted. Extracted PDFs are unsigned. The workspace blocks export while unapplied redaction marks exist. This does not securely erase original files, earlier snapshots or backups.

## Compare PDFs

Compare PDF loads another document and compares corresponding page numbers. Results identify unchanged, changed, resized, added and removed pages; include changed-pixel counts, fractions and bounding rectangles; and optionally compare extracted text. Select a result to navigate the current document, or save the structured result as JSON. Editing the current document marks the comparison stale.

SDK options control DPI, channel tolerance, page count and per-page pixel budget. Pixel comparison is bounded and cancellable by row; pages are processed sequentially. Files are not uploaded to a server.

Comparison is **not semantic equivalence**, a signature/trust check, metadata comparison, automatic alignment after page insertion, or an Acrobat-certified document-diff implementation. Raster differences depend on DPI, tolerance and the upstream renderer. Identical pictures and extracted text can still conceal different metadata, actions, structure or attachments. Review those separately.
