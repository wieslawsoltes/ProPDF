# Text rendering and typography

## PDF positioning versus substitute-font shapes

A PDF can specify glyph advances independently of the font installed on a viewing machine. Previously the optional renderer positioned successive glyphs using the PDF metrics but drew substitute glyphs at host-specific widths. Narrow or wide custom advances could produce overlaps or inconsistent spacing, particularly in the browser.

For horizontal, non-embedded fonts, ProPDF now shapes a Unicode mapping in font-design coordinates and fits its **advance**, not its ink bounding box, to the PDF character width. The interpreter still owns character/word spacing, `TJ` displacement, horizontal scaling, text rise and text matrices. Embedded glyph outlines are not resized by this fallback rule. Vertical writing is not horizontally fitted. Zero/negative advances retain their unscaled fallback instead of dividing by zero; extreme fitting ratios are rejected.

Fitted outlines use the existing vector text painting route, including fill/stroke modes and text clipping. PDF bytes and extracted text are not rewritten to achieve a viewing correction. This is not font recovery: the substitute's letter shapes, kerning, vertical metrics and language coverage can differ from the original. Fonts with no usable glyph outlines retain the bitmap draw route and are not covered by the outline-fidelity claims.

## Bounded design-space outline cache

Each resolved typeface owns an LRU cache of at most **256** Unicode/direction mappings and approximately **512 KiB**. Font size and zoom are deliberately absent from the key. Shaping uses a 1000-unit design em rather than a one-pixel font; HarfBuzz/Skia's native position quantization still applies. Both surrogate-pair and multi-codepoint mappings remain intact; mappings longer than 1024 UTF-16 code units are rejected before shaping.

Outline loans hold a reference independently of cache membership. Eviction or cache disposal cannot invalidate a live loan. Oversized entries are used transiently rather than increasing retained cache size. The budget estimates outline/text data, not all native typeface, HarfBuzz, parser or decoded-object memory. Active loans and the number of resolved typefaces affect total memory.

`FallbackTypographyTests` checks that 1000 identical mappings produce one shape miss and 999 hits; it also covers Unicode mapping, concurrent acquisition, LRU eviction, oversized entries and retained native lifetime. Synthetic missing-font PDFs independently test narrow/wide advances, character spacing, `TJ`, fill/stroke and text clipping. These are correctness and work-count results, not a hardware benchmark or complete font corpus.

## Shared editor controls

In the **Edit → Text / typography** section, select a supported standard face, font size, line-spacing multiplier and text alignment. The current eight choices are Helvetica and Courier, each with regular, bold, oblique and bold-oblique variants. They correspond to metrics supported by the owned Latin text-box implementation; unsupported faces are not presented as working choices.

The face and size apply to Insert text, Insert text box and whole-object text replacement. The line-spacing multiplier applies to wrapped boxes/replacement; the line advance is at least the font's ascent plus descent, even with a smaller multiplier. Newline characters create explicit lines. Replacement uses the position/size fields and restyles the entire selected text object. Other content and painting order are retained, and a successful replacement is a single undoable transaction.

The SDK exposes `ReplaceContentText.LineSpacing` (default 1.2) as well as the existing `AddTextBox.LineSpacing`. Invalid size/spacing, missing glyphs, or text that does not fit reject the edit without changing the published snapshot. Invalid UI drafts are not silently replaced with the last valid value.

## Dependencies and qualification

No font programs, font-file downloads or additional packages are introduced. The outline cache and editing/presentation changes are ProPDF-owned MIT code. The optional PDF-to-Skia interpreter remains explicitly attributed Apache-2.0 third-party code; its narrow modifications and original/compiled hashes are retained in `Compatibility/PdfPig.Skia/PATCHES.md` and `PROVENANCE.json`.

This does not implement rich paragraph reflow, arbitrary nested text-object editing, complete complex-script layout, CFF/collection font authoring, ICC/overprint typography, PDF/UA reading order or universal font substitution. Synthetic and native/browser regression checks qualify their tested cases, not every PDF viewer or every installed font. Hosts still need representative document/font corpora and appropriately licensed fonts.
