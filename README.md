# ProPDF

### One PDF engine. Two native desktop experiences.

Modular .NET PDF viewing and editing with **SkiaSharp**, **Avalonia** and **WPF**.

[Getting started](docs/getting-started.md) · [Architecture](docs/index.md) · [Feature matrix](docs/features.md) · [Native editing](docs/editing.md) · [Build and release](docs/build-release.md) · [Licensing](docs/licensing.md)

**Development alpha: 0.1.0-alpha.1.** This is not yet full Adobe Acrobat parity or a certified redaction/signature-trust product. The feature matrix separates executable SDK functionality, native desktop workflows and remaining work.

## Seven reusable packages

| Package | Responsibility |
| --- | --- |
| `ProPDF.Core` | Immutable snapshots, transactions, revisions, bounded undo, atomic saving, geometry and layout |
| `ProPDF.Rendering.Skia` | Background tile rendering, display-list caches and reference-counted native images |
| `ProPDF.Engine.PdfPig` | Parsing, text extraction/search and PDF-to-Skia interpretation |
| `ProPDF.Editing.iText` | Optional native editing, forms, annotations, redaction, encryption and signing |
| `ProPDF.Presentation` | Shared viewport, interactions, workspace commands and inspector state |
| `ProPDF.Avalonia` | Native viewer, thumbnail and editor controls |
| `ProPDF.Wpf` | Equivalent Windows WPF controls |

UI packages have no iText dependency. Applications opt into an editing backend; a viewer-only host uses `new PdfSession(backend)`.

## Build and run

Install the .NET 10 SDK. Libraries/apps target .NET 8; WPF executes only on Windows but cross-builds on all CI hosts.

```sh
git clone https://github.com/wieslawsoltes/ProPDF.git
cd ProPDF
dotnet restore ProPDF.slnx
dotnet build ProPDF.slnx -c Release --no-restore
dotnet run --project samples/ProPDF.Avalonia.Sample -c Release --no-build
# Windows alternative:
dotnet run --project samples/ProPDF.Wpf.Sample -c Release --no-build
```

Samples generate a real PDF with text, vectors, comments, a form and bookmarks. No confidential documents, private keys or font files are bundled.

## Integrate

```csharp
using ProPDF.Core;
using ProPDF.Engine.PdfPig;
using ProPDF.Editing.iText;
using ProPDF.Presentation;
using ProPDF.Rendering.Skia;

var backend = new PdfPigBackend();
var editor = new ITextPdfEditor(backend); // Optional; review licensing.
var session = new PdfSession(backend, editor);
var renderer = new SkiaPdfRenderer(backend);
var viewport = new PdfViewportController(session, renderer, backend);
var context = new PdfEditorContext(viewport, editor,
    token => editor.CreateAsync(cancellationToken: token), backend);
// new ProPDF.Avalonia.PdfEditor { Context = context };
// new ProPDF.Wpf.PdfEditor { Context = context };

await using var input = File.OpenRead("input.pdf");
await session.OpenAsync(input);
await session.ApplyAsync(new AddText(1, new PdfPoint(40, 60), "Reviewed"), session.Current!.Id);
await session.SaveAsAsync("reviewed.pdf");
```

Create controls on the UI thread and supply a dispatcher when no UI synchronization context exists. Each window needs a separate viewport. Detach views before awaiting viewport and renderer disposal. Dispose all acquired scenes and tile leases.

## Rendering and editing

Virtualized layout requests visible 512-pixel tiles with progressive publication, cached display lists and stale-generation rejection. Rasterization is CPU Skia; Avalonia may GPU-compose tiles and WPF uses a reusable premultiplied bitmap. No unmeasured speedup over Acrobat is claimed.

Native changes include page organization, content insertion, regional replacement, annotations, AcroForms, metadata, attachments and bookmarks. Edits are independently reopened before publishing an immutable revision. Atomic saves flush a temporary file before replacement. Region replacement is not paragraph reflow.

Redaction removes page content rather than painting over it, but is not certified full-file sanitization. Tagged PDFs and intersecting unflattened widgets are blocked; original files and undo history remain unredacted. Cryptographic signature integrity does not prove certificate trust, revocation or legal identity. Hostile PDFs require process isolation; in-process parsing is not a sandbox.

## Validation and delivery

```sh
dotnet test tests/ProPDF.Tests -c Release --no-build
dotnet run --project tests/ProPDF.Avalonia.Smoke -c Release --no-build
# Windows only:
dotnet run --project tests/ProPDF.Wpf.Smoke -c Release --no-build
dotnet pack ProPDF.slnx -c Release --no-build -o artifacts/packages
pwsh scripts/verify-packages.ps1 -PackageDirectory artifacts/packages
python -m pip install -r docs/requirements.txt
python -m mkdocs build --strict
```

CI validates Linux, Windows and macOS, independently reopened edits, native UI screenshots and clean external NuGet consumers. Release workflows reuse those checks, require exact tags reachable from main, and produce source, package and sample archives with checksums. Configure `NUGET_API_KEY` in a protected `nuget-release` environment for publication; configure Pages to use GitHub Actions. Workflow presence does not mean a public release or deployment has occurred.

## Licensing

ProPDF-authored source is MIT. PdfPig and its Skia integration are Apache-2.0; SkiaSharp is MIT. **iText and pdfSweep require AGPL compliance or appropriate commercial licenses.** The adapter's MIT license does not override these dependencies. pdfSweep's package-scoped framework-compatibility exception is documented separately.
