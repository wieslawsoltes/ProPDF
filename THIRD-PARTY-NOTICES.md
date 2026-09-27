# Third-party notices

ProPDF-authored code is Copyright (c) 2026 ProPDF contributors and MIT licensed (see LICENSE).

ProPDF.Kernel has no third-party package references. ProPDF.Editing uses SkiaSharp for image decoding and System.Security.Cryptography.Pkcs for CMS; its PDF parser, writer, security protocols and document operations are owned ProPDF code. No iText, pdfSweep, commercial PDF backend or copyleft PDF library is included in the current build.

Permissive dependencies include SkiaSharp/HarfBuzzSharp (MIT and bundled native notices), Avalonia (MIT and bundled asset/native notices), PdfPig and its Skia/JPEG/JBIG2 adapters (Apache-2.0), OpenJPEG adapter (BSD-2-Clause), ANGLE (BSD-3-Clause) and .NET cryptography/runtime components (MIT and bundled notices). Test-only dependencies include xUnit (Apache-2.0), Microsoft test infrastructure, Newtonsoft.Json and coverlet (MIT). pypdf (BSD-3-Clause) produces synthetic encryption fixtures; it is not a runtime dependency.

Copyright notices and complete license texts supplied by these packages remain authoritative. Preserve them, including native codec, graphics, shaping and font-asset notices, when redistributing compiled applications. These acknowledgements do not replace upstream copyright/license files. The release workflow bundles the restored packages' available license/notice files alongside application archives; the machine-readable audit records exact package versions and package/manifest hashes.

Primary sources: https://github.com/mono/SkiaSharp ; https://github.com/harfbuzz/harfbuzz ; https://github.com/AvaloniaUI/Avalonia ; https://github.com/UglyToad/PdfPig ; https://github.com/BobLd/PdfPig.Rendering.Skia ; https://github.com/uclouvain/openjpeg ; https://chromium.googlesource.com/angle/angle/+/main/LICENSE ; https://github.com/dotnet/runtime ; https://github.com/xunit/abstractions.xunit ; https://github.com/py-pdf/pypdf .

The CI license gate validates resolved package declarations and reviewed exact legacy exceptions. It is not legal advice, a relicensing of dependencies, or a claim that every compiled native component was independently audited. Applications must retain relevant notices from their exact deployment dependencies.
