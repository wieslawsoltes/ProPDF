# Getting started

Install the .NET 10 SDK. Libraries target .NET 8; WPF targets net8.0-windows. WPF cross-compiles on all build hosts but executes only on Windows.

```sh
dotnet restore ProPDF.slnx
dotnet build ProPDF.slnx -c Release --no-restore
dotnet run --project samples/ProPDF.Avalonia.Sample -c Release
# Windows alternative:
dotnet run --project samples/ProPDF.Wpf.Sample -c Release
```

Samples generate a real three-page PDF without bundling confidential documents, proprietary fonts or private keys. Use main for merged features; an open feature PR is not part of main until merged.

## Composition

```csharp
using ProPDF.Core;
using ProPDF.Engine.PdfPig;
using ProPDF.Editing;
using ProPDF.Presentation;
using ProPDF.Rendering.Skia;

var backend = new PdfPigBackend();
var editor = new ManagedPdfEditor(backend); // Optional; review licensing.
var session = new PdfSession(backend, editor);
var renderer = new SkiaPdfRenderer(backend);
var viewport = new PdfViewportController(session, renderer, backend,
    action => Avalonia.Threading.Dispatcher.UIThread.Post(action));
var context = new PdfEditorContext(viewport, editor,
    token => editor.CreateAsync(cancellationToken: token), backend);
var control = new ProPDF.Avalonia.PdfEditor { Context = context };
```

For WPF, supply the WPF application's dispatcher and use ProPDF.Wpf.PdfEditor. For viewing only, omit the editor and construct `new PdfSession(backend)`, then host PdfView with its Controller property. Avalonia requires its Skia renderer (`UseSkia()`). Construct native controls on their UI thread.

One viewport represents one window's dimensions, zoom and selection. Several viewports may share a session and renderer; do not reuse a single viewport for differently sized windows. The built-in navigation service is provided by the optional editing adapter; a viewer-only application can supply its own implementation of the Core contract.

## Edit and save

```csharp
await using var source = File.OpenRead("input.pdf");
await session.OpenAsync(source);
await session.ApplyAsync(new AddText(1, new PdfPoint(40, 60), "Reviewed"),
    expectedRevision: session.Current!.Id);
await session.SaveAsAsync("reviewed.pdf");
```

Pages are one-based. Geometry uses top-left-origin, cropped/rotated PDF points, not pixels or DIPs. Capture the revision before asynchronous work; recompute rather than blindly retrying a stale edit. Loading leaves ownership of the stream with the caller.

## Desktop workflows

Choose a tool and drag on the page. The toolbar text box supplies inserted text, comments or a unique form-field name. Select text performs rectangular word selection; Select region defines crop/image placement. Region replacement removes all content in the rectangle rather than reflowing a paragraph.

Page tools rotate/insert/duplicate/delete/reorder. Review and Forms inspectors edit comments and form values. The Document inspector edits metadata and saves attachments without executing them.

The Output toolbar exports PNG/JPEG at a chosen DPI, streams selected-page text, extracts page ranges and compares documents. Comparison results navigate corresponding pages and can be saved as JSON. See [Output and comparison](output-comparison.md) for bounds and semantics.

The Navigate inspector resolves bookmarks and links, supports back/forward history, inserts child bookmarks, renames and deletes subtrees, and authors internal links from a selected region. External links require explicit confirmation. Link activation currently occurs in the inspector, not by clicking the PDF page. Full subtree movement is available through the SDK. See [Navigation](navigation.md).

Redaction marks are only staging. Apply confirms actual cleanup; saving/exporting with unapplied marks is blocked. Originals and undo history remain unredacted. See [Editing](editing.md). Encryption/signing APIs do not yet have complete password-policy and certificate-management shell workflows.

## Deployment and lifetime

Linux applications must include SkiaSharp.NativeAssets.Linux.NoDependencies and HarfBuzzSharp.NativeAssets.Linux runtime assets. Font availability affects fallback fidelity; redistribution rights remain the application's responsibility. The owned editing backend requires no legacy-PDF compatibility exception; all restored dependencies must pass the permissive-license gate.

Detach controls, dispose externally hosted workspaces, await viewport disposal, then await renderer disposal. Dispose scenes and tile leases. Controls do not dispose shared host-owned services. Samples prompt before discarding unsaved work; custom hosts need their own close policy.

Pack libraries into a local feed using `dotnet pack`. Documentation package names do not imply public NuGet publication. See [Build and release](build-release.md).
