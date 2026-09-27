# Existing content editing and wrapped text

ProPDF's owned `ManagedPdfEditor` can inspect and modify existing page paint objects. These operations rewrite native PDF content streams; they are not application-side overlays. The renderer and both desktop controls use the resulting immutable document revision.

## Objects and handles

`IPdfContentService.ReadPageContentAsync` returns a `PdfPageContent` with a revision, page number and ordered objects. The supported units are a complete `BT`/`ET` text group, a painted path, an image XObject invocation, or a Form XObject invocation. Forms are edited as invocations without changing a resource shared by other pages or invocations. Shadings are listed as read-only. This is not a recursive editor of every object inside a form.

Each `PdfContentObjectReference` includes the document revision, page, instruction index and a page-content fingerprint. Re-inspect after a page edit. A stale reference, including a second handle invalidated by an earlier operation in the same batch, rejects the entire transaction. Object indexes are not persistent identifiers across revisions or content normalization.

Bounds are approximate logical selection rectangles, not exact glyph ink or path outlines. They include font metrics and stroke estimates and do not compute arbitrary clipping intersections. Bezier control hulls can be larger than the visible curve. A visually clipped object can have bounds outside its visible region. Existing ancestor clips stay in place when an invocation is transformed. These bounds must not be used as a security or redaction guarantee.

```csharp
var page = await editor.ReadPageContentAsync(snapshot, pageNumber: 1);
var item = page.Objects.First(obj => obj.CanEdit);
var changed = await editor.ApplyAsync(snapshot, new IPdfEditOperation[]
{
    new TransformContentObject(item.Reference,
        PdfAffineTransform.Translation(24, 12))
});
```

All geometry uses the shared top-left, cropped/rotated page coordinate system in PDF points. The editor conjugates the requested affine transform through the page and content matrices. Rotated pages, nonzero crop origins and UserUnit are covered by regression fixtures.

## Native operations

`TransformContentObject` moves, scales, rotates or reflects an object. `DuplicateContentObject` inserts a transformed copy immediately before the original invocation; shared resources are not duplicated unnecessarily. `DeleteContentObject` removes only the selected paint group, preserving other overlapping objects. The writer replays persistent graphics/text state as necessary so an edit does not accidentally alter the color, transform, spacing or font of subsequent objects.

`ClipContentObject` clips a selected object to a page-view rectangle. **Clipping is visual cropping, not redaction**: hidden content remains in the PDF and may still be extracted. Use the separately qualified redaction workflow for content removal, and review its limitations.

`ReplaceContentText` replaces a selected text group with native wrapped text inside an explicit rectangle. Other overlapping content remains at its existing paint order. It does not remove everything in that rectangle, unlike `ReplaceRegionText`. The chosen group may contain several runs or lines; replacement applies to the whole group, not one selected substring. Original formatting is replaced by the specified font, size, alignment and color. It does not automatically reflow neighboring page objects.

## Wrapped text boxes

`AddTextBox` authors new text with left, center or right alignment, explicit font size and line spacing. Widths come from the selected font's metrics. A long word is split at Unicode text-element boundaries, avoiding broken surrogate pairs. Newlines are normalized and whitespace at wrap boundaries is normalized. This is ordinary text-box layout, not byte-for-byte preservation of whitespace or a complex-script shaping engine.

```csharp
await session.ApplyAsync(new AddTextBox(
    PageNumber: 1,
    Bounds: new PdfRect(40, 80, 240, 120),
    Text: "A paragraph that wraps inside the selected box.",
    FontSize: 14,
    Alignment: PdfTextAlignment.Left), session.Current!.Id);
```

Text that cannot fit is rejected before publication; lines are never silently dropped. The default Helvetica and Courier metrics and supported explicit/embedded TrueType metrics are implemented. Missing glyphs and unsupported metrics are rejected. Supply an appropriately licensed embeddable font through the SDK for supported Unicode authoring. CFF/collections, arbitrary font encodings, complex-script shaping, rich paragraph styles and preservation of existing rich typography remain separate work. The built-in inspector uses the default font; it is not a font-file picker.

## Desktop workflow

The **Edit** inspector is shared between Avalonia and WPF. Choose **Select / drag** (or the **Edit existing objects** tool), then select an item from the list or click its page rectangle. Overlapping objects use reverse paint-order hit testing; the list can select an underlying object. Drag the selection to move it or drag a corner handle to resize it. Position and size fields, rotate, flip, duplicate and delete commands write native edits and support undo.

Inspection is lazy and bounded to one requested page. When a newly visible page has not yet been inspected, the first click starts loading; click again when its object list is ready. Read-only objects remain listed with a reason rather than being silently modified. A new document revision clears obsolete selections. Escape/pointer-capture loss cancels a gesture. The inspector's wrapped replacement uses the displayed dimensions, text, font size and alignment. The **Wrapped text box** tool inserts a new box using the toolbar text.

To visually crop an object, select it, switch to **Select region**, draw a rectangle on the same page, and choose **Clip to selected region**. The UI explicitly warns that clipping is not redaction.

## Conservative boundaries

Owner authorization is required and signed documents cannot be rewritten. Tagged/marked/optional content, text that changes a clip, paths that both paint and change clipping, soft masks, pattern-coordinate changes, unsupported font states, nested state inside an unfinished paint group, unknown operators and inline images are rejected or exposed read-only. An unsupported object-edit inspection does not disable ordinary PDF viewing. This implementation does not claim full Acrobat object/paragraph editing or every legal PDF graphics construction.

Inspection has instruction, object, recursion, text and regular-expression limits. It is still in-process, not an OS sandbox. Native parser/codec memory and pathological individual operations require process isolation for strict hostile-input guarantees.

## Tile rendering and memory

Raster tile origins now use device-space translations calculated in double precision before conversion to Skia's matrix floats. Near-integer tile dimensions are snapped to avoid an extra rounding-induced row or column. A two-pixel gutter supplies local antialiasing/filter support before the tile is cropped. It is not an unlimited blur/filter halo.

The display-list LRU is bounded by both entry count and `MaximumDisplayListBytes` (64 MiB by default). A picture larger than the configured estimate is rendered transiently instead of retained. `GetStatisticsAsync` reports `ApproximateDisplayListBytes`. Skia's estimate does not account for all externally referenced images, fonts, decoder state or native allocation overhead; this is not a total process-memory bound. Raster cache accounting similarly uses logical image dimensions rather than every native allocation.

Sampling regression tests compare assembled tiles against a whole-page image at fractional scales. Vector coverage is checked separately with RMS, changed-area and tile-border error limits: Skia can produce slightly different antialiased vector-edge coverage when a path is clipped into tiles. Pixel-identical rendering of all vector paths is not claimed, and no speed advantage over Acrobat has been established.
