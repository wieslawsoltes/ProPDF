# Changelog

## 0.1.0-alpha.7 — Unreleased

Browser density-only transitions synchronize the Uno canvas and managed viewport through an owned event-driven host observer. Regressions require stable compositor geometry as well as high-resolution tiles and thumbnails.

Instance-scoped host font catalogs preserve real fallback styles; the Uno sample registers its existing Open Sans assets before opening PDFs, without new dependencies or font binaries. Density-aware viewers/thumbnails across Avalonia, WPF and Uno; fractional page-edge source extents no longer squeeze rounded pixel padding. Paint caches use complete structural identities instead of hash-only keys. Synthetic font/style/isolation, display-density/cancellation, paint-collision and actual browser punctuation/bold/Retina regression checks. Font substitution remains distinct from original-font recovery; see `docs/text-rendering.md` and `docs/performance.md`.

## 0.1.0-alpha.6 — Unreleased

Fit non-embedded substitute glyph advances to explicit PDF widths without changing text positioning or saved bytes. Reuse bounded, reference-counted, design-space glyph outlines across font sizes and zoom levels; preserve fill/stroke/text-clip behavior and resolved default-family styles. Add shared standard-font and line-spacing controls for native text insertion and whole-object replacement in Avalonia, WPF and Uno. Independent fallback-width pixel fixtures, Unicode/cache-lifetime regressions, atomic typography validation and native/browser control round trips. No font files or new dependencies; source-pinned Apache-2.0 interpreter modifications remain attributed and hash-inventoried. See `docs/text-rendering.md`.

## 0.1.0-alpha.5 — Unreleased

Independent warm-tile cache leasing avoids waiting behind unrelated interpreter work. Stable viewport tile plans avoid redundant scroll cancellation/submission, progressive publication retains matching visible tiles, and dispatcher notifications coalesce. Adds page- and region-relative whole-selection alignment across Avalonia, WPF and Uno, with atomic undo and cropped/rotated coordinates. Deterministic concurrency, cache lifetime, scheduling and native command regression coverage; no new dependencies or GPU/Acrobat speed claims. See `docs/performance.md` and `docs/content-editing.md`. Uno page lists now read revision and page entries from one immutable snapshot, fixing empty thumbnails when notifications arrive ahead of workspace refresh. Browser validation checks thumbnail pixels and pointer navigation, and awaits exact native list identity after undo.

## 0.1.0-alpha.4 — Unreleased

Add independently packable Uno Platform controls and a .NET 10 browser/desktop sample using the shared editing/rendering engine. Add host file-transfer completion semantics, browser imports/downloads, native inspector panels, browser regression CI and a unified app-plus-documentation Pages deployment.

## Unreleased — atomic multi-object editing

Same-page multi-selection, revision/fingerprint-bound aggregate edits, move/resize/rotate/flip/duplicate/delete, alignment and center/gap distribution. Shared selection geometry and native Avalonia/WPF list/canvas interactions, selection overlays and group commands. Single-object appearance/replacement tools are disabled for groups. Background rendering no longer clears editing diagnostics. Includes atomic rollback, shared-resource, rotation/crop, state-preservation, geometry and native list/command regressions. See `docs/content-editing.md`.

## 0.1.0-alpha.3 — Unreleased

Owned per-object text/path appearance patches (colors, width/cap/join/dashes, painting, opacity/blend), per-invocation image replacement/interpolation, bounded static sRGB/straight-alpha authoring and all eight EXIF orientations; shared Avalonia/WPF controls and stale-dialog protection. Optional source-pinned Apache-2.0 rendering compatibility fixes regular-image opacity and nearest/linear sampling without modifying saved PDFs. Preserved license/NOTICE/provenance, source-drift gates and output-notice propagation; no commercial/copyleft dependency additions. This is not full Acrobat/conformance/color-management qualification.


## Unreleased — existing content editing

Owned content inspection and revision-bound native transforms, duplication, deletion, visual clipping and wrapped text replacement; shared object selection/drag/corner-resize controls and Edit inspectors for Avalonia/WPF; state-preservation and overflow checks; fractional-scale tile sampling corrections and an approximate display-list byte budget. Includes native command-binding checks and independent parsing/pixel/interaction regressions. Supported paint groups and conservative exclusions are documented in `docs/content-editing.md`; full Acrobat paragraph editing is not claimed.


## 0.1.0-alpha.2 — Unreleased

Replaced the previous iText/pdfSweep backend with ProPDF-owned MIT libraries: `ProPDF.Kernel` and `ProPDF.Editing`. Added classic/stream/hybrid cross-references, compressed objects, bounded lossless filters/predictors, reachable/incremental serialization, Standard-security R2–R6 reading and AES-256 R6 writing, TrueType embedding, native edits/forms/annotations/navigation, conservative content-group redaction, and platform RSA/ECDSA detached signing. Preserved the Core/UI contracts and migrated the regression suite. Added a fail-closed permissive transitive-license gate, independent cipher fixtures, generated font tests and package provenance/notices. The solution now ships eight reusable packages.

This is not complete Acrobat parity. The owned backend has explicit restrictions for international password preparation, advanced typography, redaction semantics, tagged import, certified-document changes and signature trust. See the migration and owned-engine guides.

## Unreleased — 0.1.0-alpha.1 development

### Shared engine and native editors

Immutable PDF snapshots, revision-aware transactions, bounded history, atomic saving, PdfPig loading/extraction/search, cached Skia rendering, optional iText editing/redaction/signatures, and shared Avalonia/WPF editor controls. The original feature PRs #1–#4 were validated and merged.

### Output and comparison — PR #5

Ordered page selections, bounded PNG/JPEG/WebP page and region export, streaming UTF-8 text output, visual/text page comparison, difference navigation and JSON reports. Both native shells expose shared output commands. Regression tests cover ranges, codecs, tiles, cancellation and comparison behavior.

### Navigation and outlines — PR #6

Explicit/named local PDF destinations, bounded/cycle-aware outline reading, hierarchical bookmark editing, internal-link authoring, native navigation inspectors and bounded revision-aware back/forward history. External URI activation requires explicit confirmation; unsafe/remote/chained actions are not executed.

### Correctness and delivery

Fixed search-page clamping, out-of-order search publication, straight ink strokes, WPF cross-thread notifications and page-input replacement during rendering. Repaired action pins and external NuGet consumer validation, including Windows build-server cleanup. Documentation and release workflows are implemented; public Pages deployment and NuGet publication still require repository configuration and credentials.

This changelog describes source changes, not a published release or complete Acrobat compatibility. See docs/features.md for remaining work.
