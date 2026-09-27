using System.Collections.Concurrent;
using ProPDF.Core;
using ProPDF.Editing;
using ProPDF.Engine.PdfPig;
using ProPDF.Presentation;
using ProPDF.Rendering.Skia;
using SkiaSharp;
using Xunit;

namespace ProPDF.Tests;

public sealed class AppearanceWorkspaceTests
{
    [Fact]
    public async Task AppearanceDraftSurvivesViewportChangesAndResetsForDifferentSelection()
    {
        await using var app = await Runtime.Create(); var workspace = app.Workspace;
        workspace.SelectedContentObject = workspace.ContentObjects.Single(o => o.Kind == PdfContentObjectKind.Path);
        workspace.ContentFillColor = "#FF4400"; workspace.ContentOpacity = "45";
        var choices = workspace.ContentBlendModes;
        app.View.SetZoom(1.2); await app.View.WaitForRenderingAsync(); app.Drain();
        Assert.Equal("#FF4400", workspace.ContentFillColor); Assert.Equal("45", workspace.ContentOpacity);
        Assert.Same(choices, workspace.ContentBlendModes);
        workspace.SelectedContentObject = workspace.ContentObjects.Single(o => o.Kind == PdfContentObjectKind.Image);
        Assert.Equal("", workspace.ContentFillColor); Assert.Equal("", workspace.ContentOpacity);
        Assert.False(workspace.CanSetContentColors); Assert.False(workspace.CanSetContentPainting);
        Assert.True(workspace.ReplaceImageCommand.CanExecute(null)); Assert.Contains("8 × 4", workspace.ContentImageSummary);
    }

    [Fact]
    public async Task SharedAppearanceCommandWritesNativeColorOpacityAndSupportsUndo()
    {
        await using var app = await Runtime.Create(); var workspace = app.Workspace; var before = app.Session.Current!;
        workspace.SelectedContentObject = workspace.ContentObjects.Single(o => o.Kind == PdfContentObjectKind.Path);
        workspace.ContentFillColor = "#FF000080"; workspace.ContentOpacity = "50";
        await workspace.ApplyAppearanceCommand.ExecuteAsync(); app.Drain();
        Assert.Null(app.View.LastError); Assert.NotEqual(before.Id, app.Session.Current!.Id); Assert.Null(workspace.SelectedContentObject);
        using var image = await new PdfRasterExporter(app.Renderer).RenderPageAsync(app.Session.Current, 1, new(Dpi: 72));
        using var pixels = SKBitmap.FromImage(image); var color = pixels.GetPixel(60, 60);
        Assert.Equal(255, color.Red); Assert.InRange((int)color.Green, 189, 193);
        await workspace.UndoCommand.ExecuteAsync(); app.Drain(); Assert.Same(before, app.Session.Current);
    }

    [Theory]
    [InlineData("color", "not-a-color")]
    [InlineData("opacity", "NaN")]
    [InlineData("opacity", "101")]
    [InlineData("width", "-1")]
    [InlineData("dash", "0 0")]
    [InlineData("blend", "Unknown")]
    [InlineData("empty", "")]
    public async Task InvalidAppearanceInputDoesNotPublishAnEdit(string kind, string value)
    {
        await using var app = await Runtime.Create(); var workspace = app.Workspace; var before = app.Session.Current;
        workspace.SelectedContentObject = workspace.ContentObjects.Single(o => o.Kind == PdfContentObjectKind.Path);
        switch (kind)
        {
            case "color": workspace.ContentFillColor = value; break;
            case "opacity": workspace.ContentOpacity = value; break;
            case "width": workspace.ContentLineWidth = value; break;
            case "dash": workspace.ContentDash = value; break;
            case "blend": workspace.ContentBlend = value; break;
        }
        await workspace.ApplyAppearanceCommand.ExecuteAsync(); app.Drain();
        Assert.Same(before, app.Session.Current); Assert.False(app.Session.CanUndo); Assert.False(string.IsNullOrEmpty(app.View.LastError));
    }

