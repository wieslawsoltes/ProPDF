using ProPDF.Core;
using ProPDF.Engine.PdfPig;
using ProPDF.Editing.iText;
using ProPDF.Presentation;
using ProPDF.Rendering.Skia;
using Xunit;

namespace ProPDF.Tests;

public sealed class InteractionRegressionTests
{
    [Fact]
    public async Task OlderSearchCannotReplaceNewerResults()
    {
        var backend = new PdfPigBackend();
        var editor = new ITextPdfEditor(backend);
        var document = await editor.CreateAsync(2);
        var session = new PdfSession(backend, editor);
        using (var input = document.OpenRead()) await session.OpenAsync(input);
        var text = new ControlledSearch();
        await using var renderer = new SkiaPdfRenderer(backend);
        await using var viewport = new PdfViewportController(session, renderer, text);
        var first = viewport.SearchAsync("old");
        var second = viewport.SearchAsync("new");
        text.New.SetResult(new[] { new PdfSearchHit(2, 0, 3, "new", new[] { new PdfRect(20, 30, 50, 14) }) });
        await second;
        text.Old.SetResult(new[] { new PdfSearchHit(1, 0, 3, "old", new[] { new PdfRect(20, 30, 50, 14) }) });
        await first;
        Assert.Equal("new", Assert.Single(viewport.SearchHits).Text);
        Assert.Equal(2, viewport.CurrentPage);
        await viewport.SearchAsync("");
        Assert.Empty(viewport.SearchHits);
    }

    [Theory]
    [InlineData(120, 80)]
    [InlineData(80, 120)]
    public async Task AxisAlignedInkIsNotDiscarded(double endX, double endY)
    {
        var backend = new PdfPigBackend();
        var editor = new ITextPdfEditor(backend);
        var document = await editor.CreateAsync(size: new PdfSize(300, 400));
        var session = new PdfSession(backend, editor);
        using (var input = document.OpenRead()) await session.OpenAsync(input);
        await using var renderer = new SkiaPdfRenderer(backend);
        await using var viewport = new PdfViewportController(session, renderer, backend);
        viewport.SetViewport(440, 600);
        viewport.Tool = PdfTool.Ink;
        Assert.True(viewport.BeginInteraction(new PdfPoint(80, 80)));
        await viewport.EndInteractionAsync(new PdfPoint(endX, endY));
        Assert.Equal("Ink", Assert.Single((await editor.InspectAsync(session.Current!)).Annotations).Kind);
        Assert.True(session.CanUndo);
    }

    [Theory]
    [InlineData(PdfLayoutMode.Continuous)]
    [InlineData(PdfLayoutMode.Facing)]
    [InlineData(PdfLayoutMode.SinglePage)]
    public async Task SearchTargetSurvivesScrollClamping(PdfLayoutMode mode)
    {
        var backend = new PdfPigBackend();
        var editor = new ITextPdfEditor(backend);
        var document = await editor.CreateAsync(3, new PdfSize(400, 600));
        document = await editor.ApplyAsync(document, new IPdfEditOperation[]
        {
            new AddText(1, new PdfPoint(30, 60), "Target"),
            new AddText(2, new PdfPoint(30, 60), "Target"),
            new AddText(3, new PdfPoint(30, 60), "Target")
        });
        var session = new PdfSession(backend, editor);
        using (var input = document.OpenRead()) await session.OpenAsync(input);
        await using var renderer = new SkiaPdfRenderer(backend);
        await using var viewport = new PdfViewportController(session, renderer, backend);
        viewport.SetLayoutMode(mode);
        await viewport.SearchAsync("Target");
        viewport.NextSearchResult();
        Assert.Equal(2, viewport.CurrentPage);
        viewport.NextSearchResult();
        Assert.Equal(3, viewport.CurrentPage);
        viewport.NextSearchResult();
        Assert.Equal(1, viewport.CurrentPage);
        viewport.NextSearchResult(true);
        Assert.Equal(3, viewport.CurrentPage);
    }

    private sealed class ControlledSearch : IPdfTextService
    {
        public TaskCompletionSource<IReadOnlyList<PdfSearchHit>> Old { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource<IReadOnlyList<PdfSearchHit>> New { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public Task<PdfTextPage> GetPageTextAsync(PdfSnapshot source, int pageNumber, CancellationToken cancellationToken = default) =>
            Task.FromResult(new PdfTextPage(pageNumber, "", Array.Empty<PdfWord>()));
        public Task<IReadOnlyList<PdfSearchHit>> SearchAsync(PdfSnapshot source, string query, PdfSearchOptions? options = null, CancellationToken cancellationToken = default) =>
            query == "old" ? Old.Task : New.Task;
    }
}
