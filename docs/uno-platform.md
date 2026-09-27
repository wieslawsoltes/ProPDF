# Uno Platform

ProPDF.Uno supplies a reusable `PdfView`, lazy `PdfThumbnail`, `PdfEditor` and inspector controls for the Uno Skia renderer. It references the same ProPDF.Presentation/Core/Rendering libraries as the Avalonia and WPF adapters. The sample composes the existing owned MIT kernel/editor with the attributed permissive PdfPig interpreter, not another commercial PDF SDK.

## Versions and targets

Pinned to **Uno.Sdk 6.7.30** (Uno 6.7, released September 24, 2026), .NET 10, and the existing SkiaSharp 3.119.4 ABI. The control package targets net10.0; the sample targets net10.0-browserwasm and net10.0-desktop. It uses Uno's `SKCanvasElement` to compose retained shared PDF scenes directly. Android/iOS and native WinAppSDK are not separately validated by this sample. Native WinAppSDK is not a Skia host for this control.

Primary release and control documentation: [Uno 6.7](https://platform.uno/blog/uno-platform-6-7/), [SKCanvasElement](https://platform.uno/docs/articles/controls/SKCanvasElement.html).

## Run and integrate

```sh
dotnet workload install wasm-tools
dotnet run --project samples/ProPDF.Uno.Sample -f net10.0-desktop
dotnet publish samples/ProPDF.Uno.Sample -c Release -f net10.0-browserwasm
```

The browser app is staged at `/ProPDF/`, with this documentation at `/ProPDF/docs/`. `Uno browser and Pages` owns the complete Pages deployment, preventing the documentation workflow from overwriting the application. Publication still requires GitHub Pages repository enablement/permissions and a successful deployment; source configuration is not evidence that a URL is live.

```csharp
// UI thread; the host owns its session, renderer and viewport.
var control = new ProPDF.Uno.PdfEditor { Context = context };
// Read-only viewing: new ProPDF.Uno.PdfView { Controller = viewport };
// A custom host can supply Files = its IPdfUnoFiles implementation.
```

Configure the consuming Uno application with `Skia;SkiaRenderer`. Dispose/detach controls before disposing the shared viewport/renderer. Every window requires its own viewport. Core does not depend on Uno; the Uno library does not depend on the concrete editing backend.

## Editor workflows

The native Uno shell binds to the existing shared commands: opening/new/save, undo/redo, layouts, page navigation, zoom and search; thumbnails and page organization; text/region/annotation tools; existing-content single/multiple selection and transformations, alignment/distribution, appearance and image replacement; comment/form inspectors; hierarchical bookmarks/links; metadata/attachments; raster/text/page-range export and comparison. There are no fake success buttons or browser-side replacement PDF engine.

The inspector has Edit, Review, Forms, Navigate, Export/compare and Document sections. Horizontal toolbar scrolling and optional sidebars allow narrow windows; pointer, touch, wheel, keyboard and standard Uno controls share the same viewport logic. Basic framework accessibility is not a full PDF text accessibility provider.

## Files and browser boundaries

`IPdfUnoFiles` grants file access explicitly through pickers or host callbacks. The native provider stages authorized StorageFiles; the browser provider imports files locally and hands exports to the browser download manager. There is no upload, server processing, analytics or automatic persistent storage. Reloading closes the in-memory workspace; download work before reloading. A before-unload prompt covers unsaved edits and unapplied redaction marks, subject to browser policy.

Browser imports are limited to 32 MiB per file / 128 MiB per workspace, downloads to 64 MiB, and pending export registrations to 64. Native staging also has size/entry limits. These limits do not constitute hostile-input process isolation or an OS memory cap.

`IPdfWorkspaceFileTransfer` is the shared destination handoff hook. `PdfSession.SaveAndPublishAsync` marks a document clean only after the host callback succeeds. A thrown/cancelled handoff retains dirty state and emits no Saved event. Browser success means bytes were submitted to the download manager, **not** proof that the user retained the file on disk. Save prompts/handoffs are repeated rather than attempting to overwrite a temporary path silently.

The .NET browser runtime does not support every desktop cryptographic provider. Password-protected PDFs and certificate/CMS signing are not claimed as supported browser workflows without dedicated qualification; plain PDF reading/editing/export uses the same shared backend. No native signing keys or certificate stores are exposed to the page. The desktop SDK services keep their existing behavior.

## Validation

The portable regression suite covers failed/cancelled publishing and export callbacks. The browser pipeline builds the desktop sample, publishes real Uno WebAssembly, audits restored package licenses and source provenance, then runs Chromium against the project-site base path. Browser checks use the actual bound controls and independently re-read PDF state; they are not static HTML mocks. The opt-in `?test=1` bridge exposes test actions only in the sample, not the reusable control library.

Inherited [feature limitations](features.md), [redaction boundaries](editing.md), font/codec/color restrictions and in-process resource risks remain. Uno support is not a claim of complete Adobe Acrobat parity.
