using System.Collections.Concurrent;
using System.Globalization;
using ProPDF.Core;
using ProPDF.Editing;
using ProPDF.Engine.PdfPig;
using ProPDF.Presentation;
using ProPDF.Rendering.Skia;
using Xunit;

namespace ProPDF.Tests;

public sealed class ContentSelectionWorkspaceTests
{
    [Fact]
    public async Task ToggleSelectionMovesAndResizesOneGroupThenUndoesInOneStep()
    {
        await using var runtime = await Runtime.CreateAsync(); var view = runtime.View;
        var before = view.ContentObjects.ToArray(); var original = runtime.Session.Current!;
        var a = runtime.Screen(Center(before[0].Bounds)); var b = runtime.Screen(Center(before[1].Bounds));
        Assert.True(view.BeginInteraction(a, toggleContentSelection: true)); await view.EndInteractionAsync(a);
        Assert.True(view.BeginInteraction(b, toggleContentSelection: true)); await view.EndInteractionAsync(b);
        Assert.Equal(2, view.SelectedContentObjects.Count); Assert.Same(original, runtime.Session.Current);
        Assert.True(view.BeginInteraction(a));
        await view.EndInteractionAsync(new(a.X + 18 * runtime.Scale, a.Y + 13 * runtime.Scale));
        var moved = (await runtime.Editor.ReadPageContentAsync(runtime.Session.Current!, 1)).Objects;
        for (var i = 0; i < 2; i++) { Assert.Equal(before[i].Bounds.X + 18, moved[i].Bounds.X, 3); Assert.Equal(before[i].Bounds.Y + 13, moved[i].Bounds.Y, 3); }
        Assert.Equal(before[2].Bounds, moved[2].Bounds); Assert.Empty(view.SelectedContentObjects);
        Assert.True(await runtime.Session.UndoAsync()); Assert.Same(original, runtime.Session.Current); Assert.False(await runtime.Session.UndoAsync());
        await view.LoadContentAsync(); view.SelectContentObjects(view.ContentObjects.Take(2));
        var bounds = view.ContentSelectionBounds!.Value; var corner = runtime.Screen(new(bounds.Right, bounds.Bottom));
        Assert.True(view.BeginInteraction(corner)); await view.EndInteractionAsync(new(corner.X + 40 * runtime.Scale, corner.Y + 30 * runtime.Scale));
        var resized = (await runtime.Editor.ReadPageContentAsync(runtime.Session.Current!, 1)).Objects;
        var group = PdfContentSelection.Bounds(resized.Take(2));
        // Selection bounds include conservative stroke inflation. Nonuniform scale changes that approximation slightly.
        Assert.InRange(Math.Abs(group.Width - bounds.Width - 40), 0, .1);
        Assert.InRange(Math.Abs(group.Height - bounds.Height - 30), 0, .1);
        Assert.Equal(before[2].Bounds, resized[2].Bounds);
    }
    [Fact]
    public async Task InvalidSelectionAndCancellationDoNotLoseCurrentSelectionOrEditDocument()
    {
        await using var runtime = await Runtime.CreateAsync(); var view = runtime.View;
        view.SelectContentObjects(view.ContentObjects.Take(2)); var selected = view.SelectedContentObjects; var original = runtime.Session.Current!;
        Assert.Throws<ArgumentException>(() => view.SelectContentObjects(new[] { selected[0], selected[0] })); Assert.Same(selected, view.SelectedContentObjects);
        Assert.Throws<PdfRevisionConflictException>(() => view.SelectContentObject(selected[0] with { Bounds = new(0, 0, 30, 30) })); Assert.Same(selected, view.SelectedContentObjects);
        var center = runtime.Screen(Center(selected[0].Bounds)); Assert.True(view.BeginInteraction(center));
        view.MoveInteraction(new(center.X + 50, center.Y + 50)); view.CancelInteraction(); await view.EndInteractionAsync(new(center.X + 50, center.Y + 50));
        Assert.Same(original, runtime.Session.Current); Assert.Equal(PdfContentSelection.Bounds(selected), view.ContentSelectionBounds);
        Assert.True(view.BeginInteraction(center, toggleContentSelection: true)); await view.EndInteractionAsync(center);
        Assert.Single(view.SelectedContentObjects); Assert.Equal(selected[1], view.SelectedContentObject);
        view.ClearSelection(); Assert.Empty(view.SelectedContentObjects); Assert.Null(view.ContentSelectionBounds);
    }
    [Fact]
    public async Task RevisionAndPageChangesClearEverySelectedHandle()
    {
        await using var runtime = await Runtime.CreateAsync(); var view = runtime.View;
        view.SelectContentObjects(view.ContentObjects); var old = view.SelectedContentObjects;
        await runtime.Session.ApplyAsync(new RotatePage(1)); Assert.Empty(view.SelectedContentObjects); Assert.Null(view.SelectedContentObject);
        await view.LoadContentAsync(); Assert.Throws<PdfRevisionConflictException>(() => view.SelectContentObjects(old));
        view.SelectContentObjects(view.ContentObjects); await view.LoadContentAsync(2);
        Assert.Empty(view.SelectedContentObjects); Assert.Null(view.ContentSelectionBounds);
    }
    [Fact]
    public async Task SharedCommandsUseUnionBoundsAndDisableAmbiguousSingleObjectEdits()
    {
        await using var runtime = await Runtime.CreateAsync();
        using var workspace = new PdfWorkspace(new(runtime.View, runtime.Editor), new Dialogs(), runtime.Dispatch);
        var before = workspace.ContentObjects.ToArray(); workspace.SelectContentObjects(before.Take(2)); runtime.Drain();
        var bounds = PdfContentSelection.Bounds(before.Take(2)); Assert.Equal(bounds.X, double.Parse(workspace.ContentX, CultureInfo.InvariantCulture), 3);
        Assert.True(workspace.ApplyObjectBoundsCommand.CanExecute(null)); Assert.True(workspace.AlignObjectsCommand.CanExecute(null));
        Assert.False(workspace.DistributeObjectsCommand.CanExecute(null)); Assert.False(workspace.ApplyAppearanceCommand.CanExecute(null)); Assert.False(workspace.ReplaceObjectTextCommand.CanExecute(null));
        workspace.ContentX = (bounds.X + 22).ToString(CultureInfo.InvariantCulture);
        runtime.View.ReportError(new ArgumentException("unrelated redraw")); await runtime.View.WaitForRenderingAsync(); runtime.Drain();
        Assert.Equal((bounds.X + 22).ToString(CultureInfo.InvariantCulture), workspace.ContentX); // A redraw must not reset a user's draft.
        await workspace.ApplyObjectBoundsCommand.ExecuteAsync(); runtime.Drain();
        var actual = (await runtime.Editor.ReadPageContentAsync(runtime.Session.Current!, 1)).Objects;
        for (var i = 0; i < 2; i++) Assert.Equal(before[i].Bounds.X + 22, actual[i].Bounds.X, 3);
        Assert.Equal(before[2].Bounds, actual[2].Bounds); Assert.Empty(workspace.SelectedContentObjects);
        await workspace.UndoCommand.ExecuteAsync(); await runtime.View.LoadContentAsync(); runtime.Drain();
        await workspace.SelectAllObjectsCommand.ExecuteAsync(); runtime.Drain(); Assert.Equal(3, workspace.SelectedContentObjects.Count);
        workspace.ContentAlignment = PdfSelectionAlignment.Left; await workspace.AlignObjectsCommand.ExecuteAsync(); runtime.Drain();
        actual = (await runtime.Editor.ReadPageContentAsync(runtime.Session.Current!, 1)).Objects;
        Assert.All(actual, item => Assert.Equal(before.Min(o => o.Bounds.X), item.Bounds.X, 3));
    }
    [Fact]
    public async Task RegionSelectionFindsIntersectingObjectsAndProducesAGroupScene()
    {
        await using var runtime = await Runtime.CreateAsync(); var view = runtime.View;
        using var workspace = new PdfWorkspace(new(view, runtime.Editor), new Dialogs(), runtime.Dispatch);
        view.Tool = PdfTool.SelectRegion;
        Assert.True(view.BeginInteraction(runtime.Screen(new(10, 10))));
        await view.EndInteractionAsync(runtime.Screen(new(240, 155)));
        await workspace.SelectRegionObjectsCommand.ExecuteAsync(); runtime.Drain();
        Assert.Equal(2, view.SelectedContentObjects.Count); Assert.Equal(PdfTool.EditObject, view.Tool);
        await view.WaitForRenderingAsync(); using var scene = view.CaptureScene();
        using var surface = SkiaSharp.SKSurface.Create(new SkiaSharp.SKImageInfo(600, 500)); scene.Draw(surface.Canvas);
        await workspace.ClearObjectsCommand.ExecuteAsync(); Assert.Empty(view.SelectedContentObjects);
    }
    [Fact]
    public async Task BackgroundRenderingCannotEraseAnEditingDiagnostic()
    {
        await using var runtime = await Runtime.CreateAsync(); var view = runtime.View;
        view.SetZoom(1.4); view.ReportError(new ArgumentException("Invalid geometry draft"));
        await view.WaitForRenderingAsync(); runtime.Drain(); Assert.Equal("Invalid geometry draft", view.LastError);
        view.ClearError(); Assert.Null(view.LastError);
    }
    [Fact]
    public async Task PageAlignmentAcceptsOneObjectAndUsesActualPageSize()
    {
        await using var runtime = await Runtime.CreateAsync();
        using var workspace = new PdfWorkspace(new(runtime.View, runtime.Editor), new Dialogs(), runtime.Dispatch);
        var before = runtime.Session.Current!; var original = workspace.ContentObjects.ToArray();
        workspace.SelectContentObjects(original.Take(1));
        Assert.False(workspace.AlignObjectsCommand.CanExecute(null));
        workspace.ContentAlignmentReference = PdfContentAlignmentReference.Page;
        workspace.ContentAlignment = PdfSelectionAlignment.Right; runtime.Drain();
        Assert.True(workspace.AlignObjectsCommand.CanExecute(null));
        await workspace.AlignObjectsCommand.ExecuteAsync(); runtime.Drain();
        var actual = (await runtime.Editor.ReadPageContentAsync(runtime.Session.Current!, 1)).Objects;
        Assert.Equal(before.GetPage(1).Size.Width, actual[0].Bounds.Right, 3);
        Assert.Equal(original[0].Bounds.Y, actual[0].Bounds.Y); Assert.Equal(original[1].Bounds, actual[1].Bounds);
        await workspace.UndoCommand.ExecuteAsync(); Assert.Same(before, runtime.Session.Current);
    }
    [Fact]
    public async Task RegionAlignmentKeepsSpacingAndDisablesAfterRegionInvalidation()
    {
        await using var runtime = await Runtime.CreateAsync(); var view = runtime.View;
        using var workspace = new PdfWorkspace(new(view, runtime.Editor), new Dialogs(), runtime.Dispatch);
        workspace.ContentAlignmentReference = PdfContentAlignmentReference.Region;
        workspace.SelectContentObjects(workspace.ContentObjects.Take(2));
        Assert.False(workspace.AlignObjectsCommand.CanExecute(null));
        view.Tool = PdfTool.SelectRegion;
        Assert.True(view.BeginInteraction(runtime.Screen(new(5, 5))));
        await view.EndInteractionAsync(runtime.Screen(new(245, 160)));
        await workspace.SelectRegionObjectsCommand.ExecuteAsync(); runtime.Drain();
        var original = workspace.SelectedContentObjects.ToArray(); Assert.Equal(2, original.Length);
        var region = view.Selection!; Assert.True(workspace.AlignObjectsCommand.CanExecute(null));
        workspace.ContentAlignment = PdfSelectionAlignment.Bottom;
        await workspace.AlignObjectsCommand.ExecuteAsync(); runtime.Drain();
        var actual = (await runtime.Editor.ReadPageContentAsync(runtime.Session.Current!, 1)).Objects;
        Assert.Equal(region.Bounds.Bottom, PdfContentSelection.Bounds(actual.Take(2)).Bottom, 3);
        Assert.Equal(original[1].Bounds.Y - original[0].Bounds.Y, actual[1].Bounds.Y - actual[0].Bounds.Y, 3);
        Assert.False(workspace.AlignObjectsCommand.CanExecute(null));
        Assert.Null(view.Selection); Assert.Empty(view.SelectedContentObjects);
    }
    private static PdfPoint Center(PdfRect r) => new(r.X + r.Width / 2, r.Y + r.Height / 2);
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
        { var budget = 10000; while (_queue.TryDequeue(out var action)) { if (--budget == 0) throw new InvalidOperationException("Notification loop"); action(); } }
        public static async Task<Runtime> CreateAsync()
        {
            var runtime = new Runtime(); var doc = await runtime.Editor.CreateAsync(2, new(400, 600));
            doc = await runtime.Editor.ApplyAsync(doc, [new AddShape(1, new(20, 30, 80, 50), Fill: PdfColor.Blue),
                new AddShape(1, new(150, 95, 50, 40), Fill: new PdfColor(255, 0, 0)), new AddShape(1, new(260, 210, 30, 55), Fill: new PdfColor(0, 128, 0))]);
            using var stream = doc.OpenRead(); await runtime.Session.OpenAsync(stream); await runtime.View.LoadContentAsync(); runtime.View.Tool = PdfTool.EditObject; runtime.Drain(); return runtime;
        }
        public PdfPoint Screen(PdfPoint p)
        {
            var placement = PdfPageLayout.Create(Session.Current!.Pages, View.Viewport.Width, View.Zoom).Pages[0];
            return new(placement.Bounds.X + p.X * Scale - View.Offset.X, placement.Bounds.Y + p.Y * Scale - View.Offset.Y);
        }
        public async ValueTask DisposeAsync() { await View.DisposeAsync(); await Renderer.DisposeAsync(); }
    }
    private sealed class Dialogs : IPdfWorkspaceDialogs
    {
        public Task<string?> PickOpenPathAsync(PdfFileKind kind, CancellationToken token) => Task.FromResult<string?>(null);
        public Task<string?> PickSavePathAsync(string name, CancellationToken token) => Task.FromResult<string?>(null);
        public Task<string?> RequestPasswordAsync(CancellationToken token) => Task.FromResult<string?>(null);
        public Task<bool> ConfirmAsync(string title, string message, CancellationToken token) => Task.FromResult(false);
        public Task CopyTextAsync(string text, CancellationToken token) => Task.CompletedTask;
    }
}