    [Fact]
    public async Task ImageCommandImportsOneSelectionAndInterpolationRoundTrips()
    {
        await using var app = await Runtime.Create(); var workspace = app.Workspace;
        var path = Path.Combine(Path.GetTempPath(), $"propdf-image-{Guid.NewGuid():N}.png");
        try
        {
            await File.WriteAllBytesAsync(path, Image(SKColors.Red, 16, 10).ToArray()); app.Dialogs.Open = _ => Task.FromResult<string?>(path);
            workspace.SelectedContentObject = workspace.ContentObjects.Single(o => o.Kind == PdfContentObjectKind.Image);
            workspace.ContentImageInterpolation = true;
            await workspace.ReplaceImageCommand.ExecuteAsync(); app.Drain(); await app.View.LoadContentAsync(); app.Drain();
            var item = workspace.ContentObjects.Single(o => o.Kind == PdfContentObjectKind.Image);
            Assert.Equal(16, item.ImageInfo!.PixelWidth); Assert.Equal(10, item.ImageInfo.PixelHeight); Assert.True(item.ImageInfo.Interpolate);
            workspace.SelectedContentObject = item; Assert.True(workspace.ContentImageInterpolation);
            workspace.ContentImageInterpolation = false; await workspace.ApplyImageInterpolationCommand.ExecuteAsync(); app.Drain();
            var after = (await app.Editor.ReadPageContentAsync(app.Session.Current!, 1)).Objects.Single(o => o.Kind == PdfContentObjectKind.Image);
            Assert.False(after.ImageInfo!.Interpolate); Assert.Equal(item.Bounds, after.Bounds); Assert.Equal(16, after.ImageInfo.PixelWidth);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public async Task DelayedImagePickerCannotApplyToAChangedDocument()
    {
        await using var app = await Runtime.Create(); var workspace = app.Workspace;
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var picked = new TaskCompletionSource<string?>(TaskCreationOptions.RunContinuationsAsynchronously);
        app.Dialogs.Open = _ => { entered.SetResult(); return picked.Task; };
        workspace.SelectedContentObject = workspace.ContentObjects.Single(o => o.Kind == PdfContentObjectKind.Image);
        var edit = workspace.ReplaceImageCommand.ExecuteAsync(); await entered.Task;
        await app.Session.ApplyAsync(new SetDocumentMetadata(new("Different revision"))); app.Drain();
        var revision = app.Session.Current;
        var path = Path.Combine(Path.GetTempPath(), $"propdf-stale-{Guid.NewGuid():N}.png");
        try
        {
            await File.WriteAllBytesAsync(path, Image(SKColors.Red).ToArray()); picked.SetResult(path); await edit; app.Drain();
            Assert.Same(revision, app.Session.Current); Assert.Contains("changed", app.View.LastError);
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public async Task CancelledPickerDoesNotChangePdfOrHistory()
    {
        await using var app = await Runtime.Create(); var workspace = app.Workspace; var revision = app.Session.Current;
        workspace.SelectedContentObject = workspace.ContentObjects.Single(o => o.Kind == PdfContentObjectKind.Image);
        await workspace.ReplaceImageCommand.ExecuteAsync(); app.Drain();
        Assert.Same(revision, app.Session.Current); Assert.False(app.Session.CanUndo);
    }

    private static PdfBinaryAsset Image(SKColor color, int width = 8, int height = 4)
    {
        using var bitmap = new SKBitmap(width, height); bitmap.Erase(color);
        using var image = SKImage.FromBitmap(bitmap); using var data = image.Encode(SKEncodedImageFormat.Png, 100); return new(data.ToArray());
    }
    private sealed class Runtime : IAsyncDisposable
    {
        private readonly ConcurrentQueue<Action> _queue = new();
        public PdfPigBackend Reader { get; } = new();
        public ManagedPdfEditor Editor { get; }
        public PdfSession Session { get; }
        public SkiaPdfRenderer Renderer { get; }
        public PdfViewportController View { get; }
        public PdfWorkspace Workspace { get; }
        public Dialogs Dialogs { get; } = new();
        private Runtime()
        {
            Editor = new(Reader); Session = new(Reader, Editor); Renderer = new(Reader);
            View = new(Session, Renderer, Reader, _queue.Enqueue);
            Workspace = new(new(View, Editor), Dialogs, _queue.Enqueue);
        }
        public void Drain() { var count = 0; while (_queue.TryDequeue(out var action)) { Assert.True(++count < 10000); action(); } }
        public static async Task<Runtime> Create()
        {
            var app = new Runtime(); var pdf = await app.Editor.CreateAsync(size: new(240, 240));
            pdf = await app.Editor.ApplyAsync(pdf, [new AddShape(1, new(20, 20, 80, 80), Fill: PdfColor.Blue),
                new AddText(1, new(20, 150), "Appearance test"), new AddImage(1, new(140, 20, 60, 40), Image(SKColors.Blue))]);
            using (var input = pdf.OpenRead()) await app.Session.OpenAsync(input);
            app.Drain(); await app.Workspace.EditObjectsCommand.ExecuteAsync(); app.Drain(); return app;
        }
        public async ValueTask DisposeAsync() { Workspace.Dispose(); await View.DisposeAsync(); await Renderer.DisposeAsync(); }
    }
    private sealed class Dialogs : IPdfWorkspaceDialogs
    {
        public Func<CancellationToken, Task<string?>> Open { get; set; } = _ => Task.FromResult<string?>(null);
        public Task<string?> PickOpenPathAsync(PdfFileKind kind, CancellationToken cancellationToken) => Open(cancellationToken);
        public Task<string?> PickSavePathAsync(string suggestedName, CancellationToken cancellationToken) => Task.FromResult<string?>(null);
        public Task<string?> RequestPasswordAsync(CancellationToken cancellationToken) => Task.FromResult<string?>(null);
        public Task<bool> ConfirmAsync(string title, string message, CancellationToken cancellationToken) => Task.FromResult(false);
        public Task CopyTextAsync(string text, CancellationToken cancellationToken) => Task.CompletedTask;
    }
}
