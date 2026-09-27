# Existing-content appearance and image replacement

The owned editing library can change the appearance of selected native text, paths and image invocations without rasterizing the page. The shared **Edit** inspector exposes the same operations in Avalonia and WPF. These extend the [existing-object editing model](content-editing.md); they do not implement arbitrary rich paragraph editing or every PDF graphics construction.

## Selective appearance patches

`SetContentAppearance` takes a revision/fingerprint-bound handle and a `PdfContentAppearance` patch. Null properties preserve the original state. An empty patch is rejected. Available properties include RGB/alpha fill and stroke colors, line width, line caps and joins, dash arrays/phase, path painting mode, constant opacity, and all sixteen standard PDF blend-mode names.

```csharp
var page = await editor.ReadPageContentAsync(snapshot, 1);
var path = page.Objects.First(o => o.Kind == PdfContentObjectKind.Path && o.CanEdit);
var updated = await editor.ApplyAsync(snapshot, new IPdfEditOperation[]
{
    new SetContentAppearance(path.Reference, new PdfContentAppearance(
        FillColor: new PdfColor(220, 70, 40),
        StrokeColor: new PdfColor(40, 60, 90),
        StrokeWidth: 2,
        Opacity: 0.7,
        BlendMode: PdfBlendMode.Multiply,
        PathPaint: PdfPathPaintMode.FillAndStroke,
        LineCap: PdfLineCap.Round,
        LineJoin: PdfLineJoin.Round,
        Dash: new PdfDashPattern(new double[] { 6, 3 })))
});
```

Overrides are emitted immediately before the selected group's paint/text-show instructions, not just before the start of a text object. This matters when the original text group changes color between runs. Original fonts, text operators, spacing, matrices and painting order are retained. The original persistent graphics/text state is replayed after the isolated override so following content is not accidentally recolored or made transparent.

Changing a color sets its RGB components **and its alpha**. Explicit Opacity multiplies a supplied color alpha; without a supplied color it sets stroke and fill alpha directly. Thus a six-digit RGB color has alpha 1; use eight-digit RGBA to specify a different alpha. Leaving both color and opacity blank preserves the original alpha. An opacity of zero hides painting; it does not remove text or provide redaction.

Width, dash lengths and phase are in the object's **local PDF graphics space**, not screen pixels or normalized page points. A scaled/rotated content transform still affects them. A zero width is a PDF hairline. Dash arrays are immutable, have at most 32 finite nonnegative entries, and a nonempty pattern may not be entirely zero. Empty arrays select solid lines. Path painting changes preserve explicit close-path operations and the original even-odd/nonzero fill rule. Text rendering modes are not changed; an unpainted text mode stays unpainted.

Image invocations accept opacity and blend mode, not path colors or line styles. Form XObjects and shadings are not restyled by this operation because group/backdrop semantics require separate support. The existing read-only safeguards for marked/tagged/optional content, text clipping, active soft masks, unsupported fonts and other unqualified states still apply.

## Image replacement and interpolation

`ReplaceContentImage` allocates an image resource for **one selected invocation**. It does not overwrite a resource used by other invocations or pages. The original matrix, ancestor clipping and position in painting order are retained. Replacement pixels stretch to the original image's unit square; aspect-ratio fitting/cropping is not automatic.

```csharp
var image = page.Objects.First(o => o.Kind == PdfContentObjectKind.Image && o.CanEdit);
var bytes = await File.ReadAllBytesAsync("replacement.png");
var updated = await editor.ApplyAsync(snapshot, new IPdfEditOperation[]
{
    new ReplaceContentImage(image.Reference, new PdfBinaryAsset(bytes), Interpolate: true)
});
```

The shared image authoring path decodes static images with Skia into sRGB, unpremultiplied color samples and, when needed, an owned grayscale PDF soft mask. All eight EXIF orientations are applied to pixel storage, including dimension-swapping rotations. Decode is limited to 24 megapixels and incomplete/animated images are rejected rather than silently substituted with a partial result. The decoded row is copied incrementally to avoid another full RGBA copy, but native codec memory is not an OS-enforced sandbox. This path is not lossless JPEG passthrough, ICC profile preservation, CMYK/separation authoring or certified color-managed print production.

`SetContentImageInterpolation` copies the selected image's dictionary while retaining encoded image bytes and references. Other invocations keep their original setting. `PdfContentObject.ImageInfo` exposes dimensions, bits/component, color-space family, interpolation and soft-mask presence without decoding pixels. Named color-space resources may be reported by their resource name rather than resolved color characterization.

The UI has an Image section with metadata, replacement and interpolation commands. It captures the selected revision before showing a file picker. A document changed while the dialog is open cannot accidentally receive the old replacement. The UI accepts at most 32 MiB of encoded replacement bytes; the SDK asset and decoded-pixel budgets apply separately. Canceling a picker changes neither the document nor undo history.

## Rendering corrections and source ownership

The optional `ProPDF.Engine.PdfPig` adapter now compiles source pinned to PdfPig.Rendering.Skia 0.1.16.4 instead of referencing its binary package. This is explicitly **third-party Apache-2.0 source**, not ProPDF-authored interpreter code. The kernel and native editing operations remain owned MIT code. There are no new commercial/copyleft packages, and the already-used permissive parser, codec and shaping versions are unchanged.

The upstream sealed factory/internal processor does not expose a supported image-paint interception hook. A narrow compatibility patch applies nonstroking constant alpha to regular image paints and uses explicit nearest/linear sampling from `/Interpolate`. Each invocation clones the cached paint before setting alpha, preventing state leakage between uses of the same image. The backend passes cancellation through the interpreter's token-aware recording overload; individual native codec calls still are not interruptible OS-isolated work. Stencil images retain their existing color/alpha handling. Graphics-state soft-mask images apply constant alpha once in the inner paint and the blend mode once at outer composition. This avoids reflection, binary patching or changing saved PDF pixels to compensate for a renderer defect.

Every imported source carries its original copyright notice and a modification notice. The original LICENSE/NOTICE, exact source commit, original/compiled SHA-256 inventory and patch description are in `src/ProPDF.Engine.PdfPig/Compatibility/PdfPig.Skia`. CI rejects unreviewed source drift. The adapter package uses `MIT AND Apache-2.0`, contains the notices, and propagates notices to consuming application output. The other seven ProPDF libraries remain MIT-authored. This is not a claim that the complete renderer is now owned or that every transparency/font/codec case is qualified.

## Regression evidence and limits

Generated fixtures check native operators and independent rendered output: per-run text recoloring with unchanged font/position; path painting and even-odd holes; stroke/dash isolation; all blend-mode names, selected backdrop pixel cases and opacity endpoints; repeated edits and stale handles; shared images across pages and four rotations; soft masks and straight-alpha RGB; all eight EXIF orientations; interpolation without image-data changes; nearest/linear sampling; image alpha without cache leakage; and continuity across fractional tile origins. Shared-workspace tests cover invalid inputs, draft lifetime, picker cancellation, stale dialogs, image replacement, interpolation and undo. Native smoke checks execute the actual bound appearance command and inspect resulting PDF pixels.

Passing these regressions is not full Acrobat parity, complete PDF graphics/conformance qualification, physical-GPU benchmarking, or secure sanitization. Recoloring, zero opacity, image replacement and visual clipping are editing operations, not a full-file redaction guarantee. Original files and undo snapshots retain previous content.
