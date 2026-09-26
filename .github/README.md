<div align="center">

# ProPDF

### One PDF engine. Two native desktop experiences.

Modular .NET PDF viewing and editing · SkiaSharp rendering · Avalonia and WPF

[Start building](../docs/getting-started.md) · [Architecture](../docs/index.md) · [Feature matrix](../docs/features.md) · [Licensing](../docs/licensing.md) · [Delivery](../docs/build-release.md)

</div>

---

**Development alpha — 0.1.0-alpha.1.** ProPDF implements substantial native PDF workflows, but is not a complete Adobe Acrobat replacement, an ISO-conformance implementation or a certified redaction/signature-trust product. Build artifacts are not automatically public NuGet releases.

## A workspace, not just a page image

Avalonia and WPF share document state, transactions, commands, search, zoom, selection, editing tools, undo/redo and inspector models. Thin native adapters handle rendering integration, input, bindings and platform dialogs.

The built-in editors include virtualized page strips, continuous/facing/single layouts, anchored zoom, comments, forms, metadata and attachment inspectors, page organization, content insertion, image placement, region replacement and staged redaction with explicit confirmation. Backend capabilities govern available commands.

## Seven reusable libraries

| Package | Responsibility |
| --- | --- |
| `ProPDF.Core` | Immutable snapshots, transactions, history, atomic save, geometry/layout and service contracts |
| `ProPDF.Rendering.Skia` | Background display-list/tile rendering, bounded caches and native image leases |
| `ProPDF.Engine.PdfPig` | Loading, page metadata, extraction/search and PDF-to-Skia interpretation |
| `ProPDF.Editing.iText` | Optional native editing, forms, annotations, cleanup, encryption and signing |
| `ProPDF.Presentation` | Shared viewport, scenes, interactions, commands and inspector state |
| `ProPDF.Avalonia` | Native viewer, lazy thumbnails and editor shell |
| `ProPDF.Wpf` | Equivalent Windows WPF controls |

**UI packages do not depend on iText.** Choose a viewer-only composition or another `IPdfEditor` without replacing the controls. Sample editors explicitly opt into iText/pdfSweep and their licensing obligations.

## Run

Install the .NET 10 SDK. Libraries target .NET 8; WPF executes on Windows. The complete solution cross-builds on Linux/macOS as well.

```sh
git clone https://github.com/wieslawsoltes/ProPDF.git
cd ProPDF
dotnet restore ProPDF.slnx
dotnet build ProPDF.slnx -c Release --no-restore

dotnet run --project samples/ProPDF.Avalonia.Sample -c Release
# Windows alternative:
dotnet run --project samples/ProPDF.Wpf.Sample -c Release
```

During review, use the latest stacked feature branch; main gains the implementation only after PRs are merged in dependency order. Samples generate a real three-page PDF without private documents, proprietary font files, API keys or signing certificates.

## Embed

```csharp
var backend = new PdfPigBackend();
var editor = new ITextPdfEditor(backend); // Optional: review licensing.
var session = new PdfSession(backend, editor);
var renderer = new SkiaPdfRenderer(backend);
var viewport = new PdfViewportController(session, renderer, backend);
var context = new PdfEditorContext(viewport, editor,
    token => editor.CreateAsync(cancellationToken: token), backend);

// Choose the platform adapter:
// new ProPDF.Avalonia.PdfEditor { Context = context };
// new ProPDF.Wpf.PdfEditor { Context = context };
// Or host PdfView with Controller = viewport.

await using var input = File.OpenRead("input.pdf");
await session.OpenAsync(input);
await session.ApplyAsync(new AddText(1, new PdfPoint(40, 60), "Reviewed"),
    expectedRevision: session.Current!.Id);
await session.SaveAsAsync("reviewed.pdf");
```

A viewer-only host constructs `new PdfSession(backend)`. Construct controls on their UI thread and supply a dispatcher when necessary. Use one viewport per window. At shutdown detach controls, dispose workspace subscriptions, await viewport disposal and then renderer disposal. See the [integration guide](../docs/getting-started.md) for namespaces, native assets and ownership.

## Rendering and correctness

Metadata-only layout and binary-search virtualization select visible 512-pixel tiles. Progressive publication and generation checks prevent stale document/zoom results replacing newer ones. Reference-counted images remain valid while a deferred UI scene owns them.

Defaults bound the raster cache to 128 MiB, display lists to 24 entries, parsers to two and undo history to 32 snapshots/256 MiB. Display-list/parser/decoded-object memory is not fully byte-accounted. **Rasterization is CPU Skia**; Avalonia may GPU-compose tiles, while WPF uses a reusable premultiplied bitmap. No unmeasured speedup over Acrobat is claimed.

Edits operate on private output and independently reopen it before publishing. Failed edits preserve the current revision. Atomic saves flush a same-directory temporary file before replacement.

## Safety and limitations

Redaction invokes actual pdfSweep content removal, not a painted rectangle. Unapplied marks block saving. Tagged PDFs and intersecting unflattened widgets are rejected. Page cleanup does not sanitize metadata, attachments, other pages, backups or undo history.

Signed-document rewrites are blocked. Detached signing is a separate append-mode service; cryptographic integrity does not establish certificate trust, revocation or PAdES-LTV. Parsing remains in-process, not an OS sandbox. ProPDF does not execute PDF JavaScript, launch actions or attachments.

General existing-content editing/reflow, complex typography, OCR, conversion, printing, advanced forms/signature UI, full accessibility, PDF/A/PDF/UA, prepress and enterprise collaboration remain substantial work. The [feature matrix](../docs/features.md) separates SDK APIs from shell features.

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

Validation includes independently parsed output, redaction stream/pixel checks, encryption/signing, cancellation/resource lifetime, native headless UI rendering, NuGet packaging and clean external consumers. Workflows upload test/screenshot artifacts. Release publication requires a reviewed immutable tag, successful validation, protected environment approval and a configured NuGet secret.

## Licensing

ProPDF source is MIT. PdfPig and its Skia integration are Apache-2.0; SkiaSharp is MIT. **iText and pdfSweep require AGPL compliance or appropriate commercial licenses.** The adapter's MIT license does not override those dependencies. Samples combine them and must be distributed accordingly.

pdfSweep currently restores a .NET Framework assembly through a package-scoped NU1701 exception. Used paths are tested, not presumed universally compatible. See [licensing](../docs/licensing.md) and [dependency compatibility](../docs/dependency-compatibility.md).
