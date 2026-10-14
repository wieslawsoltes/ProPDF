using ProPDF.Core;

namespace ProPDF.Avalonia.Viewer.Smoke;

internal static class ViewerFileSaveChecks
{
    public static void Verify(PdfViewer viewer, Action<Task> pump, Action<bool, string> check)
    {
        var session = viewer.Context!.Session;
        var workspace = viewer.Workspace!;
        check(viewer.FileSaveService is null, "Native save picker remains the default");
        pump(session.ApplyAsync(new SetFormValue("Name", "Save handoff regression"), session.Current!.Id));
        var revision = session.Current!.Id;
        var service = new RecordingSave { Hold = new(TaskCreationOptions.RunContinuationsAsynchronously) };
        viewer.FileSaveService = service;
        var save = workspace.SaveAsCommand.ExecuteAsync();
        pump(service.Entered.Task);
        check(session.IsDirty && workspace.IsBusy && service.Input!.CanRead, "Pending save keeps dirty state and its input stream alive");
        var replacement = new RecordingSave();
        viewer.FileSaveService = replacement;
        pump(workspace.SaveAsCommand.ExecuteAsync());
        check(service.Calls == 1 && replacement.Calls == 0, "Pending save rejects duplicates and retains its captured host service");
        service.Hold!.SetResult(); pump(save);
        check(!session.IsDirty && session.Current!.Id == revision, "Successful output handoff marks only the saved revision clean");
        check(service.Bytes.AsSpan().StartsWith("%PDF-"u8) && service.Name == "document.pdf", "Host receives real PDF bytes and a friendly suggested filename");
        check(!service.Input!.CanRead && !File.Exists(service.Stage), "Completed save closes its stream and removes private staging bytes");

        pump(session.ApplyAsync(new SetFormValue("Name", "Failed save regression"), session.Current!.Id));
        revision = session.Current!.Id;
        var failure = new RecordingSave { Fail = true };
        viewer.FileSaveService = failure;
        pump(workspace.SaveAsCommand.ExecuteAsync());
        check(session.IsDirty && session.Current!.Id == revision && viewer.HasError, "Failed host output preserves the dirty document and reports an error");
        check(!failure.Input!.CanRead && !File.Exists(failure.Stage), "Failed host output also releases stream and staging bytes");
        pump(viewer.ClearErrorCommand.ExecuteAsync());

        var cancelled = new RecordingSave { Cancel = true };
        viewer.FileSaveService = cancelled;
        pump(workspace.SaveAsCommand.ExecuteAsync());
        check(session.IsDirty && session.Current!.Id == revision && !cancelled.Input!.CanRead, "Cancelled host output cannot mark a document clean or retain its stream");
        pump(viewer.ClearErrorCommand.ExecuteAsync());
        viewer.FileSaveService = replacement;
        pump(workspace.SaveAsCommand.ExecuteAsync());
        check(!session.IsDirty && replacement.Calls == 1 && replacement.Name == "document.pdf", "A fresh save succeeds after failure and cancellation");
        viewer.FileSaveService = null;
    }

    private sealed class RecordingSave : IPdfViewerFileSaveService
    {
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource? Hold { get; init; }
        public bool Fail { get; init; }
        public bool Cancel { get; init; }
        public int Calls { get; private set; }
        public byte[] Bytes { get; private set; } = [];
        public string? Name { get; private set; }
        public string? Stage { get; private set; }
        public Stream? Input { get; private set; }
        public async Task SaveAsync(Stream content, string suggestedName, CancellationToken cancellationToken = default)
        {
            Calls++; Input = content; Name = suggestedName; Stage = (content as FileStream)?.Name;
            using var copy = new MemoryStream();
            await content.CopyToAsync(copy, cancellationToken);
            Bytes = copy.ToArray(); Entered.TrySetResult();
            if (Hold is not null) await Hold.Task.WaitAsync(cancellationToken);
            if (Fail) throw new IOException("Deliberate host output failure.");
            if (Cancel) throw new OperationCanceledException("Host cancelled its save.");
        }
    }
}
