using System.Collections.Concurrent;
using ProPDF.Core;
using ProPDF.Editing;
using ProPDF.Engine.PdfPig;
using ProPDF.Presentation;
using ProPDF.Rendering.Skia;
using Xunit;

namespace ProPDF.Tests;

public sealed class TypographyWorkspaceTests
{
    [Theory]
    [InlineData("Helvetica")] [InlineData("Helvetica-Bold")] [InlineData("Helvetica-Oblique")] [InlineData("Helvetica-BoldOblique")]
    [InlineData("Courier")] [InlineData("Courier-Bold")] [InlineData("Courier-Oblique")] [InlineData("Courier-BoldOblique")]
    public async Task EveryExposedFaceWritesNativeFontAndExplicitLineSpacing(string face)
    {
        var reader = new PdfPigBackend(); var editor = new ManagedPdfEditor(reader);
        var source = await editor.CreateAsync();
        var result = await editor.ApplyAsync(source, [new AddTextBox(1, new(20, 20, 400, 120), "Alpha\nBeta", 20, 1.75, StandardFont: face)]);
        using var input = result.OpenRead(); using var pdf = UglyToad.PdfPig.PdfDocument.Open(input);
        var letters = pdf.GetPage(1).Letters;
        Assert.Equal("AlphaBeta", string.Concat(letters.Select(l => l.Value)));
        Assert.All(letters, l => Assert.Contains(face, l.FontName));
        Assert.Equal(35, Math.Abs(letters[0].StartBaseLine.Y - letters[5].StartBaseLine.Y), 4);
    }

