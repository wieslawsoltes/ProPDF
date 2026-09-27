<div align="center">

# ProPDF
### One PDF engine. Two native desktop experiences.

**Owned PDF kernel and editing · SkiaSharp rendering · Avalonia and WPF · Permissive dependencies**

[Get started](docs/getting-started.md) · [Owned engine](docs/owned-engine.md) · [Feature matrix](docs/features.md) · [Licensing](docs/licensing.md) · [Build and release](docs/build-release.md)

</div>

---

**Development alpha — 0.1.0-alpha.3.** ProPDF is a modular .NET toolkit and native desktop editor. It implements real PDF operations, not just editable overlays. It is not yet a complete Acrobat replacement, a certified redaction product, or a complete signature-trust validator. The [feature matrix](docs/features.md) separates working SDK APIs, desktop workflows and remaining work.

## Own the document engine

The PDF object model, tokenizer, cross-reference reader, stream filters, graph writer, security handler and native editing implementation are ProPDF-authored MIT code. **iText, pdfSweep and their adapters have been removed.** No commercial PDF license or copyleft PDF engine is needed by the library or sample applications.

SkiaSharp supplies drawing and image codecs. The optional PdfPig adapter supplies an independent parser and text extraction, plus source-pinned Apache-2.0 PDF-to-Skia interpretation with attributed image-paint corrections. Its mixed-source package is `MIT AND Apache-2.0`; this third-party interpreter is not represented as ProPDF-owned code. Framework controls remain independent of both concrete editor and loader choices. Restored NuGet dependencies are checked by a fail-closed permissive-license CI gate; reviewed legacy metadata exceptions are version/hash pinned.

## Eight reusable NuGet libraries

| Package | Responsibility |
| --- | --- |
| `ProPDF.Kernel` | Owned PDF objects, byte syntax, xref/object streams, bounded filters, serialization, incremental revisions and Standard security. No third-party package references. |
| `ProPDF.Core` | Immutable snapshots, transactions, undo/redo, atomic saving, geometry, page layout and service contracts. |
| `ProPDF.Rendering.Skia` | Background display lists, visible-page tiles, bounded caches, native image leases and raster export. |
| `ProPDF.Engine.PdfPig` | Optional independent loading, text extraction/search and PDF-to-Skia interpretation. |
| `ProPDF.Editing` | Owned page/content edits, TrueType embedding, annotations, AcroForms, redaction, attachments, navigation, encryption and signing. |
| `ProPDF.Presentation` | Shared viewport, scenes, editing tools, workspace commands, navigation/history, export and comparison. |
| `ProPDF.Avalonia` | Native `PdfView`, lazy `PdfThumbnail` and `PdfEditor` shell. |
| `ProPDF.Wpf` | Equivalent native WPF controls. |

Libraries target .NET 8; WPF targets .NET 8 Windows. Use the .NET 10 SDK selected by `global.json`. A package name here identifies a packable project, not an assertion that a public NuGet version has already been published.

## A shared native workspace

Both editors use the same document transactions, commands and interaction model: continuous/facing/single-page views; anchored zoom and panning; virtualized page thumbnails; search and rectangular text selection; undo/redo; comments, forms and metadata inspectors; page insertion, duplication, rotation, cropping, merging and extraction; text/image/vector insertion; region replacement; staged redaction; hierarchical bookmarks and safe links; raster/text export and page-aligned comparison.

Native adapters own drawing integration, input, bindings and platform dialogs. Capabilities govern available commands. SDK signing and encryption do not yet have full certificate/password-policy editor workflows.

```sh
git clone https://github.com/wieslawsoltes/ProPDF.git
cd ProPDF
dotnet restore ProPDF.slnx
dotnet build ProPDF.slnx -c Release --no-restore

# Linux, Windows and macOS
dotnet run --project samples/ProPDF.Avalonia.Sample -c Release

# Windows
dotnet run --project samples/ProPDF.Wpf.Sample -c Release
```

The samples generate their own PDF with real text, vectors, a form, annotations and bookmarks. Private documents, proprietary fonts and signing credentials are not bundled.

## Integrate the owned backend

```csharp
using ProPDF.Core;
using ProPDF.Editing;
using ProPDF.Engine.PdfPig;
using ProPDF.Presentation;
using ProPDF.Rendering.Skia;

var backend = new PdfPigBackend();
var editor = new ManagedPdfEditor(backend); // Independent reopen validation.
var session = new PdfSession(backend, editor);
var renderer = new SkiaPdfRenderer(backend);
var viewport = new PdfViewportController(session, renderer, backend);
var context = new PdfEditorContext(viewport, editor,
    token => editor.CreateAsync(cancellationToken: token), backend);

// Choose one native host:
// new ProPDF.Avalonia.PdfEditor { Context = context };
// new ProPDF.Wpf.PdfEditor { Context = context };

await using var input = File.OpenRead("input.pdf");
await session.OpenAsync(input);
await session.ApplyAsync(new AddText(1, new PdfPoint(40, 60), "Reviewed"),
    expectedRevision: session.Current!.Id);
await session.SaveAsAsync("reviewed.pdf");

// At application shutdown, detach the control before disposing its services.
await viewport.DisposeAsync();
await renderer.DisposeAsync();
```

