# Templated Avalonia PDF viewer

`ProPDF.Avalonia.PdfViewer` is a reusable Avalonia `TemplatedControl` over the existing ProPDF presentation, PdfPig parsing/text, and Skia rendering engines. It is not a WebView wrapper or a browser-only imitation. The same control and shared sample are used by the desktop and Avalonia WebAssembly heads.

## Run the samples

Use the .NET 10 SDK selected by `global.json`. The desktop/shared libraries target .NET 8; the browser entry point targets `net10.0-browser`.

```sh
dotnet run --project samples/ProPDF.Avalonia.Viewer.Desktop -p:ProPDFBuildBrowser=false

dotnet workload install wasm-tools
dotnet publish samples/ProPDF.Avalonia.Viewer.Browser -c Release -p:ProPDFBuildBrowser=false -o artifacts/viewer
# Serve the directory that contains index.html and _framework/.
python3 scripts/avalonia-viewer-server.py --directory artifacts/viewer/wwwroot --port 8123
```

The full validation script locates the SDK's actual publish directory instead of assuming a fixed output layout:

```sh
bash scripts/avalonia-viewer-validate.sh
```

The browser application must be served over HTTP on localhost or HTTPS; opening `index.html` with `file://` does not work. The supplied local server supplies WebAssembly MIME types and cross-origin-isolation headers. Files are processed in the application, not uploaded to a server. Normal browser storage sandbox restrictions still apply.

`ProPDF.Avalonia.Viewer.slnx` contains only the viewer-related projects, without requiring the unrelated Uno or WPF sample workloads.

## Embed the control

Reference `ProPDF.Avalonia` and supply an application-owned `PdfEditorContext`. Include Fluent (or another compatible Avalonia theme) and the viewer styles:

```xml
<Application.Styles>
  <FluentTheme />
  <StyleInclude Source="avares://ProPDF.Avalonia/Themes/PdfViewer.axaml" />
</Application.Styles>
```

```xml
<pdf:PdfViewer xmlns:pdf="using:ProPDF.Avalonia"
               Context="{Binding PdfContext}"
               PageNumber="{Binding CurrentPage, Mode=TwoWay}"
               Zoom="{Binding Zoom, Mode=TwoWay}"
               IsReadOnly="True"
               IsSidebarVisible="True" />
```

A basic composition, using the existing engine services, is:

```csharp
var backend = new PdfPigBackend(fontCatalog);
var editor = new ManagedPdfEditor(backend);
var session = new PdfSession(backend, editor);
var renderer = new SkiaPdfRenderer(backend);
var viewport = new PdfViewportController(session, renderer, backend,
    action => Dispatcher.UIThread.Post(action));
var context = new PdfEditorContext(viewport, editor,
    token => editor.CreateAsync(cancellationToken: token), backend);
viewer.Context = context;

// Call from the UI thread after the viewer is attached.
await viewer.LoadAsync(inputStream, cancellationToken: token);
```

The input stream remains owned by the caller. A failed open preserves the previous session snapshot. Hosts with their own load workflow may open directly through the session; the viewer observes its revision changes.

The sample uses the Inter font resources already embedded in its Avalonia dependency as an explicit instance-scoped fallback catalog. It does not depend on browser operating-system fonts or download fonts at runtime. Applications should supply font programs appropriate to their PDFs and licensing requirements; embedded PDF fonts retain precedence.

## Features and boundaries

| Area | Implementation |
| --- | --- |
| Rendering | Existing visible-tile Skia rendering, density-aware thumbnails, viewport clipping, rendering cancellation and existing bounded caches. UI notifications are coalesced. |
| Navigation | First/previous/next/last page, editable page number, thumbnails, outline, safe internal/external links, back/forward view history. |
| Layout | Continuous, single-page and facing layouts, percentage zoom, zoom steps, fit width and fit page/spread. |
| Reading | Pointer panning, scrollbars/wheel, text selection, clipboard copy, keyboard shortcuts. |
| Search | Document-wide text search, case/whole-word/regular-expression options, highlighted matches, result list, previous/next match, debounce, cancellation and stale-result protection. |
| Review | Host-enabled highlight, note, free-text and ink tools; annotation inspector, edit/delete comments; undo/redo. |
| Forms | Field list, value editing, explicit flatten operation through the existing engine. No execution of XFA or PDF JavaScript. |
| File operations | Stream-based Avalonia storage APIs work in desktop and browser hosts; password/confirmation prompts appear inside the control. |
| Export | Save PDF copy, current-page PNG/JPEG export, page-range text export and PDF page extraction. |
| Organization | Host-enabled rotate, insert, duplicate, move, delete and merge commands, backed by the existing transactional editor. Rotation edits the PDF rather than being a view-only display transform. |
| Inspector | Metadata, attachments and document information; attachments are never executed. |
| Styling | Five replaceable `ControlTheme`/`TemplatedControl` building blocks, light/dark dynamic resources, optional toolbar/sidebar/status/search UI, responsive sidebar overlay. |
| Host integration | Bindable styled/direct properties, awaitable commands, routed document/page/search/error events, host-owned context, replaceable dialog and print services. |
| Printing | Explicit handoff to a system PDF application on desktop or a browser PDF tab. It is not a native advanced print-settings UI and cannot confirm physical printing. |
| Accessibility | Named controls, keyboard operation, focus-restoring modal prompts and a document automation peer. This is not a full PDF/UA semantic reading tree. |

This is an extension of ProPDF's alpha engine, not a claim of complete Acrobat feature parity or production certification. OCR, a complete tagged-PDF accessibility tree, arbitrary XFA scripting, print-spooler control, trusted-signature validation UI, view-only page rotation and certified redaction are not implemented by this viewer. Complex malformed/hostile documents still require application-level isolation, resource policy and a representative PDF corpus review before deployment. See the repository's broader feature matrix for engine-specific limitations.

