using System.Collections.Concurrent;
using ProPDF.Core;
using ProPDF.Editing;
using ProPDF.Engine.PdfPig;
using ProPDF.Presentation;
using ProPDF.Rendering.Skia;
using SkiaSharp;
using Xunit;

namespace ProPDF.Tests;

public sealed class ViewportSchedulingTests
{
    private static async Task OpenAsync(PdfSession session, ManagedPdfEditor editor, double edge = 3000)
    {
        var document = await editor.CreateAsync(1, new(edge, edge));
        using var input = document.OpenRead(); await session.OpenAsync(input);
    }

    [Fact]
    public async Task FortySmallScrollsReuseCompletedTileCoverageWithoutCacheRequests()
    {
        var backend = new PdfPigBackend(); var editor = new ManagedPdfEditor(backend); var session = new PdfSession(backend, editor);
        await using var renderer = new SkiaPdfRenderer(backend);
        await using var viewport = new PdfViewportController(session, renderer, backend);
        viewport.SetViewport(240, 220); await OpenAsync(session, editor);
        viewport.SetOffset(40, 40); await viewport.WaitForRenderingAsync();
        var before = await renderer.GetStatisticsAsync();
        for (var i = 0; i < 40; i++) { viewport.ScrollBy(1, 1); await viewport.WaitForRenderingAsync(); }
        var after = await renderer.GetStatisticsAsync();
        Assert.Equal(before.CacheHits, after.CacheHits); Assert.Equal(before.CacheMisses, after.CacheMisses);
        Assert.Equal(new PdfPoint(80, 80), viewport.Offset);
        using var scene = viewport.CaptureScene(); Assert.True(scene.TileCount > 0);
    }

    [Fact]
    public async Task SmallScrollKeepsAnInFlightPlanAlive()
    {
        var backend = new PdfPigBackend(); var editor = new ManagedPdfEditor(backend); var session = new PdfSession(backend, editor);
        using var factory = new DelayedFactory();
        await using var renderer = new SkiaPdfRenderer(factory);
        await using var viewport = new PdfViewportController(session, renderer, backend);
        viewport.SetViewport(240, 220); await OpenAsync(session, editor);
        try
        {
            await factory.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            viewport.ScrollBy(25, 25);
            Assert.False(factory.Token.IsCancellationRequested);
            Assert.True(viewport.IsRendering);
        }
        finally { factory.Release.Set(); await viewport.WaitForRenderingAsync(); }
        Assert.Null(viewport.LastError); Assert.Equal(1, (await renderer.GetStatisticsAsync()).CacheMisses);
    }

    [Fact]
    public async Task ProgressivePublicationKeepsPreviouslyVisibleTilesStillNeededByThePlan()
    {
        var backend = new PdfPigBackend(); var editor = new ManagedPdfEditor(backend); var session = new PdfSession(backend, editor);
        await using var renderer = new SkiaPdfRenderer(backend);
        // Observe each publication synchronously. xUnit's ambient context may
        // defer/coalesce notifications past the renderer completion await, which
        // would make this ownership test depend on unrelated dispatcher timing.
        // The queued-dispatcher contract is verified separately below.
        await using var viewport = new PdfViewportController(session, renderer, backend, action => action());
        viewport.SetViewport(900, 700); await OpenAsync(session, editor); await viewport.WaitForRenderingAsync();
        using var first = viewport.CaptureScene(); Assert.Equal(4, first.TileCount);
        var publications = new ConcurrentBag<int>();
        viewport.Invalidated += (_, _) => { using var scene = viewport.CaptureScene(); publications.Add(scene.TileCount); };
        viewport.ScrollBy(200, 0); await viewport.WaitForRenderingAsync();
        using var final = viewport.CaptureScene(); Assert.Equal(6, final.TileCount);
        Assert.Contains(4, publications); Assert.Contains(6, publications);
        Assert.All(publications, count => Assert.True(count >= 4, $"Progressive publication dropped to {count} tiles."));
    }

