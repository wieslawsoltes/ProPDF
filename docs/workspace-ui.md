# Document workspace

The Avalonia, WPF and Uno sample applications use the same document-first workspace. Its layout follows the task-based organization of the current Acrobat interface, with ProPDF branding and original vector icons. It is not a copy of Adobe assets and does not claim complete Acrobat functionality.

## Find the tools

The **All tools** panel groups the existing capabilities into Edit a PDF, Export a PDF, Organize pages, Add comments, Prepare a form, Redact a PDF, Bookmarks & links, and Document properties. Selecting a task opens its existing inspector. The back arrow returns to the catalog, and the close button gives the document more space.

The top bar keeps Open, Save, Undo, Redo and Find accessible. **Menu** opens file operations, protected opening, Save a copy, combining documents, page extraction, copying selected text, and page-display options. Search is a separate dismissible row; Enter runs a search. Page navigation and zoom are in the bottom bar. Fit width and Fit page are on the right rail.

The floating palette selects the hand, text selection, highlight, sticky-note, ink and text tools. Tasks with text or drawing operations retain the full tool selector and draft text input. Detailed text, object, alignment, appearance and image controls remain in the Edit inspector. PDF edits use the same transactions, undo history, confirmations, capability checks and file services as before.

The right rail opens page thumbnails, bookmarks, comments and document properties. On a wide window the tools and thumbnail panels may both be open. Below 980 logical pixels they overlay the document, with only the most recently opened drawer in front. A closed panel stays closed on resize. Below 600 logical pixels the quick-tools palette becomes a horizontal row above the document, so it cannot obscure page content. Undo and Redo move into Menu; no command requires a wide window.

## Keyboard and focus

Use Ctrl+O to open, Ctrl+S to save, Ctrl+Shift+S to save a copy, and Ctrl+F to find. Avalonia also recognizes the platform Meta modifier. Escape closes search first, then the front compact drawer. Buttons have descriptive automation names and tooltips; icon-only controls do not depend on an icon font. Page number accepts Enter as well as the Go button.

## Reuse and implementation

`PdfShellState`, `PdfShellCategory`, `PdfShellSection` and `PdfShellIcons` belong to `ProPDF.Presentation`. The state object is owned by a `PdfWorkspace` and used on its UI thread. Shell navigation does not create document revisions. Hosts may use the state and commands with their own interface.

`src/Shared/PdfEditorChrome.cs` is linked into each native adapter library. One visual-tree implementation defines the application bar, task catalog, quick tools, file/organize/redact panels, search, page navigation and responsive drawers. Conditional code is limited to each framework's binding, vector geometry, automation and keyboard APIs. Existing native content/inspector controls remain independently usable. The source file is not a fourth UI framework or a new runtime dependency.

Controls and panels are reused during task changes rather than recreated. Repeated unchanged layout state produces no property-change notifications. The selected quick-tool appearance is only updated when its state changes. Existing tile caches, density-aware rendering, cancellation and lease ownership are retained.

## Boundaries

The document tab represents the currently open PDF; it is not a multi-document tab manager. There are no nonfunctional Adobe cloud, AI, OCR, e-signature or print buttons. Existing format, rendering, browser cryptography, and native Uno platform limitations remain. High-density correctness and browser workflow checks do not substitute for hardware, assistive-technology, or complete PDF conformance qualification.
