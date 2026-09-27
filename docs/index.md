# ProPDF

## One PDF engine. Two native desktop experiences.

ProPDF shares document transactions, SkiaSharp rendering, editing tools and workspace state between Avalonia and WPF. Host a viewer, a lazy thumbnail or the complete native editor shell.

!!! warning "Development alpha"
    This is not complete Adobe Acrobat parity, a certified redaction tool or a complete signature-trust validator. Review the [feature matrix](features.md), [licensing](licensing.md) and [editing boundaries](editing.md).

## Reusable libraries

| Package | Responsibility |
| --- | --- |
| ProPDF.Core | Immutable snapshots, transactions, history, atomic saves, geometry and service contracts |
| ProPDF.Rendering.Skia | Background display-list and tile rendering, caches and native image leases |
| ProPDF.Engine.PdfPig | Loading, page geometry, text extraction/search and PDF-to-Skia interpretation |
| ProPDF.Editing.iText | Optional native content/pages, annotations, forms, redaction, encryption and signing |
| ProPDF.Presentation | Shared viewport, scenes, interactions, commands and inspector state |
| ProPDF.Avalonia | Native viewer, thumbnail and editor controls |
| ProPDF.Wpf | Equivalent Windows WPF controls |

Core has no UI or PDF-vendor dependency. Neither UI package depends on iText. Applications choose their backend at the composition root; the samples explicitly opt into the optional editor.

## Transaction model

Snapshots defensively own their bytes, metadata and revision identity. Edits operate on a private document, close it and reopen it through the validator before publishing. Expected revisions prevent delayed dialogs and stale geometry from modifying a different document state. Failed/cancelled edits preserve the prior snapshot.

Undo/redo retains complete immutable snapshots with entry/byte budgets. This favors reliable rollback, not constant-memory editing. Saving flushes a same-directory temporary file before replacing the destination. Filesystem permissions, backups and hostile-directory protection remain host responsibilities.

## Rendering design

Layout reads page metadata without eagerly interpreting all content. Visible rows are found through binary search. The viewport requests only visible 512-pixel tiles, progressively publishes them, and rejects stale document/zoom generations. A serialized background worker owns parser/display-list access. UI scenes retain image leases, so cache eviction cannot dispose an image still being drawn.

Default limits include 128 MiB raster cache, 24 display lists, two rendering parsers, 256 viewport tiles and 32 undo snapshots/256 MiB. Display lists/parsers are entry-bounded rather than fully byte-accounted. Complex streams, decoded images and fonts can still consume substantial memory/time.

**Tile rasterization is CPU Skia.** Avalonia can GPU-compose the images; WPF draws through a reusable premultiplied BGRA bitmap. No measured speed advantage over Acrobat or zero-copy GPU PDF interpretation is claimed. Benchmark cold open, first paint, warm scrolling, zoom, search and editing separately on representative PDFs and physical hardware.

## Ownership and security

Each window owns a viewport; several viewports may share a session. This is local synchronization, not multi-user collaboration. Applications own the session/renderer/viewport and signing keys. Detach controls, dispose workspace subscriptions, await viewport disposal and then renderer disposal. Dispose every acquired scene/tile lease.

Parsing is in-process, not an OS sandbox. Input/cache limits do not eliminate decompression bombs or upstream native/parser vulnerabilities. Restrict hostile documents to an isolated worker with OS resource limits. ProPDF does not execute PDF JavaScript, launch actions or attachments and does not automatically fetch external resources.

Page-region redaction is real content removal, not a visual overlay, but it is not full-document sanitization. Original files, backups and undo history remain unredacted. Cryptographic signature validity does not establish certificate trust, revocation or legal identity. See [Native editing](editing.md) for precise restrictions.

[Getting started](getting-started.md) · [Feature matrix](features.md) · [Build and release](build-release.md)