`new ManagedPdfEditor()` and `new ManagedPdfLoader()` work without a third-party PDF parser. The PdfPig adapter remains useful for independent validation and rendering. A viewer-only host omits the editor. Supply the native UI dispatcher when necessary, and use separate viewport controllers for separate windows.

## Correctness and resource ownership

Edits write private output, close it and reopen it before publishing an immutable revision. A failed transaction leaves the current snapshot unchanged. Revision checks prevent delayed operations from applying stale geometry. Saving flushes a same-directory temporary file before replacement.

The kernel locates objects through cross-references, not a regex scan of arbitrary PDF bytes. It supports classic, stream and hybrid cross-references, compressed objects and previous revisions. Decoding, objects, nesting, input and output have explicit limits. Unknown dictionaries and encoded streams can be preserved without claiming their semantics are implemented.

Rendering requests visible 512-pixel tiles, publishes progressively and rejects stale document/zoom generations. Leased images survive eviction while a UI scene uses them. Defaults include 128 MiB of raster-cache memory and bounded display-list/parser counts. **Tile rasterization is CPU Skia**; Avalonia may GPU-compose the tiles and WPF uses a reusable premultiplied bitmap. No unmeasured speedup over Acrobat is claimed.

## Security-sensitive boundaries

The owned redactor removes whole intersecting supported text objects, painted paths, and image/form invocations, then paints the requested fill. It does not merely cover content. Conservative removal can affect content outside the rectangle. Inline images, tagged/ActualText content, soft masks, patterns and other unqualified cases fail explicitly. Marks must be applied before saving. Originals, backups, undo history, metadata and attachments are not sanitized automatically.

Standard password security supports R2–R6 reading; new encryption uses AES-256 R6 with an explicit PDF 1.7 extension-level-8 declaration. Currently passwords are printable ASCII; full Unicode password preparation remains a compatibility gap. Rewriting protected files requires owner-authorized access. Ordinary edits of signed PDFs are rejected. Signing appends a revision, uses host-owned .NET RSA/ECDSA keys and produces detached CMS/CAdES data; integrity checks do not establish certificate trust, revocation, timestamps or legal validity.

Parsing is in-process, not a hostile-input sandbox. PDF JavaScript, launch actions and embedded attachments are not executed. See [Owned engine](docs/owned-engine.md) and [Native editing](docs/editing.md) before handling sensitive documents.

## Build, test and ship

```sh
python scripts/audit-licenses.py
python -m unittest discover -s scripts/tests -v
dotnet test tests/ProPDF.Tests -c Release --no-build
dotnet run --project tests/ProPDF.Avalonia.Smoke -c Release --no-build
# Windows only:
dotnet run --project tests/ProPDF.Wpf.Smoke -c Release --no-build
dotnet pack ProPDF.slnx -c Release --no-build -o artifacts/packages
pwsh scripts/verify-packages.ps1 -PackageDirectory artifacts/packages
python -m pip install -r docs/requirements.txt
python -m mkdocs build --strict
```

CI validates Windows, Linux and macOS, native UI smoke rendering and clean external NuGet consumers. Regression inputs include independently produced encryption files, hand-built compressed-object/xref fixtures, generated in-memory TrueType programs, decoded-stream/pixel redaction checks and signature tampering. Screenshots, test results and license inventories are artifacts, not certification.

Publication requires configured GitHub Pages, NuGet ownership/credentials and a protected release environment. Releases revalidate immutable reviewed tags and include source, package/symbol files, sample archives and checksums. See [Deployment prerequisites](docs/deployment-setup.md).

ProPDF source is MIT. Preserve the licenses and native notices of the permissive dependencies when distributing applications. [Third-party notices](THIRD-PARTY-NOTICES.md) · [Migration guide](docs/dependency-compatibility.md) · [Changelog](CHANGELOG.md)

## Existing page content

The owned editor now inspects text groups, painted paths, image invocations and form invocations. Both native editors provide an **Edit** inspector with selection, dragging, corner resizing, numeric transforms, duplication, deletion, visual clipping and wrapped text replacement. Persistent graphics/text state is preserved so subsequent objects are not accidentally restyled. Revision/fingerprint handles reject stale edits; text overflow rejects the transaction instead of silently dropping lines.

Tile rendering uses device-space origin alignment and a small gutter. Display-list caching now has an approximate byte limit in addition to its entry limit. These changes do not imply full GPU PDF interpretation, pixel-identical vector antialiasing or an unmeasured speedup. See [Existing content and text boxes](docs/content-editing.md) for supported units, bounds, font/layout limits and the distinction between visual clipping and actual redaction.

## Appearance and image editing

The shared Edit inspector now changes native text/path colors, local stroke width/cap/join/dashes, path painting, opacity and blend mode. Text keeps its original fonts and positioning; following objects retain their prior graphics state. Image replacement changes one invocation without overwriting images shared by other pages. Static image authoring handles sRGB/straight-alpha soft masks and all eight EXIF orientations. Image interpolation is editable and honored by the corrected rendering adapter.

Revisions, input validation, pixel budgets and undo apply to these operations. Zero opacity and clipping are not redaction. See [Appearance and images](docs/content-appearance.md) for exact alpha semantics, resource isolation, renderer-source provenance and remaining graphics/color-management boundaries.
