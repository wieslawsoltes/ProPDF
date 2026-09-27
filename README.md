# ProPDF

**One document engine. Two native desktop experiences.**

ProPDF is a modular .NET PDF toolkit for building an Avalonia or WPF viewer and editor around shared SkiaSharp rendering. The project is in **0.1.0-alpha.1 development**; it is not a claim of complete Adobe Acrobat compatibility.

## Design

| Package | Responsibility |
| --- | --- |
| `ProPDF.Core` | Immutable snapshots, transactions, revision checks, bounded undo/redo, atomic saves, geometry and virtualized page layout |
| `ProPDF.Rendering.Skia` | Clipped tile rendering, bounded image and display-list caches, reference-counted native resources |
| `ProPDF.Engine.PdfPig` | PDF loading, lightweight page geometry, text extraction/search, Skia display-list creation |

No UI framework is referenced by these libraries. Rendering is scheduled away from the UI thread, and a PDF document is never accessed concurrently by the rendering worker. Only requested page tiles are rasterized.

## Build and test

Install the .NET 10 SDK (the libraries target .NET 8):

```sh
dotnet restore ProPDF.slnx
dotnet build ProPDF.slnx -c Release --no-restore
dotnet test tests/ProPDF.Tests -c Release --no-build
dotnet pack ProPDF.slnx -c Release --no-build -o artifacts/packages
```

GitHub Actions validates Linux, Windows and macOS and uploads test results and NuGet artifacts. Artifacts are not a published NuGet release.

## Safety and lifecycle

Document snapshots own immutable bytes. An edit becomes visible only after the edited PDF has been reopened successfully. Failed and cancelled edits leave the current snapshot unchanged. Save uses a same-directory temporary file, flushes it, then replaces the destination; a cancelled write never truncates the original file. Undo history has both byte and entry limits.

PDF input is untrusted. Input size, page count, tile size, search result count and regex execution are bounded. In-process parsing is **not a sandbox**; applications handling hostile files should run it in a restricted worker process. PDF JavaScript, launch actions and network fetches are not executed by ProPDF.

Native image leases must be disposed. Evicting a cache entry never invalidates a lease that is still being displayed. Renderer disposal waits for the active worker; outstanding image leases remain valid until released.

## Engine selection and licensing

PdfPig and PdfPig.Rendering.Skia provide Apache-2.0-licensed parsing/rendering integration; SkiaSharp is MIT licensed. A separate iText editing adapter is planned in the next feature PR to supply document manipulation, forms and real content redaction. That adapter and the assembled editor applications require **AGPL compliance or an appropriate commercial iText license**. It will not be a transitive dependency of the reusable UI controls.

Upstream references: [PdfPig](https://github.com/UglyToad/PdfPig), [PdfPig.Rendering.Skia](https://github.com/BobLd/PdfPig.Rendering.Skia), [SkiaSharp](https://github.com/mono/SkiaSharp), [iText licensing](https://itextpdf.com/how-buy/AGPLv3-license).

## Compatibility

PDF rendering depends on the upstream interpreter and font availability. Passing the test suite is not ISO 32000, PDF/A, PDF/UA or Acrobat certification. Dedicated interoperability fixtures, native UI checks and performance qualification are required before a production release.

## Contributing

Use focused feature branches and include regression tests. Keep framework-specific code in adapters, preserve cancellation and resource ownership, and never advertise an unsupported capability. Do not commit confidential PDFs, private keys, licenses or font files.

ProPDF-authored foundation code is licensed under MIT; third-party and optional adapter licensing must also be respected.
