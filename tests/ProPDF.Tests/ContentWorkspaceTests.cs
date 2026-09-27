using System.Collections.Concurrent;
using ProPDF.Core;
using ProPDF.Editing;
using ProPDF.Engine.PdfPig;
using ProPDF.Presentation;
using ProPDF.Rendering.Skia;
using Xunit;

namespace ProPDF.Tests;

public sealed class ContentWorkspaceTests
{
    [Fact]
    public async Task PointerDragMovesNativeObjectAndUndoRestoresIt()
    {
        await using var runtime = await Runtime.CreateAsync(); var view = runtime.View;
        await view.LoadContentAsync(); view.Tool = PdfTool.EditObject;
        var original = Assert.Single(view.ContentObjects, item => item.Kind == PdfContentObjectKind.Path);
        var center = runtime.Screen(new(original.Bounds.X + original.Bounds.Width / 2, original.Bounds.Y + original.Bounds.Height / 2));
        Assert.True(view.BeginInteraction(center));
        await view.EndInteractionAsync(new(center.X + 20 * runtime.Scale, center.Y + 15 * runtime.Scale));
        var moved = Assert.Single((await runtime.Editor.ReadPageContentAsync(runtime.Session.Current!, 1)).Objects, item => item.Kind == PdfContentObjectKind.Path);
        Assert.Equal(original.Bounds.X + 20, moved.Bounds.X, 4); Assert.Equal(original.Bounds.Y + 15, moved.Bounds.Y, 4);
        Assert.Null(view.SelectedContentObject);
        Assert.True(await runtime.Session.UndoAsync()); await view.LoadContentAsync();
        Assert.Equal(original.Bounds, Assert.Single(view.ContentObjects, item => item.Kind == PdfContentObjectKind.Path).Bounds);
    }
    [Fact]
    public async Task CornerResizeChangesNativeGeometryAndCancellationDoesNotEdit()
    {
        await using var runtime = await Runtime.CreateAsync(); var view = runtime.View;
        await view.LoadContentAsync(); view.Tool = PdfTool.EditObject;
        var item = Assert.Single(view.ContentObjects, item => item.Kind == PdfContentObjectKind.Path); view.SelectContentObject(item);
        var corner = runtime.Screen(new(item.Bounds.Right, item.Bounds.Bottom));
        Assert.True(view.BeginInteraction(corner)); view.MoveInteraction(new(corner.X + 40, corner.Y + 40));
        view.CancelInteraction(); var revision = runtime.Session.Current!.Id;
        await view.EndInteractionAsync(new(corner.X + 40, corner.Y + 40)); Assert.Equal(revision, runtime.Session.Current.Id);
        Assert.True(view.BeginInteraction(corner));
        await view.EndInteractionAsync(new(corner.X + 40, corner.Y + 40));
        var resized = Assert.Single((await runtime.Editor.ReadPageContentAsync(runtime.Session.Current, 1)).Objects, obj => obj.Kind == PdfContentObjectKind.Path);
        Assert.True(resized.Bounds.Width > item.Bounds.Width + 25); Assert.True(resized.Bounds.Height > item.Bounds.Height + 25);
    }
    [Fact]
    public async Task SelectingTopmostObjectAndChangingRevisionNeverReusesStaleHandles()
    {
        await using var runtime = await Runtime.CreateAsync();
        await runtime.Session.ApplyAsync(new AddShape(1, new(20, 30, 80, 50), Fill: PdfColor.Blue));
        var view = runtime.View; await view.LoadContentAsync(); view.Tool = PdfTool.EditObject;
        var point = runtime.Screen(new(50, 50)); Assert.True(view.BeginInteraction(point));
        Assert.Equal(view.ContentObjects[^1], view.SelectedContentObject);
        var old = view.SelectedContentObject!; await runtime.Session.ApplyAsync(new RotatePage(1));
        Assert.Null(view.SelectedContentObject); await view.EndInteractionAsync(point);
        Assert.Throws<PdfRevisionConflictException>(() => view.SelectContentObject(old));
    }
    [Fact]
    public async Task SharedInspectorValidatesInputAndExecutesOwnedCommands()
    {
        await using var runtime = await Runtime.CreateAsync();
        using var workspace = new PdfWorkspace(new(runtime.View, runtime.Editor), new Dialogs(), runtime.Dispatch);
        await workspace.EditObjectsCommand.ExecuteAsync(); runtime.Drain();
        workspace.SelectedContentObject = Assert.Single(workspace.ContentObjects, item => item.Kind == PdfContentObjectKind.Path);
        var before = workspace.SelectedContentObject!;
        Assert.True(workspace.ApplyObjectBoundsCommand.CanExecute(null));
        workspace.ContentX = "NaN"; var revision = runtime.Session.Current!.Id;
        await workspace.ApplyObjectBoundsCommand.ExecuteAsync(); runtime.Drain();
        Assert.Equal(revision, runtime.Session.Current.Id); Assert.Contains("finite", runtime.View.LastError);
        workspace.ContentX = (before.Bounds.X + 25).ToString(System.Globalization.CultureInfo.InvariantCulture);
        await workspace.ApplyObjectBoundsCommand.ExecuteAsync(); runtime.Drain(); await runtime.View.LoadContentAsync(); runtime.Drain();
        var moved = Assert.Single(workspace.ContentObjects, item => item.Kind == PdfContentObjectKind.Path);
        Assert.Equal(before.Bounds.X + 25, moved.Bounds.X, 4); Assert.Null(workspace.SelectedContentObject);
        workspace.SelectedContentObject = moved;
        await workspace.DuplicateObjectCommand.ExecuteAsync(); runtime.Drain(); await runtime.View.LoadContentAsync(); runtime.Drain();
        Assert.Equal(2, workspace.ContentObjects.Count(item => item.Kind == PdfContentObjectKind.Path));
        workspace.SelectedContentObject = workspace.ContentObjects.First(item => item.Kind == PdfContentObjectKind.Path);
        await workspace.DeleteObjectCommand.ExecuteAsync(); runtime.Drain(); await runtime.View.LoadContentAsync(); runtime.Drain();
        Assert.Single(workspace.ContentObjects, item => item.Kind == PdfContentObjectKind.Path);
    }
    [Fact]
    public async Task DelayedInspectionCannotOverwriteNewRevision()
    {
        await using var runtime = await Runtime.CreateAsync(); var view = runtime.View;
        var delayed = new DelayedContent(runtime.Editor); view.ConfigureContentService(delayed);
        var previous = runtime.Session.Current!.Id;
        var read = view.LoadContentAsync(); await delayed.Entered.Task;
        await runtime.Session.ApplyAsync(new AddText(1, new(20, 200), "New revision"));
        delayed.Release.TrySetResult(); await read;
        Assert.Empty(view.ContentObjects); Assert.NotEqual(previous, view.Document!.Id);
        view.ConfigureContentService(runtime.Editor); await view.LoadContentAsync();
        Assert.All(view.ContentObjects, item => Assert.Equal(view.Document.Id, item.Reference.Revision));
    }
    private sealed class DelayedContent(IPdfContentService inner) : IPdfContentService
    {
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public async Task<PdfPageContent> ReadPageContentAsync(PdfSnapshot source, int pageNumber, PdfContentInspectionOptions? options = null, CancellationToken cancellationToken = default)
        {
            Entered.TrySetResult(); await Release.Task.WaitAsync(cancellationToken);
            return await inner.ReadPageContentAsync(source, pageNumber, options, cancellationToken);
        }
    }
    private sealed class Runtime : IAsyncDisposable
    {
        private readonly ConcurrentQueue<Action> _queue = new();
        public PdfPigBackend Backend { get; } = new();
        public ManagedPdfEditor Editor { get; }
        public PdfSession Session { get; }
        public SkiaPdfRenderer Renderer { get; }
        public PdfViewportController View { get; }
        public double Scale => View.Zoom * 96 / 72;
        private Runtime()
        {
            Editor = new(Backend); Session = new(Backend, Editor); Renderer = new(Backend);
            View = new(Session, Renderer, Backend, Dispatch); View.ConfigureContentService(Editor); View.SetViewport(600, 500);
        }
        public void Dispatch(Action action) => _queue.Enqueue(action);
        public void Drain()
        {
            var budget = 10_000; while (_queue.TryDequeue(out var action)) { if (--budget == 0) throw new InvalidOperationException("Notification loop."); action(); }
        }
        public static async Task<Runtime> CreateAsync()
        {
            var runtime = new Runtime(); var doc = await runtime.Editor.CreateAsync(size: new PdfSize(400, 600));
            doc = await runtime.Editor.ApplyAsync(doc, [new AddShape(1, new(20, 30, 80, 50), Fill: PdfColor.Blue), new AddText(1, new(20, 150), "Edit me")]);
            using var input = doc.OpenRead(); await runtime.Session.OpenAsync(input); runtime.Drain(); return runtime;
        }
        public PdfPoint Screen(PdfPoint page)
        {
            var placement = PdfPageLayout.Create(Session.Current!.Pages, View.Viewport.Width, View.Zoom).Pages[0];
            return new(placement.Bounds.X + page.X * Scale - View.Offset.X, placement.Bounds.Y + page.Y * Scale - View.Offset.Y);
        }
        public async ValueTask DisposeAsync() { await View.DisposeAsync(); await Renderer.DisposeAsync(); }
    }
    private sealed class Dialogs : IPdfWorkspaceDialogs
    {
        public Task<string?> PickOpenPathAsync(PdfFileKind kind, CancellationToken cancellationToken) => Task.FromResult<string?>(null);
        public Task<string?> PickSavePathAsync(string suggestedName, CancellationToken cancellationToken) => Task.FromResult<string?>(null);
        public Task<string?> RequestPasswordAsync(CancellationToken cancellationToken) => Task.FromResult<string?>(null);
        public Task<bool> ConfirmAsync(string title, string message, CancellationToken cancellationToken) => Task.FromResult(false);
        public Task CopyTextAsync(string text, CancellationToken cancellationToken) => Task.CompletedTask;
    }
}
