using ProPDF.Core;
using ProPDF.Editing;
using ProPDF.Engine.PdfPig;
using ProPDF.Presentation;
using ProPDF.Rendering.Skia;
using Xunit;

namespace ProPDF.Tests;

public sealed class FileTransferTests
{
    private static async Task<(PdfSession Session, PdfPigBackend Backend, ManagedPdfEditor Editor)> CreateAsync()
    {
        var backend = new PdfPigBackend(); var editor = new ManagedPdfEditor(backend);
        var document = await editor.CreateAsync(); var session = new PdfSession(backend, editor);
        using var input = document.OpenRead(); await session.OpenAsync(input);
        await session.ApplyAsync(new AddText(1, new PdfPoint(30, 60), "Browser transfer regression"));
        return (session, backend, editor);
    }

    [Fact]
    public async Task FailedHandoffRetainsRevisionDirtyHistoryAndSavedPath()
    {
        var (session, backend, _) = await CreateAsync(); var revision = session.Current!.Id;
        var path = Path.Combine(Path.GetTempPath(), $"propdf-handoff-{Guid.NewGuid():N}.pdf");
        var events = new List<PdfChangeKind>(); session.Changed += (_, e) => events.Add(e.Kind);
        try
        {
            await Assert.ThrowsAsync<IOException>(() => session.SaveAndPublishAsync(path, async (staged, token) =>
            {
                Assert.Equal(Path.GetFullPath(path), staged); Assert.True(session.IsDirty);
                using var stream = File.OpenRead(staged);
                var copy = await backend.OpenAsync(stream, cancellationToken: token);
                Assert.Single(copy.Pages);
                throw new IOException("Host rejected the download");
            }));
            Assert.True(session.IsDirty); Assert.True(session.CanUndo); Assert.Null(session.FilePath);
            Assert.Equal(revision, session.Current!.Id); Assert.Empty(events);
            Assert.True(await session.UndoAsync()); Assert.False(session.IsDirty);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public async Task SuccessfulHandoffPrecedesSavedNotificationAndKeepsHistory()
    {
        var (session, _, _) = await CreateAsync(); var path = Path.Combine(Path.GetTempPath(), $"propdf-handoff-{Guid.NewGuid():N}.pdf");
        var published = false; var saved = 0;
        session.Changed += (_, e) => { if (e.Kind == PdfChangeKind.Saved) { Assert.True(published); saved++; } };
        try
        {
            await session.SaveAndPublishAsync(path, (staged, token) =>
            {
                Assert.True(session.IsDirty); Assert.True(File.Exists(staged));
                token.ThrowIfCancellationRequested(); published = true; return Task.CompletedTask;
            });
            Assert.False(session.IsDirty); Assert.Equal(1, saved); Assert.True(session.CanUndo);
            Assert.Equal(Path.GetFullPath(path), session.FilePath);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public async Task CancelledHandoffDoesNotMarkSaved()
    {
        var (session, _, _) = await CreateAsync(); var path = Path.Combine(Path.GetTempPath(), $"propdf-handoff-{Guid.NewGuid():N}.pdf");
        using var cancellation = new CancellationTokenSource(); var saved = false;
        session.Changed += (_, e) => saved |= e.Kind == PdfChangeKind.Saved;
        try
        {
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => session.SaveAndPublishAsync(path, (_, token) =>
            { cancellation.Cancel(); token.ThrowIfCancellationRequested(); return Task.CompletedTask; }, cancellation.Token));
            Assert.True(session.IsDirty); Assert.False(saved); Assert.Null(session.FilePath);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public async Task HandoffKeepsEditSerializationUntilPublicationCompletes()
    {
        var (session, _, _) = await CreateAsync(); var path = Path.Combine(Path.GetTempPath(), $"propdf-handoff-{Guid.NewGuid():N}.pdf");
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        try
        {
            var saving = session.SaveAndPublishAsync(path, async (_, _) => { entered.SetResult(); await release.Task; });
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
            var edit = session.ApplyAsync(new RotatePage(1)); Assert.False(edit.IsCompleted);
            release.SetResult(); await saving; await edit;
            Assert.True(session.IsDirty); Assert.True(session.CanUndo);
            await session.UndoAsync(); Assert.False(session.IsDirty);
        }
        finally { release.TrySetResult(); File.Delete(path); }
    }

    [Theory]
    [InlineData("save")]
    [InlineData("png")]
    [InlineData("text")]
    [InlineData("range")]
    [InlineData("page")]
    public async Task WorkspaceOutputCallsHostExactlyOnce(string operation)
    {
        var (session, backend, editor) = await CreateAsync();
        await using var renderer = new SkiaPdfRenderer(backend);
        await using var viewport = new PdfViewportController(session, renderer, backend);
        using var dialogs = new TransferDialogs();
        using var workspace = new PdfWorkspace(new PdfEditorContext(viewport, editor, documentLoader: backend), dialogs);
        workspace.ExportDpi = "36";
        var command = operation switch { "save" => workspace.SaveCommand, "png" => workspace.ExportPngCommand,
            "text" => workspace.ExportTextCommand, "range" => workspace.ExtractRangeCommand, _ => workspace.ExtractPageCommand };
        Assert.True(command.CanExecute(null)); await command.ExecuteAsync();
        Assert.Null(viewport.LastError); Assert.Equal(1, dialogs.Publications); Assert.NotEmpty(dialogs.Output);
        if (operation == "save") { Assert.False(session.IsDirty); await command.ExecuteAsync(); Assert.Equal(2, dialogs.Picks); }
        else Assert.True(session.IsDirty);
    }

    private sealed class TransferDialogs : IPdfWorkspaceDialogs, IPdfWorkspaceFileTransfer, IDisposable
    {
        private readonly string _directory = Path.Combine(Path.GetTempPath(), "propdf-transfer-test-" + Guid.NewGuid().ToString("N"));
        public bool AlwaysPickSaveDestination => true;
        public int Picks { get; private set; }
        public int Publications { get; private set; }
        public byte[] Output { get; private set; } = [];
        public Task<string?> PickOpenPathAsync(PdfFileKind kind, CancellationToken token) => Task.FromResult<string?>(null);
        public Task<string?> PickSavePathAsync(string name, CancellationToken token)
        { Directory.CreateDirectory(_directory); Picks++; return Task.FromResult<string?>(Path.Combine(_directory, Picks + name)); }
        public async Task PublishFileAsync(string path, CancellationToken token)
        { Publications++; Output = await File.ReadAllBytesAsync(path, token); }
        public Task<string?> RequestPasswordAsync(CancellationToken token) => Task.FromResult<string?>(null);
        public Task<bool> ConfirmAsync(string title, string message, CancellationToken token) => Task.FromResult(true);
        public Task CopyTextAsync(string text, CancellationToken token) => Task.CompletedTask;
        public void Dispose() { if (Directory.Exists(_directory)) Directory.Delete(_directory, true); }
    }
}
