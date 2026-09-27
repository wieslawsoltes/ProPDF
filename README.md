<div align="center">

# ProPDF

### One PDF engine. Three reusable UI adapters.

Owned .NET PDF kernel and editing libraries · SkiaSharp rendering · Avalonia · WPF · Uno Platform

[Getting started](docs/getting-started.md) · [Uno and browser](docs/uno-platform.md) · [Architecture](docs/index.md) · [Feature matrix](docs/features.md) · [Delivery](docs/build-release.md)

</div>

---

**Development preview — 0.1.0-alpha.4.** ProPDF implements native PDF editing and viewing with independently reusable libraries. It is not complete Adobe Acrobat parity, a conformance-certified PDF implementation or a certified redaction/signature-trust product. See the feature matrix for implemented APIs, UI workflows and remaining qualification.

## Reusable libraries

| Package | Responsibility | Target |
| --- | --- | --- |
| `ProPDF.Kernel` | Owned objects, parser, cross-references, streams, writer and PDF security | .NET 8 |
| `ProPDF.Core` | Contracts, immutable snapshots, transactions, history, atomic saving, geometry and layout | .NET 8 |
| `ProPDF.Rendering.Skia` | Display lists, progressive tiles, caches, image leases and raster export | .NET 8 |
| `ProPDF.Engine.PdfPig` | Optional permissive parsing/extraction and attributed PDF-to-Skia interpreter | .NET 8 |
| `ProPDF.Editing` | Owned content/page editing, forms, annotations, fonts, navigation and signatures | .NET 8 |
| `ProPDF.Presentation` | Shared viewport, interactions, selection, commands and inspector state | .NET 8 |
| `ProPDF.Avalonia` | Viewer, thumbnails and editor shell | .NET 8 / Avalonia |
| `ProPDF.Wpf` | Equivalent native Windows controls | .NET 8 Windows |
| `ProPDF.Uno` | Uno Skia viewer, thumbnails and shared-workspace editor | .NET 10 / Uno |

The kernel has no third-party package references. UI adapters do not reference a concrete editing vendor. Applications compose the owned editor or supply their own implementation of the shared contracts. There is no iText, pdfSweep or commercial PDF SDK in this stack.

## Run and build

Use the .NET 10 SDK selected by `global.json`. Avalonia runs on Windows, Linux and macOS; WPF runs on Windows. The Uno browser sample uses **Uno.Sdk 6.7.30 / Uno 6.7.135**, with a separate native macOS sample head.

```sh
git clone https://github.com/wieslawsoltes/ProPDF.git
cd ProPDF
dotnet workload install wasm-tools
dotnet restore ProPDF.slnx -p:Configuration=Release
python scripts/fetch-uno-notices.py
dotnet build ProPDF.slnx -c Release --no-restore

dotnet run --project samples/ProPDF.Avalonia.Sample -c Release --no-build
# Windows:
dotnet run --project samples/ProPDF.Wpf.Sample -c Release --no-build
# Native Uno on macOS:
dotnet run --project samples/ProPDF.Uno.Sample -c Release -f net10.0-desktop --no-build
# Uno browser:
dotnet publish samples/ProPDF.Uno.Sample -c Release -f net10.0-browserwasm
```

During PR review, use `feature/uno-platform` for the unmerged Uno work. Source on a branch is not evidence of successful deployment or publication.

**Native Uno Windows/Linux heads are not included in this sample.** Their stock hosts introduce non-permissive SDK metadata or video dependencies. The browser app is the Uno path on those operating systems; existing native WPF/Avalonia editors remain available. Android/iOS and native WinAppSDK are not qualified by this sample. See [Uno platform boundaries](docs/uno-platform.md).

Samples generate a real three-page PDF. No confidential PDFs, private keys or commercial license files are bundled. Browser imports and downloads remain local to the tab; there is no PDF upload or server-side editing.

## Native editing workflows

The editors share opening/saving, undo/redo, search, anchored zoom, continuous/facing/single-page layout, thumbnails and page organization. Inspectors cover comments, forms, bookmarks/links, metadata, attachments, output and comparison. Existing content can be selected individually or as a same-page multi-selection, transformed, aligned, distributed, duplicated or deleted in one atomic revision.

Appearance edits preserve text fonts, positions and surrounding graphics state while changing colors, local line style, opacity or blend modes. Image replacement affects one selected invocation rather than every use of a shared resource. Wrapped text insertion/replacement is supported within explicit bounds, but is not general rich paragraph reflow. See [content editing](docs/content-editing.md) and [appearance/image editing](docs/content-appearance.md).

