# Permissive dependency policy

All ProPDF-authored source is MIT. The PDF kernel and editing implementation are owned code, not commercial or copyleft PDF wrappers. The former iText/pdfSweep implementation and adapters were removed; they are not built, restored, packaged or shipped on this development line. Git history remains intact for provenance.

| Component | Responsibility | License declaration |
| --- | --- | --- |
| ProPDF.Kernel / Core / Editing / Presentation / controls | Owned PDF engine and application integration | MIT |
| SkiaSharp / HarfBuzzSharp | Drawing, image codecs and text infrastructure | MIT; preserve bundled native notices |
| PdfPig and PDF-to-Skia integration | Optional independent parsing/extraction/rendering | Apache-2.0 |
| PdfPig JPEG/JBIG2 adapters | Rendering codecs | Apache-2.0 |
| PdfPig OpenJPEG adapter | Rendering codec | BSD-2-Clause |
| Avalonia packages | Native desktop UI and headless validation | MIT; preserve included assets' notices |
| ANGLE Windows native package | UI graphics dependency | Reviewed bundled BSD-3-Clause text |
| System.Security.Cryptography.Pkcs | Platform CMS containers | MIT |
| xUnit / test infrastructure | Build/test only | Apache-2.0 or MIT |

Some native components and font assets contain their own permissive notices. Package-level SPDX metadata does not replace these redistribution obligations. Preserve notices from original packages and exact native distributions; no claim is made that a metadata gate is a complete legal audit of every compiled native source component.

## Enforced checks

`python scripts/audit-licenses.py` examines each project's actual restored `project.assets.json`, including transitive packages, and the installed package manifests. It fails when a project has not been restored, package bytes are missing, a license is unknown/restricted, a direct restricted PDF dependency remains, or a reviewed manifest/license-file hash changes. A mixed expression containing a non-permissive alternative is not accepted automatically.

The two legacy metadata exceptions in `scripts/license-policy.json` are pinned to exact package versions and content hashes: xunit.abstractions has a historical license URL rather than SPDX metadata; ANGLE carries a license file. Unknown new cases require an explicit review, not broad warning suppression. Build and release workflows run the gate, its negative tests, and publish the resulting package provenance inventory.

There is no remaining package-scoped NU1701 suppression for a legacy PDF engine. Shipping applications should also carry the [third-party notices](https://github.com/wieslawsoltes/ProPDF/blob/main/THIRD-PARTY-NOTICES.md) and the underlying dependencies' notices.

## Primary sources

[SkiaSharp](https://github.com/mono/SkiaSharp), [PdfPig](https://github.com/UglyToad/PdfPig), [PdfPig.Rendering.Skia](https://github.com/BobLd/PdfPig.Rendering.Skia), [Avalonia](https://github.com/AvaloniaUI/Avalonia), [ANGLE license](https://chromium.googlesource.com/angle/angle/+/main/LICENSE), [xUnit abstractions license](https://github.com/xunit/abstractions.xunit/blob/main/license.txt), [.NET runtime license](https://github.com/dotnet/runtime/blob/main/LICENSE.TXT).

## Source-pinned compatibility code

The optional rendering assembly now compiles attributed Apache-2.0 PdfPig.Rendering.Skia source pinned to the prior package's exact commit, with narrow image-paint and compilation corrections. It is not owned interpreter code. Its NuGet license expression is `MIT AND Apache-2.0`; original notices and source provenance are packaged and copied to application output. The other ProPDF-authored libraries retain MIT. See [Appearance and images](content-appearance.md) and the root third-party notices for the source pin and distribution details.