    [Theory]
    [InlineData(0)] [InlineData(-1)] [InlineData(.49)] [InlineData(10.01)] [InlineData(double.NaN)] [InlineData(double.PositiveInfinity)]
    public async Task InvalidReplacementSpacingRejectsAtomically(double spacing)
    {
        await using var runtime = await Runtime.Create(); var original = runtime.Session.Current!;
        var item = Assert.Single(runtime.View.ContentObjects);
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => runtime.Session.ApplyAsync(
            new ReplaceContentText(item.Reference, new(20, 20, 300, 100), "Invalid", LineSpacing: spacing), original.Id));
        Assert.Same(original, runtime.Session.Current); Assert.False(runtime.Session.CanUndo);
    }

    [Fact]
    public async Task SharedReplacementUsesTypefaceAndSpacingAndUndoRestoresOriginal()
    {
        await using var runtime = await Runtime.Create(); var w = runtime.Workspace; var original = runtime.Session.Current!;
        var choices = w.ContentStandardFonts;
        w.SelectedContentObject = Assert.Single(w.ContentObjects);
        w.ContentStandardFont = "Courier-Bold"; w.ContentLineSpacing = "1.6"; w.ContentFontSize = "18";
        w.ContentWidth = "300"; w.ContentHeight = "90"; w.ContentText = "First line\nSecond line";
        await w.ReplaceObjectTextCommand.ExecuteAsync(); runtime.Drain();
        Assert.NotEqual(original.Id, runtime.Session.Current!.Id); Assert.Null(runtime.View.LastError);
        using (var input = runtime.Session.Current.OpenRead())
        using (var pdf = UglyToad.PdfPig.PdfDocument.Open(input))
        {
            var letters = pdf.GetPage(1).Letters;
            Assert.Equal("First lineSecond line", string.Concat(letters.Select(l => l.Value)));
            Assert.All(letters, l => Assert.Contains("Courier-Bold", l.FontName));
            Assert.Equal(28.8, Math.Abs(letters[0].StartBaseLine.Y - letters[10].StartBaseLine.Y), 4);
        }
        await w.UndoCommand.ExecuteAsync(); runtime.Drain(); Assert.Same(original, runtime.Session.Current);
        Assert.Same(choices, w.ContentStandardFonts); Assert.Equal("Courier-Bold", w.ContentStandardFont);
        Assert.Equal(1.6, runtime.View.TextBoxLineSpacing); Assert.Equal(18, runtime.View.TextBoxFontSize);
    }

    [Theory]
    [InlineData(PdfTool.Text)] [InlineData(PdfTool.TextBox)]
    public async Task PointerInsertionUsesTheSharedTypographyDraft(PdfTool tool)
    {
        await using var runtime = await Runtime.Create(); var w = runtime.Workspace; var view = runtime.View;
        w.ContentStandardFont = "Courier-Oblique"; w.ContentFontSize = "22"; w.ContentLineSpacing = "1.8";
        view.ToolText = tool == PdfTool.Text ? "Inserted" : "Inserted\nNew line"; view.Tool = tool;
        Assert.True(view.BeginInteraction(runtime.Screen(new(20, 200))));
        await view.EndInteractionAsync(runtime.Screen(new(350, 310))); runtime.Drain();
        using var input = runtime.Session.Current!.OpenRead(); using var pdf = UglyToad.PdfPig.PdfDocument.Open(input);
        var letters = pdf.GetPage(1).Letters.Where(l => (l.FontName ?? "").Contains("Courier-Oblique", StringComparison.Ordinal)).ToArray();
        Assert.Equal(tool == PdfTool.Text ? "Inserted" : "InsertedNew line", string.Concat(letters.Select(l => l.Value)));
        Assert.All(letters, l => Assert.Equal(22, l.PointSize, 3));
        if (tool == PdfTool.TextBox) Assert.Equal(39.6, Math.Abs(letters[0].StartBaseLine.Y - letters[8].StartBaseLine.Y), 4);
    }

    [Theory]
    [InlineData("NaN", "1.2")] [InlineData("not a number", "1.2")] [InlineData("18", "oops")]
    public async Task InvalidDraftNeverFallsBackSilentlyToLastValidInput(string size, string spacing)
    {
        await using var runtime = await Runtime.Create(); var w = runtime.Workspace; var original = runtime.Session.Current!;
        w.SelectedContentObject = Assert.Single(w.ContentObjects); w.ContentFontSize = size; w.ContentLineSpacing = spacing;
        w.ContentText = "Rejected";
        await w.ReplaceObjectTextCommand.ExecuteAsync(); runtime.Drain();
        Assert.Same(original, runtime.Session.Current); Assert.NotNull(runtime.View.LastError);
        runtime.View.Tool = PdfTool.TextBox; runtime.View.ToolText = "Rejected insertion";
        Assert.True(runtime.View.BeginInteraction(runtime.Screen(new(20, 200))));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => runtime.View.EndInteractionAsync(runtime.Screen(new(350, 310))));
        Assert.Same(original, runtime.Session.Current);
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
        private Runtime()
        {
            Editor = new(Reader); Session = new(Reader, Editor); Renderer = new(Reader);
            View = new(Session, Renderer, Reader, a => _queue.Enqueue(a)); View.SetViewport(600, 500);
            Workspace = new(new(View, Editor), new Dialogs(), a => _queue.Enqueue(a));
        }
        public static async Task<Runtime> Create()
        {
            var r = new Runtime(); var source = await r.Editor.CreateAsync(size: new(400, 600));
            source = await r.Editor.ApplyAsync(source, [new AddText(1, new(20, 60), "Original")]);
            using var input = source.OpenRead(); await r.Session.OpenAsync(input); r.Drain();
            await r.View.LoadContentAsync(); r.Drain(); return r;
        }
        public void Drain() { var budget = 10000; while (_queue.TryDequeue(out var a)) { if (--budget == 0) throw new InvalidOperationException("Notification loop"); a(); } }
        public PdfPoint Screen(PdfPoint point)
        {
            var page = PdfPageLayout.Create(Session.Current!.Pages, View.Viewport.Width, View.Zoom).Pages[0]; var scale = View.Zoom * 96 / 72;
            return new(page.Bounds.X + point.X * scale - View.Offset.X, page.Bounds.Y + point.Y * scale - View.Offset.Y);
        }
        public async ValueTask DisposeAsync() { Workspace.Dispose(); await View.DisposeAsync(); await Renderer.DisposeAsync(); }
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