    [Fact]
    public async Task RenderingCompletionDoesNotRequireTheHostToDrainQueuedNotifications()
    {
        var queue = new ConcurrentQueue<Action>();
        var backend = new PdfPigBackend(); var editor = new ManagedPdfEditor(backend); var session = new PdfSession(backend, editor);
        await using var renderer = new SkiaPdfRenderer(backend);
        await using var viewport = new PdfViewportController(session, renderer, backend, queue.Enqueue);
        viewport.SetViewport(900, 700); await OpenAsync(session, editor);
        await viewport.WaitForRenderingAsync().WaitAsync(TimeSpan.FromSeconds(5));
        // Rendering can complete while a host intentionally holds its UI queue.
        Assert.Single(queue);
        Assert.True(queue.TryDequeue(out var initial)); initial();
        Assert.Empty(queue);
        using var first = viewport.CaptureScene(); Assert.Equal(4, first.TileCount);

        var publications = new List<int>();
        viewport.Invalidated += (_, _) => { using var scene = viewport.CaptureScene(); publications.Add(scene.TileCount); };
        viewport.ScrollBy(200, 0);
        await viewport.WaitForRenderingAsync().WaitAsync(TimeSpan.FromSeconds(5));
        using var rendered = viewport.CaptureScene(); Assert.Equal(6, rendered.TileCount);
        Assert.Empty(publications); Assert.Single(queue);
        Assert.True(queue.TryDequeue(out var publish)); publish();
        Assert.Equal(new[] { 6 }, publications); Assert.Empty(queue);
    }

    [Fact]
    public async Task DispatcherNotificationsCoalesceAndExposeTheLatestState()
    {
        var queue = new ConcurrentQueue<Action>();
        var backend = new PdfPigBackend(); var editor = new ManagedPdfEditor(backend); var session = new PdfSession(backend, editor);
        await using var renderer = new SkiaPdfRenderer(backend);
        await using var viewport = new PdfViewportController(session, renderer, backend, queue.Enqueue);
        var raised = 0; viewport.Invalidated += (_, _) => raised++;
        for (var i = 0; i < 100; i++) viewport.ToolText = i.ToString(System.Globalization.CultureInfo.InvariantCulture);
        Assert.Single(queue); Assert.Equal(0, raised);
        Assert.True(queue.TryDequeue(out var callback)); callback();
        Assert.Equal(1, raised); Assert.Equal("99", viewport.ToolText); Assert.Empty(queue);
        viewport.ToolText = "final"; Assert.Single(queue);
        await viewport.DisposeAsync(); Assert.True(queue.TryDequeue(out callback)); callback(); Assert.Equal(1, raised);
    }

    [Fact]
    public async Task BudgetFailureRecoversAndZoomOrRevisionChangesNeverReuseTheWrongTiles()
    {
        var backend = new PdfPigBackend(); var editor = new ManagedPdfEditor(backend); var session = new PdfSession(backend, editor);
        await using var renderer = new SkiaPdfRenderer(backend);
        await using var viewport = new PdfViewportController(session, renderer, backend);
        viewport.SetViewport(240, 220); await OpenAsync(session, editor); await viewport.WaitForRenderingAsync();
        viewport.SetViewport(16000, 16000, 8); await viewport.WaitForRenderingAsync();
        Assert.Contains("256-tile", viewport.LastError);
        viewport.SetViewport(240, 220); await viewport.WaitForRenderingAsync(); Assert.Null(viewport.LastError);
        var before = await renderer.GetStatisticsAsync(); viewport.SetZoom(2); await viewport.WaitForRenderingAsync();
        Assert.True((await renderer.GetStatisticsAsync()).CacheMisses > before.CacheMisses);
        before = await renderer.GetStatisticsAsync();
        await session.ApplyAsync(new AddShape(1, new(0, 0, 100, 100), Fill: new PdfColor(255, 0, 0)));
        await viewport.WaitForRenderingAsync(); Assert.Null(viewport.LastError);
        Assert.True((await renderer.GetStatisticsAsync()).CacheMisses > before.CacheMisses);
    }

    private sealed class DelayedFactory : ISkiaPdfDocumentFactory, IDisposable
    {
        public readonly TaskCompletionSource Entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public readonly ManualResetEventSlim Release = new();
        public CancellationToken Token;
        public ISkiaPdfDocument Open(PdfSnapshot snapshot) => new Document(this);
        public void Dispose() { Release.Set(); Release.Dispose(); }
        private sealed class Document(DelayedFactory owner) : ISkiaPdfDocument
        {
            public SKPicture RecordPage(int pageNumber, CancellationToken cancellationToken = default)
            {
                owner.Token = cancellationToken; owner.Entered.TrySetResult();
                if (!owner.Release.Wait(TimeSpan.FromSeconds(10), cancellationToken)) throw new TimeoutException("Test render was not released.");
                using var recorder = new SKPictureRecorder(); var canvas = recorder.BeginRecording(new(0, 0, 3000, 3000));
                canvas.Clear(SKColors.Red); return recorder.EndRecording();
            }
            public void Dispose() { }
        }
    }
}
