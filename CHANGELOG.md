# Changelog

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