## Templates and themes

The viewer does not overwrite its host's `DataContext`. Its internal template binds to the templated parent. `Context` is the explicit service boundary.

All five controls are independently templatable:

- `PdfViewer`: overall shell, status, search and prompt layers.
- `PdfViewerToolbar`: command chrome; `Owner` references the viewer.
- `PdfViewerSidebar`: navigation/inspector panels; `Owner` references the viewer.
- `PdfViewerViewport`: synchronized scrollbars around the existing `PdfView` drawing surface.
- `PdfViewerThumbnail`: page-thumbnail chrome around the existing lazy renderer.

The default viewer discovers `PART_Viewport` (`PdfViewerViewport`) and `PART_SearchBox` (`TextBox`). Default in-control dialogs use `PART_DialogInput` and `DialogAcceptButton`. A template omitting dialog parts must supply a host `Dialogs` service; a required prompt otherwise produces an explicit error instead of waiting indefinitely.

The viewport discovers `PART_Surface` (`PdfView`), `PART_VerticalScrollBar` and `PART_HorizontalScrollBar`. All are optional; omitted parts do not throw. Old parts are disconnected before new parts are attached. The sample's **Minimal template** checkbox replaces the whole viewer template and then restores it, rather than merely hiding the toolbar.

Dynamic brush keys are `PdfViewerSurface`, `PdfViewerSubtle`, `PdfViewerBorder`, `PdfViewerText`, `PdfViewerMuted`, `PdfViewerAccent`, and `PdfViewerError`. Override these in the host's resources to brand the chrome. The PDF page content is not recolored in dark mode.

## State and commands

Configuration uses styled properties: `Context`, `Dialogs`, `PrintService`, `IsReadOnly`, visibility flags, `SidebarWidth`, `SidebarIndex`, `PageNumber`, `Zoom`, `ZoomMode`, `LayoutMode`, `Tool`, search query/options and text-entry fields. Page/zoom/sidebar values are bounded and non-finite zoom/width values are coerced safely.

Read-only direct properties expose `Workspace`, `PageCount`, `HasDocument`, `CanEdit`, `CanAnnotate`, `IsBusy`, `IsSearching`, `IsCompact`, title/status/error, results and prompt state. Routed events are `DocumentChanged`, `PageChanged`, `SearchCompleted` and `ErrorOccurred`. `DocumentChanged` reports a new session revision, including edits, not just a new file.

Viewer-specific commands include navigation, zoom/fit, search/cancel/results, tool selection, undo/redo, print, sidebar/search toggles and error dismissal. Existing engine/inspector/export commands are exposed through `Workspace`. `PdfViewerCommand.ExecuteAsync()` is awaitable and rejects concurrent execution of the same command.

`IsReadOnly` is host policy for the viewer UI and pointer/keyboard tools, not a security boundary around the externally supplied session. A host that exposes raw `Workspace` editing commands can still intentionally edit the document. Do not expose a separate unrestricted command surface to users expected to have read-only access.

## Ownership, cancellation and file safety

The host owns the backend, editor, session, renderer, viewport and `PdfEditorContext`. Detaching the viewer cancels its search, prompts and workspace operations, unsubscribes observers and disconnects visual parts. It does not dispose host-owned services. Dispose the viewport and renderer when the application no longer needs them; the samples demonstrate this order.

Viewer properties and UI methods are UI-thread APIs. Background parsing/rendering notifications are marshalled through the supplied dispatcher and coalesced before UI updates. Search generations prevent stale completions from replacing newer queries or a different document revision.

The default file service copies browser/native storage streams into private staging files. Imports have a 128 MiB limit, checked both against available metadata and while copying. Save always asks for a user-selected destination; writing a staging file alone never counts as successful user output. Completed/cancelled operations clean up their private staging files. Output streams and storage handles are disposed. Passwords are cleared from the prompt state when it closes and are not included in the sample diagnostics.

External URI activation passes through the engine's URI policy and an explicit confirmation. PDF JavaScript, attachment execution and arbitrary URI scheme launching are not added by this viewer. Browser popup policies can still prevent print/link handoff; the failure is surfaced rather than silently treated as success.

## Keyboard and interaction reference

Ctrl/Cmd+O opens, Ctrl/Cmd+S saves a copy, Ctrl/Cmd+F opens search, F3/Shift+F3 navigate matches, Ctrl/Cmd+plus/minus zoom, Ctrl/Cmd+0 fits the page, Ctrl/Cmd+Home/End navigate first/last page, Alt+Left/Right traverse view history, and Ctrl/Cmd+Z/Y perform permitted undo/redo. Enter applies the page/zoom/search input. Escape cancels selection/search or closes the active prompt. Text-entry controls retain their own editing behavior instead of forwarding undo to the PDF session.

## Validation artifacts

The `Avalonia viewer` workflow captures the exact commit and source tree before compiling. It builds the reusable control and desktop sample, runs the existing engine tests, publishes the actual WebAssembly application, and executes Chromium at device scales 1 and 2. The browser test uses mouse, keyboard, clipboard, file-picker and download interactions. The opt-in `?diagnostics=1` hook only reads state and visual bounds; it does not provide mutation or command execution shortcuts.

Artifacts include build logs, engine TRX results, a published browser bundle, browser JSON reports, screenshots, actual saved/exported files, and source identity. A green workflow is required before claiming browser validation. Consult the generated reports for the exact assertions and browser version rather than treating a successful compilation as an interaction-test pass.
