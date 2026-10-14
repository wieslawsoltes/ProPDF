# Avalonia viewer file output

The reusable viewer defaults to Avalonia's native storage picker. Applications may set `PdfViewer.FileSaveService` to an `IPdfViewerFileSaveService` when the host uses a different explicit output mechanism.

```csharp
public interface IPdfViewerFileSaveService
{
    Task SaveAsync(Stream content, string suggestedName,
        CancellationToken cancellationToken = default);
}

viewer.FileSaveService = applicationOwnedFileSaveService;
```

The viewer owns the readable stream and keeps it alive until the returned task completes. Implementations must not dispose or retain it. The application owns the service; the viewer does not dispose it. Return only after the output handoff completes, and throw on failure or cancellation.

The service is captured for each save operation. Replacing the property cannot redirect an operation already in progress. Save commands are serialized by the workspace. A failed or cancelled handoff preserves the dirty revision, and private staging files and streams are released on completion. Set the property back to `null` to restore native pickers; this does not change an already captured save.

## Browser sample

The browser sample supplies `BrowserFileSaveHandoff`. Save copy, page images, text exports and other workspace outputs become explicit browser downloads; opening still uses Avalonia's file picker. Files remain local and are not uploaded to a server. The handoff uses a Blob and a temporary download link, limits each output to 128 MiB, strips path components from suggested filenames and revokes object URLs after the browser consumes the request.

This is an intentional host integration, not a permission workaround. It does not override browser permission methods, change native filesystem handles or retry a denied native save through another mechanism. In the pinned Avalonia browser implementation, forcing the file-dialog polyfill on a browser with native file pickers can subject a write-only download handle to native read/write permission checks. The sample avoids that incompatible handle combination by selecting its download service in advance.

A successful download handoff means that the browser accepted the output request. Browser APIs do not prove that a user retained the file on disk. The sample's dirty marker clears after handoff, not after independent verification of the user's Downloads folder. Native hosts using real storage destinations retain the stronger completion semantics of their output stream.

Browser output is currently buffered and base64-marshalled, so its peak memory usage exceeds the file size. The 128 MiB cap is an upper bound, not a tested performance promise. Hosts needing large or streaming exports should provide an appropriate streaming save service.

## Validation

`ViewerFileSaveChecks` extends the real-Skia native viewer suite with pending-save, captured-service, duplicate-command, stream ownership, staging cleanup, failure, cancellation and retry checks. `scripts/tests/viewer-file-save.test.mjs` covers byte contents, filenames, MIME types, error propagation and object-URL lifetime.

The Playwright viewer suite remains the integration authority: it clicks Save copy in the actual Avalonia WebAssembly application, captures the actual browser download, exports PNG/text, reopens the downloaded PDF through the file picker and checks the saved form value. A successful helper test is not a substitute for these browser workflows. Consult the exact commit's CI reports for executed results.
