# Source-pinned optional rendering compatibility adapter

This directory contains **third-party Apache-2.0 code**, not ProPDF-owned PDF interpreter code. Source is pinned to BobLd/PdfPig.Rendering.Skia 0.1.16.4, commit `e4476d80f98bf1a5a7cd6f7d45fb7c2afe10ec11`. Original copyright headers, LICENSE and NOTICE are retained. `PROVENANCE.json` records original and compiled-source SHA-256 hashes.

The upstream public factory is sealed and its stream processor is internal, so an image-paint correction cannot be injected through the supported public API. Compiling the pinned source into the optional adapter avoids reflection, binary patching, rendering-only PDF rewrites, or permanently baking a viewer workaround into saved document pixels. Core, Kernel, Editing, Presentation and the UI libraries do not include this source.

## Narrow changes

Every source file moves from `UglyToad.PdfPig.Rendering.Skia` to `ProPDF.Engine.PdfPig.Compatibility` so hosts can still reference the upstream package without duplicate fully qualified types. A modification notice is added. Parent-namespace imports are made explicit. Nullable stroke/pattern invariants and XML parameter documentation are made explicit so copied code builds with the repository warning-as-error policy; the obsolete image `Bounds` alias becomes `BoundingBox`. These do not disable any compiler diagnostics. Upstream visual debug overlays require the explicit `PROPDF_RENDER_DIAGNOSTICS` build symbol rather than appearing automatically in ordinary Debug builds. `SkiaStreamProcessor.Image.cs` additionally clones the cached regular-image paint for each invocation, applies nonstroking alpha `/ca`, and supplies linear or nearest sampling from `/Interpolate` to regular, stencil and graphics-state-soft-mask image draws. Cached paints are never mutated. Soft-mask compositing retains the original outer blend mode and uses Normal for its inner draw.

No fonts, PDF fixtures, private keys or upstream project signing key are imported. Existing permissive codec/font package versions are unchanged; the binary PdfPig.Rendering.Skia dependency is removed. The assembly package license expression is `MIT AND Apache-2.0`; notices are included in packages and native sample output.

## Maintenance

Review upgrades against the pinned commit and this hash inventory, preserve notices, and run source-inventory, pixel, native UI and external-package checks. The inventory detects accidental additions/removals/edits; it is not a signature or security audit. This patch fixes the covered image paths, not every PDF transparency, group, color-management, font, codec or conformance case.
