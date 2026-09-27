# Navigation, bookmarks and links

The Navigate inspector is available in both native editor shells. Its view model lives in Presentation; Core defines vendor-independent records and `IPdfNavigationService`, implemented by the optional iText adapter. A viewer-only host may supply another navigation implementation; the PdfPig adapter alone does not currently implement this service.

## Reading destinations

Navigation parsing is data-only: it never executes document actions. It resolves local explicit and named destinations, including common Fit and XYZ variants, and exposes bookmark hierarchy and link rectangles. It supports the named FirstPage, LastPage, NextPage and PrevPage actions. Malformed or unsupported destinations have an explicit reason rather than triggering hidden fallback behavior.

Outline traversal detects cycles and enforces entry, depth and title limits. Named destination resolution has its own cycle/depth checks. Bounds and explicit points are converted through cropped/rotated page transforms. Specialized fit behavior and unusual PDFs still require broader interoperability qualification.

Remote-file, launch, JavaScript, multimedia and chained actions are disabled. URI destinations are limited to HTTP, HTTPS and mailto, reject control characters and backslashes, and reject embedded credentials in web URLs. Recognizing a URI does not assert that its destination is trustworthy.

## Native workflows

Select a bookmark and choose Go. Back and Forward restore bounded, revision-aware page/zoom/scroll history. History entries from an edited revision cannot be applied to the new revision. The inspector lists document links; Follow navigates internal targets or requests external confirmation. Copy URI does not open the address.

External activation requires the host to implement `IPdfExternalNavigation`. The built-in desktop hosts open the normalized URI with the system application **only after the workspace's explicit confirmation**. There is no automatic browser launch while opening/parsing a PDF. Custom hosts can omit external activation entirely. Links are currently activated from the inspector, not by clicking their page rectangles.

Select a region, put a destination page number in the main toolbar text box, then choose Link region to author a native internal link. The SDK additionally accepts a destination point. Bookmark Child and Rename use the toolbar text. New child bookmarks target the current page; deletion requires confirmation and removes the subtree.

## Outline editing API

```csharp
await session.ApplyAsync(new IPdfEditOperation[]
{
    new InsertOutline("Chapter", 2),
    new InsertOutline("Details", 3, ParentPath: "0"),
    new UpdateOutline("0/0", Title: "Updated details"),
    new MoveOutline("0/0", ParentPath: null, Position: 1)
}, expectedRevision: session.Current!.Id);
```

Paths are zero-based sibling paths such as `0/2`, and refer to the current state **at that operation's position in the transaction**. Moves use the final sibling position, preserve subtrees, and reject moving a node into itself or its descendants. Removing or moving earlier siblings changes subsequent paths; prepare a coherent transaction against a captured revision. This is not a concurrent tree-editing/CRDT API.

Subtree movement is available in the SDK; the built-in inspector currently exposes insert-child, rename and delete rather than drag-and-drop tree reorganization.
