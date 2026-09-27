# Feature matrix

Implemented means executable code and regression coverage exist, not qualification against every PDF/font/codec. ProPDF is development-alpha software, not a complete Acrobat replacement.

| Area | SDK | Built-in shell | Boundary |
| --- | --- | --- | --- |
| Loading/passwords | Implemented | Implemented | Bounded input, not an OS sandbox |
| Skia rendering | Implemented | Avalonia and WPF | CPU raster tiles; upstream fidelity limits |
| Layout/zoom/pan/navigation | Implemented | Implemented | Continuous, facing, single-page |
| Lazy thumbnails | Implemented | Virtualized page strip | Bounded dimensions |
| Extraction/search | Implemented | Find/next/previous | Word-based; literal/regex; stale result rejection |
| Selection/copy | Rectangular words | Implemented | Not semantic cross-page selection |
| Undo/revisions/atomic save | Implemented | Implemented | Whole-snapshot memory costs |
| Page organization | Rotate/delete/move/insert/duplicate/crop/merge/extract | Implemented | Extracted copies are unsigned/unencrypted |
| Page ranges | Ordered ranges, last, odd/even, deduplication | Text and page extraction | One-based, bounded input/results |
| Raster output | PNG/JPEG/WebP page/region | PNG/JPEG current page with DPI | Bounded assembled raster; not vector export |
| UTF-8 export | Streaming selected pages | Implemented | Extraction order; not OCR/layout reconstruction |
| Document comparison | Page-aligned visual/text difference | Compare/list/navigate/JSON report | No automatic alignment, semantic or metadata equivalence |
| Text/image/vector insertion | Implemented | Implemented | Native insertion; no complex-script shaping |
| Existing page content | Inspect, affine transform, duplicate, delete and clip | Object list, click/drag, corner resize and inspector commands | Whole text/path groups or XObject invocations; approximate bounds; conservative exclusions |
| Content appearance | Native text/path colors, width/cap/join/dashes, painting modes, opacity and blend mode | Shared appearance inspector | Local graphics units; fonts/positions retained; Form/shading/marked cases excluded |
| Image replacement and interpolation | Per-invocation resources, dimensions/metadata, static sRGB/alpha authoring with EXIF orientation | Image section and revision-aware picker | Stretches to existing geometry; 24 MP decode limit; no lossless JPEG/CMYK/ICC preservation |
| Wrapped text and object replacement | AddTextBox and ReplaceContentText | Wrapped box tool and selected-text replacement | Overflow rejected; replaces whole text group; no adjacent-object paragraph reflow |
| Region replacement | Implemented | Implemented | Removes all regional content; no paragraph reflow |
| Notes/free-text/highlight/underline/strikeout | Implemented | First three tools | Underline/strikeout API-only |
| Shape/link/ink annotations | Implemented | Shapes/ink/internal links | Axis-aligned ink supported; no page-click link activation yet |
| Annotation update/delete | Implemented | Implemented | Free-text appearance changes require replacement |
| AcroForm authoring | Text/checkbox/choice | Text/checkbox | No full field designer |
| Fill/remove/flatten forms | Implemented | Fill/flatten | No XFA or JavaScript calculations |
| Page-region redaction | Owned content-group cleanup | Stage/confirm/apply | Whole intersecting groups may be removed; tags/ActualText/inline images/patterns/soft masks and other unqualified cases rejected |
| Metadata/attachments | Implemented | Implemented | Attachments are never executed |
| Hierarchical bookmarks | Read/insert/update/delete/move subtree | Navigate, insert child, rename, delete | Subtree movement API-only; revision-relative paths |
| Destinations and links | Common explicit/named local targets, safe URI and named actions | Inspector Follow/Copy; back/forward history | External confirmation; unsafe/chained/remote actions disabled |
| AES-256 encryption | Implemented | Protected opening | Full permissions/password editor absent |
| Detached signing | Implemented | Not yet | Host keys/certificates; certified files blocked |
| Signature integrity | Implemented | Not yet | No certificate trust/revocation/LTV verdict |
| Shared session | Separate local viewports | No multi-window workspace UI yet | Not multi-user collaboration |
| Accessibility | Basic labels/keyboard | Partial | Full PDF text/UI Automation missing |
| Packaging | Eight reusable NuGet libraries | Samples separate | Public publication requires credentials/configuration |

## Major remaining Acrobat-level work

Recursive/general content editing beyond supported paint groups, rich paragraph editing and adjacent-object reflow; advanced typography and complex-script shaping; semantic/cross-page selection; complete forms/calculation/signature UI; XFA; OCR/scanning; Office/HTML conversion; printing and production-print support; semantic comparison and automatic page alignment; portfolios; optional-content layer editing; complete page-click links and outline drag/drop; tagged-PDF authoring/reading order; PDF/A/PDF/UA conformance; preflight, overprint, separations and ICC; multimedia/3D; certificate trust/revocation/timestamps/PAdES-LTV; secure enterprise collaboration; hostile-input process isolation; certified sanitization; physical-machine performance qualification.

## Validation and qualification

The CI matrix builds all projects, tests independently reopened PDFs, raster pixels, export codecs, transactions, navigation/history and resource lifetime; renders actual Avalonia/WPF editors headlessly; verifies native command bindings; and restores/executes clean consumers of all eight packages. Passing these checks is not full ISO 32000 conformance.

Add licensed representative files covering embedded/Type3/CID fonts, CMaps, bidirectional scripts, masks, transparency, codecs, damaged cross-reference streams, tags, optional content, signatures and incremental histories. Compare in independent viewers and qualify accessibility/performance on physical machines. Each new feature must state its interoperability and security boundaries.

[Appearance and images](content-appearance.md) · [Existing content and wrapped text](content-editing.md) · [Output and comparison](output-comparison.md) · [Navigation and bookmarks](navigation.md)

## Owned backend qualifications

The PDF object model, parser, stream filters, writer, native edits and Standard security handler are now ProPDF-owned MIT code. TrueType cmap 4/12 embedding and Unicode ToUnicode are implemented. Existing SDK operations are retained, but conservative content-group redaction is not partial-image/glyph editing. Passwords currently use printable ASCII; full international preparation, CFF/collections, complex shaping, public-key encryption and damaged-file repair remain gaps. See [Owned engine](owned-engine.md).

Multi-object selection now supports atomic same-page move/resize/rotate/flip/duplicate/delete, alignment and center/gap distribution in both native editors. The 1,000-object limit, whole-invocation semantics and logical-bound restrictions are described in [Content editing](content-editing.md#multi-object-selection-and-atomic-editing).
