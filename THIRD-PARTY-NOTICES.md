# Third-party notices

ProPDF-authored source is Copyright (c) 2026 ProPDF contributors and MIT licensed (see LICENSE). ProPDF.Kernel has no third-party package references. The PDF parser, writer, security protocols and editing operations in ProPDF.Editing are owned code; image decoding and CMS use permissive SkiaSharp and .NET components. No iText, pdfSweep or commercial PDF backend is included.

Permissive dependencies include SkiaSharp/HarfBuzzSharp, Avalonia, Uno Platform, PdfPig and its codec adapters, OpenJPEG, ANGLE and .NET runtime/cryptography components. Test dependencies include xUnit and Microsoft test infrastructure. Their exact copyright notices, full license texts and native-code notices remain authoritative and must be preserved in redistribution.

The fail-closed dependency audit records versions, package/manifest hashes and explicit legacy reviews. It is not legal advice, a relicensing of upstream components or certification of every native binary. The release/site pipelines retain collected legal documents; package notice targets propagate notices to consuming outputs.

## Source-pinned PDF interpreter

`ProPDF.Engine.PdfPig` contains explicitly attributed Apache-2.0 source from BobLd/PdfPig.Rendering.Skia 0.1.16.4, commit `e4476d80f98bf1a5a7cd6f7d45fb7c2afe10ec11`. Copyright BobLd and other notices are retained. It is not represented as ProPDF-owned interpreter code. Its compiled package uses **MIT AND Apache-2.0**.

Exact LICENSE/NOTICE, provenance and modification descriptions are in `src/ProPDF.Engine.PdfPig/Compatibility/PdfPig.Skia/` and the package's `licenses/PdfPig.Skia/`. Thirty-three files are hash-verified. The namespace is isolated and image alpha/interpolation corrections are documented. No fonts, private PDFs or keys were imported with these sources.

## Uno runtime selection and legacy notices

Uno packages are explicitly selected rather than using implicit media, logging or developer tools. The sample excludes stock X11/Win32 hosts that introduce LGPL video or non-permissive Windows SDK metadata. It does not grant exceptions for those packages or silently relicense them. Native Uno execution in this sample is limited to macOS; the browser sample and existing Avalonia/WPF editors remain separate supported routes.

Legacy `Uno.Diagnostics.Eventing/2.0.1` has a missing package declaration; its exact manifest is reviewed against the upstream Apache-2.0 license. The reviewed ICU 77 packages combine Uno's Apache-2.0 wrapper with the Unicode License V3 runtime and the complete ICU third-party notices. They do not grant blanket approval to unrelated build scripts or packages.

`scripts/fetch-uno-notices.py` retains the full source-pinned Eventing, Uno ICU and ICU 77 license documents. All three are bundled under `licenses/Uno/` in `ProPDF.Uno`, copied into consumer outputs and staged with the website. Every fetched text is bounded and verified against its reviewed Git blob; changed text requires review. Preserve the complete notices, not merely this summary.

Primary repositories: https://github.com/unoplatform/uno ; https://github.com/unoplatform/Uno.Diagnostics.Eventing ; https://github.com/unoplatform/uno.icu ; https://github.com/unicode-org/icu ; https://github.com/mono/SkiaSharp ; https://github.com/harfbuzz/harfbuzz ; https://github.com/AvaloniaUI/Avalonia ; https://github.com/UglyToad/PdfPig ; https://github.com/BobLd/PdfPig.Rendering.Skia .
