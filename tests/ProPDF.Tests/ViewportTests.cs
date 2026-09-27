using ProPDF.Core;
using ProPDF.Editing;
using ProPDF.Engine.PdfPig;
using ProPDF.Presentation;
using ProPDF.Rendering.Skia;
using SkiaSharp;
using Xunit;

namespace ProPDF.Tests;

public sealed class ViewportTests
{
    private static async Task<(PdfSession Session, PdfPigBackend Backend)> CreateAsync()
    {
        var backend = new PdfPigBackend();
        var editor = new ManagedPdfEditor(backend);
        var document = await editor.CreateAsync(3, new PdfSize(400, 600));
        document = await editor.ApplyAsync(document, [
            new AddShape(1, new PdfRect(20, 30, 80, 60), Fill: new PdfColor(255, 0, 0)),
            new AddText(1, new PdfPoint(30, 150), "Shared viewport one"),
            new AddText(2, new PdfPoint(30, 150), "Shared viewport two"),
            new AddText(3, new PdfPoint(30, 150), "Shared viewport three")]);
        var session = new PdfSession(backend, editor);
        using var input = document.OpenRead();
        await session.OpenAsync(input);
        return (session, backend);
    }

    [Fact]
    public async Task ZoomPreservesPointUnderPointerAndOffsetIsClamped()
    {
        var (session, backend) = await CreateAsync();
        await using var renderer = new SkiaPdfRenderer(backend);
        await using var controller = new PdfViewportController(session, renderer, backend);
        controller.SetViewport(600, 500);
        var anchor = new PdfPoint(200, 200);
        var before = controller.HitTest(anchor)!.Value;
        controller.SetZoom(2, anchor);
        var after = controller.HitTest(anchor)!.Value;
        Assert.Equal(before.PageNumber, after.PageNumber);
        Assert.Equal(before.Point.X, after.Point.X, 6);
        Assert.Equal(before.Point.Y, after.Point.Y, 6);
        controller.SetOffset(1e9, 1e9);
        Assert.Equal(controller.Extent.Height - controller.Viewport.Height, controller.Offset.Y, 6);
        controller.GoToPage(2);
        Assert.Equal(2, controller.CurrentPage);
    }

    [Fact]
    public async Task SceneOwnsResourcesBeyondControllerAndRendererLifetime()
    {
        var (session, backend) = await CreateAsync();
        var renderer = new SkiaPdfRenderer(backend);
        var controller = new PdfViewportController(session, renderer, backend);
        controller.SetViewport(600, 500);
        await controller.WaitForRenderingAsync();
        Assert.Null(controller.LastError);
        using var scene = controller.CaptureScene();
        Assert.True(scene.TileCount > 0);
        Assert.InRange(scene.PageCount, 1, 2);
        using var retained = scene.Retain();
        await controller.DisposeAsync();
        await renderer.DisposeAsync();
        using var surface = SKSurface.Create(new SKImageInfo(600, 500));
        retained.Draw(surface.Canvas);
        using var image = surface.Snapshot();
        using var pixels = SKBitmap.FromImage(image);
        Assert.True(pixels.GetPixel(90, 90).Red > 200 && pixels.GetPixel(90, 90).Green < 30);
    }

    [Fact]
    public async Task RedactionGestureOnlyStagesMarksUntilExplicitApply()
    {
        var (session, backend) = await CreateAsync();
        await using var renderer = new SkiaPdfRenderer(backend);
        await using var controller = new PdfViewportController(session, renderer, backend);
        controller.SetViewport(600, 500);
        controller.Tool = PdfTool.Redact;
        var revision = session.Current!.Id;
        Assert.True(controller.BeginInteraction(new PdfPoint(70, 70)));
        await controller.EndInteractionAsync(new PdfPoint(150, 130));
        Assert.Equal(1, controller.PendingRedactions);
        Assert.Equal(revision, session.Current.Id);
        Assert.False(session.IsDirty);
        controller.ClearRedactions();
        Assert.Equal(0, controller.PendingRedactions);
    }

    [Fact]
    public async Task SearchNavigatesAcrossPagesAndDocumentChangesClearResults()
    {
        var (session, backend) = await CreateAsync();
        await using var renderer = new SkiaPdfRenderer(backend);
        await using var controller = new PdfViewportController(session, renderer, backend);
        await controller.SearchAsync("Shared");
        Assert.Equal(3, controller.SearchHits.Count);
        Assert.Equal(1, controller.CurrentPage);
        controller.NextSearchResult();
        Assert.Equal(2, controller.CurrentPage);
        await session.ApplyAsync(new RotatePage(1));
        Assert.Empty(controller.SearchHits);
    }

    [Fact]
    public async Task ThumbnailIsBoundedAndLargeRequestsAreRejected()
    {
        var (session, backend) = await CreateAsync();
        await using var renderer = new SkiaPdfRenderer(backend);
        await using var controller = new PdfViewportController(session, renderer, backend);
        using var thumbnail = await controller.CreateThumbnailAsync(3);
        Assert.Equal(1, thumbnail.PageCount);
        Assert.Equal(1, thumbnail.TileCount);
        Assert.Equal(160, thumbnail.Viewport.Width);
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => controller.CreateThumbnailAsync(1, 2000, 2000));
    }

    [Fact]
    public async Task RapidViewportChangesPublishOnlyCurrentRevision()
    {
        var (session, backend) = await CreateAsync();
        await using var renderer = new SkiaPdfRenderer(backend);
        await using var controller = new PdfViewportController(session, renderer, backend);
        for (var i = 0; i < 20; i++) { controller.SetZoom(0.5 + i * 0.1); controller.GoToPage(i % 3 + 1); }
        await session.ApplyAsync(new AddText(1, new PdfPoint(20, 200), "New revision"));
        controller.GoToPage(1);
        await controller.WaitForRenderingAsync();
        Assert.Equal(session.Current!.Id, controller.Document!.Id);
        Assert.False(controller.IsRendering);
        Assert.Null(controller.LastError);
        using var scene = controller.CaptureScene();
        Assert.True(scene.TileCount > 0);
    }
}
