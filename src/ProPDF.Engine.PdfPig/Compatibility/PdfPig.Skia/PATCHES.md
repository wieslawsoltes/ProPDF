# Source-pinned optional rendering compatibility adapter

This directory contains **third-party Apache-2.0 code**, not ProPDF-owned PDF interpreter code. Source is pinned to BobLd/PdfPig.Rendering.Skia 0.1.16.4, commit `e4476d80f98bf1a5a7cd6f7d45fb7c2afe10ec11`. Original copyright headers, LICENSE and NOTICE are retained. `PROVENANCE.json` records original and compiled-source SHA-256 hashes.

The upstream public factory is sealed and its stream processor is internal, so an image-paint correction cannot be injected through the supported public API. Compiling the pinned source into the optional adapter avoids reflection, binary patching, rendering-only PDF rewrites, or permanently baking a viewer workaround into saved document pixels. Core, Kernel, Editing, Presentation and the UI libraries do not include this source.

## Narrow changes

Every source file moves from `UglyToad.PdfPig.Rendering.Skia` to `ProPDF.Engine.PdfPig.Compatibility` so hosts can still reference the upstream package without duplicate fully qualified types. A modification notice is added. Parent-namespace imports are made explicit. Nullable stroke/pattern invariants and XML parameter documentation are made explicit so copied code builds with the repository warning-as-error policy; the obsolete image `Bounds` alias becomes `BoundingBox`. These do not disable any compiler diagnostics. Upstream visual debug overlays require the explicit `PROPDF_RENDER_DIAGNOSTICS` build symbol rather than appearing automatically in ordinary Debug builds. `SkiaStreamProcessor.Image.cs` additionally clones the cached regular-image paint for each invocation, applies nonstroking alpha `/ca`, and supplies linear or nearest sampling from `/Interpolate` to regular, stencil and graphics-state-soft-mask image draws. Cached paints are never mutated. Soft-mask compositing retains the original outer blend mode and uses Normal for its inner draw.

No fonts, PDF fixtures, private keys or upstream project signing key are imported. Existing permissive codec/font package versions are unchanged; the binary PdfPig.Rendering.Skia dependency is removed. The assembly package license expression is `MIT AND Apache-2.0`; notices are included in packages and native sample output.

## Text rendering corrections (alpha.6)

`SkiaStreamProcessor.Glyph.cs` fits non-embedded substitute outlines to the PDF character advance and sends them through the same fill/stroke/text-clip path as embedded outlines. It does not change the text pen, embedded fonts or document bytes. An owned `PdfFallbackGlyphCache` outside this third-party directory provides bounded design-space Unicode/direction outline loans. Size and zoom are not cache keys. Bitmap-only glyphs retain the upstream draw route; complex vertical/color-font behavior is not newly qualified. Temporary glyph paths are disposed. `SkiaFontCacheItem` owns/disposes its glyph cache, and `SkiaFontCache.Font.cs` retains resolved styles even in the default family, avoids per-character UTF-32 byte allocation, and does not evaluate unmaterialized glyph paths during disposal. Original upstream hashes remain unchanged; only reviewed compiled-source hashes are updated.

## Maintenance

Review upgrades against the pinned commit and this hash inventory, preserve notices, and run source-inventory, pixel, native UI and external-package checks. The inventory detects accidental additions/removals/edits; it is not a signature or security audit. This patch fixes the covered image paths, not every PDF transparency, group, color-management, font, codec or conformance case.

## Host font catalogs and paint identity (alpha.7)

`SkiaPageFactory.cs` associates an immutable caller catalog with each parser via its ParsingOptions identity. `SkiaFontCache.Font.cs` resolves explicit family/style aliases to document-owned native typefaces, reuses them, checks complete Unicode coverage and releases them on disposal. The catalog implementation is separate ProPDF-owned code; no font program is included in these sources. `SKPaintCache.cs` compares full color/stroke/dash/blend keys and snapshots dash arrays on cache misses instead of treating a 32-bit hash as identity. Original upstream hashes and license notices are retained.
