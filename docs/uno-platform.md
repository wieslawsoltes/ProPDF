# Uno Platform

ProPDF.Uno supplies reusable `PdfView`, lazy `PdfThumbnail`, `PdfEditor` and inspector controls for Uno's Skia renderer. It shares ProPDF.Presentation/Core/Rendering with Avalonia and WPF. The sample composes the owned MIT kernel/editor with the attributed permissive PdfPig interpreter, not a commercial PDF SDK.

## Versions and supported targets

Pinned to stable **Uno.Sdk 6.7.30** and **Uno 6.7.135**, .NET 10 and SkiaSharp 3.119.4. The SDK package was published September 18, 2026; the Uno 6.7 announcement followed September 24. The reusable control package targets `net10.0`. The sample has `net10.0-browserwasm` and `net10.0-desktop` heads and composes retained scenes with `SKCanvasElement`.

The browser head is intended for modern browsers on Windows, Linux and macOS. The native desktop sample currently includes **only the macOS host**. The stock X11 host pulls in an LGPL video-player dependency, and the stock Win32 host pulls in Windows SDK metadata packages with non-permissive SDK terms. They are deliberately not restored or shipped. Use the browser sample on those operating systems, or the existing native Avalonia/WPF applications. Native Android/iOS, WinAppSDK and physical-GPU behavior are not separately qualified here. This is not a claim of complete native Uno platform coverage.

Primary references: [Uno 6.7](https://platform.uno/blog/uno-platform-6-7/), [SKCanvasElement](https://platform.uno/docs/articles/controls/SKCanvasElement.html). The sample opts out of implicit Uno packages and declares only the runtime packages it uses; no commercial development tooling, legacy service locator or video player is enabled.

## Build and integrate

```sh
dotnet workload install wasm-tools
dotnet restore ProPDF.slnx -p:Configuration=Release
python scripts/fetch-uno-notices.py
# Native macOS sample:
dotnet run --project samples/ProPDF.Uno.Sample -c Release -f net10.0-desktop
# Browser publication:
dotnet publish samples/ProPDF.Uno.Sample -c Release -f net10.0-browserwasm
```

The app is staged at `/ProPDF/`, with documentation at `/ProPDF/docs/` and dependency notices at `/ProPDF/licenses/`. One workflow deploys them together, preventing documentation from overwriting the application. Deployment still requires enabled GitHub Pages and an authorized successful run; configuration does not establish that a URL is live.

```csharp
// UI thread; host owns context, renderer and viewport.
var control = new ProPDF.Uno.PdfEditor { Context = context };
// Viewer: new ProPDF.Uno.PdfView { Controller = viewport };
// Hosts may supply Files = their IPdfUnoFiles implementation.
```

Use `Skia;SkiaRenderer` in the consuming Uno app, preserve the explicit package selection and audit its entire dependency graph. Detach/dispose controls before disposing shared services. Each window requires its own viewport. The Uno control library does not depend on the concrete editing backend.

## Editor workflows

The shell binds existing shared commands: opening/new/save, undo/redo, layouts, navigation, zoom and search; thumbnails and page organization; text/region/annotation tools; existing-content single/multiple selection and transforms, alignment/distribution, appearance and image replacement; comments/forms; bookmarks/links; metadata/attachments; raster/text/page export and comparison. The browser uses the same PDF backend, not a replacement JavaScript engine.

The inspector contains Edit, Review, Forms, Navigate, Export/compare and Document sections. Scrollable toolbars and optional sidebars support narrow windows. Pointer, touch, wheel, keyboard and native Uno controls share viewport logic. Basic framework accessibility is not a complete PDF text accessibility provider.

## Files and browser boundaries

`IPdfUnoFiles` grants file access through pickers or host callbacks. Native files are staged; the browser imports locally and hands exports to the download manager. There is no upload, analytics or automatic persistent storage. Download work before reloading. A before-unload guard covers unsaved edits and unapplied marks, subject to browser policy.

Browser limits: 32 MiB per imported file, 128 MiB per workspace, 64 MiB per download and 64 pending export registrations. These are not hostile-input isolation or an OS memory cap.

`PdfSession.SaveAndPublishAsync` marks a document clean only after the host handoff succeeds. Failed/cancelled publication retains dirty state. Browser success means bytes were submitted to its download manager, not proof that a file was retained on disk. Saving does not silently overwrite temporary import paths.

The browser runtime lacks some desktop cryptographic providers. Password-protected PDFs and certificate/CMS signing are not claimed as supported browser workflows without qualification. Native keys/certificate stores are not exposed to the page. Existing desktop SDK services retain their behavior.

## Licensing and validation

The fail-closed license gate audits all restored projects. Legacy Eventing and ICU packages have exact-version, manifest-hash reviews backed by pinned upstream legal texts. `fetch-uno-notices.py` retrieves only three bounded, hash-verified legal documents; changed texts require review. Complete notices are packaged and propagated to consumers and the published site. This does not approve unrelated GPL build scripts, SDK metadata or media libraries. Preserve the full dependency inventory and notices when redistributing; the gate is not legal certification of every native component.

Portable tests cover failed/cancelled file handoffs. Browser CI builds the desktop head, publishes real WebAssembly, audits dependencies, then runs Chromium against the project base path. The opt-in `?test=1` bridge exercises actual bound controls and rereads PDF state. It is sample-only, not part of the reusable library.

Inherited [feature limitations](features.md), [redaction boundaries](editing.md), font/codec/color restrictions and in-process resource risks remain. Uno support does not complete Adobe Acrobat parity.
