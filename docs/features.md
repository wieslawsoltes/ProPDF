# Feature matrix

Implemented means executable code exists, not qualification against every PDF/version/font/codec. ProPDF is development-alpha software, not a complete Acrobat replacement.

| Area | SDK | Built-in shell | Boundary |
| --- | --- | --- | --- |
| Loading/passwords | Implemented | Implemented | Bounded input, not a sandbox |
| Skia rendering | Implemented | Avalonia and WPF | CPU tile rasterization; upstream fidelity limits |
| Layout/zoom/pan/navigation | Implemented | Implemented | Continuous, facing, single-page |
| Lazy thumbnails | Implemented | Virtualized page strip | Bounded dimensions |
| Extraction/search | Implemented | Find/next/previous | Word-based; literal/regex |
| Selection/copy | Rectangular words | Implemented | Not semantic cross-page selection |
| Undo/revisions/atomic save | Implemented | Implemented | Whole-snapshot memory costs |
| Page rotate/delete/move/insert/duplicate | Implemented | Implemented | Native PDF changes |
| Crop/merge/extract | Implemented | Implemented | Extracted copies are unsigned/unencrypted |
| Text/image/vector insertion | Implemented | Implemented | No general existing-object editor |
| Region replacement | Implemented | Implemented | Removes all regional content; no reflow |
| Notes/free-text/highlights/underline/strikeout | Implemented | First three | Underline/strikeout API-only |
| Shape/link/ink annotations | Implemented | Shapes/ink | Link creation API-only |
| Annotation updates/deletion | Implemented | Implemented | Free-text appearances require replacement |
| AcroForm authoring | Text/checkbox/choice | Text/checkbox | No full field designer |
| Fill/remove/flatten forms | Implemented | Fill/flatten | No XFA or JavaScript calculation |
| Page-region redaction | pdfSweep cleanup | Stage/confirm/apply | Not complete sanitization; tagged PDFs/widgets blocked |
| Metadata/attachments | Implemented | Implemented | Attachments are never executed |
| Root bookmarks | Add/remove/inspect | Add/list | Hierarchy/navigation incomplete |
| AES-256 encryption | Implemented | Protected opening | Full permissions/password editor absent |
| Detached signing | Implemented | Not yet | Host keys/certificates; certified files blocked |
| Signature integrity | Implemented | Not yet | No certificate trust/revocation/LTV verdict |
| Shared session | Separate local viewports | Not a multi-window workspace yet | Not multi-user collaboration |
| Accessibility | Basic labels/keyboard | Partial | Full PDF text/UI Automation missing |
| Packaging | Seven NuGet libraries | Samples separate | Publication requires maintainer configuration |

## Major remaining Acrobat-level work

General existing-object editing and paragraph reflow; advanced typography and complex-script shaping; semantic/cross-page selection; complete forms/calculation/signature UI; XFA; OCR/scanning; Office/HTML conversion; printing and production-print support; document comparison; portfolios; optional-content layers; complete links/bookmarks; tagged-PDF authoring/reading order; PDF/A/PDF/UA conformance; preflight, overprint, separations and ICC; multimedia/3D; certificate trust/revocation/timestamps/PAdES-LTV; secure enterprise collaboration; hostile-input process isolation; certified sanitization; physical-machine performance qualification.

## Qualification

Generated regression fixtures cover real output, independent parsing, raster pixels, transaction failures and native resource lifetime. They are not a complete ISO 32000 corpus. Add licensed representative files with embedded/Type3/CID fonts, CMaps, bidirectional scripts, masks, transparency, codecs, damaged cross-reference streams, tags, optional content, signatures and incremental history. Compare in independent viewers and qualify accessibility/performance on real machines.

Prioritize rendering correctness, process isolation and benchmarks before widening compatibility/performance claims. Every feature needs output round trips, UI tests where applicable, resource limits and explicit boundaries.