## Integrate

```csharp
using ProPDF.Core;
using ProPDF.Editing;
using ProPDF.Engine.PdfPig;
using ProPDF.Presentation;
using ProPDF.Rendering.Skia;

var backend = new PdfPigBackend();
var editor = new ManagedPdfEditor(backend);
var session = new PdfSession(backend, editor);
var renderer = new SkiaPdfRenderer(backend);
var viewport = new PdfViewportController(session, renderer, backend);
var context = new PdfEditorContext(viewport, editor,
    token => editor.CreateAsync(cancellationToken: token), backend);

// Choose a platform's PdfEditor { Context = context } or PdfView { Controller = viewport }.
// ProPDF.Avalonia / ProPDF.Wpf / ProPDF.Uno
await using var input = File.OpenRead("input.pdf");
await session.OpenAsync(input);
await session.ApplyAsync(new AddText(1, new PdfPoint(40, 60), "Reviewed"),
    expectedRevision: session.Current!.Id);
await session.SaveAsAsync("reviewed.pdf");
```

Construct native UI state on its dispatcher. A viewer-only host can omit the editor. Each window owns a viewport; sessions and renderers may be shared. Detach controls, dispose workspace subscriptions, await viewport disposal and then renderer disposal. Dispose acquired scenes/tile leases. Browser/native picker integrations use an explicit destination handoff rather than assuming a temporary path is the user's saved file.

## Rendering and data integrity

Metadata-based layout and binary-search virtualization select visible 512-pixel tiles. Progressive publication, cancellation and revision/generation checks reject stale results. Sampling gutters reduce fractional-scale tile seams. Reference-counted images remain valid while deferred scenes own them. Cache entry/byte budgets and input/output limits are explicit, but do not constitute total native memory accounting or a hostile-input sandbox.

**PDF tile rasterization is CPU Skia.** Framework compositors may use the GPU to present the resulting images. No measured speed advantage over Acrobat or fully GPU PDF interpretation is claimed.

Edits write private output and reopen it before publication; failed transactions preserve the prior revision. Atomic filesystem saving flushes a same-directory temporary file before replacement. Save-and-publish handoffs mark a document clean only after the host callback succeeds. Browser download acceptance is not proof of a durable disk write.

## Validation and packages

```sh
dotnet test tests/ProPDF.Tests -c Release --no-build
dotnet run --project tests/ProPDF.Avalonia.Smoke -c Release --no-build
# Windows:
dotnet run --project tests/ProPDF.Wpf.Smoke -c Release --no-build
python scripts/fetch-uno-notices.py
dotnet pack ProPDF.slnx -c Release --no-build -o artifacts/packages
pwsh scripts/verify-packages.ps1 -PackageDirectory artifacts/packages
python -m pip install -r docs/requirements.txt
python -m mkdocs build --strict
```

CI validates native controls, independent PDF output, transaction failures, rendering, packages and clean external consumers. The Uno workflow publishes real WebAssembly, tests browser editing/download/reopen behavior and stages one application-plus-docs site. Runtime notices are retained in packages, consumer output and the site. Check the exact commit's Actions results; workflow source alone is not evidence of success. Public NuGet publication requires reviewed tags, credentials and environment approval.

## Safety, licensing and remaining work

Owned redaction removes content groups rather than painting a black overlay; conservative removal can affect more than a selected region. Unsupported tagged/marked/soft-mask/other unqualified cases fail atomically. Original files and history retain old content. Clipping, opacity and image replacement are not sanitization. Signature integrity is separate from certificate trust, revocation and PAdES-LTV validation. PDF JavaScript, launch actions and embedded executables are not run.

ProPDF-authored source is **MIT**. The optional interpreter contains attributed Apache-2.0 PdfPig/Skia source; Skia/Uno/runtime dependencies retain their own permissive terms and notices. Explicit runtime selection excludes the restricted stock host dependencies. Exact reviewed legacy manifests and full legal texts are hash-checked. This is not a legal certification of every native component. See [third-party notices](THIRD-PARTY-NOTICES.md).

General rich-content reflow, nested editing, advanced typography, full browser crypto, OCR, conversion, production printing, comprehensive accessibility/conformance, prepress and enterprise collaboration remain work. [The feature matrix](docs/features.md) is the compatibility reference, not a claim of full Acrobat equivalence.
