using ProPDF.Core;
using ProPDF.Editing;
using ProPDF.Engine.PdfPig;
using ProPDF.Presentation;
using ProPDF.Rendering.Skia;
using SkiaSharp;
using Xunit;

namespace ProPDF.Tests;

public sealed class DisplayDensityTests
{
    private static async Task<PdfSession> Session(PdfPigBackend backend)
    {
        var editor = new ManagedPdfEditor(backend); var doc = await editor.CreateAsync(size:new(400,600));
        var session = new PdfSession(backend,editor); using var input = doc.OpenRead(); await session.OpenAsync(input); return session;
    }

    [Theory]
    [InlineData(1.25)] [InlineData(1.5)] [InlineData(2)] [InlineData(3)]
    public async Task ThumbnailDensityIncreasesRasterResolutionWithoutChangingDipGeometry(double density)
    {
        var backend=new PdfPigBackend(); var session=await Session(backend);
        await using var renderer=new SkiaPdfRenderer(backend);
        await using var viewport=new PdfViewportController(session,renderer,backend, a=>a());
        await viewport.WaitForRenderingAsync();
        using var one=await viewport.CreateThumbnailAsync(1,160,200);
        using var high=await viewport.CreateThumbnailAsync(1,160,200,density);
        Assert.Equal(one.Viewport,high.Viewport);
        Assert.InRange((double)high.RasterPixelCount / one.RasterPixelCount,density*density*.98,density*density*1.02);
        var before=await renderer.GetStatisticsAsync();
        using var repeated=await viewport.CreateThumbnailAsync(1,160,200,density);
        var after=await renderer.GetStatisticsAsync();
        Assert.Equal(before.CacheMisses,after.CacheMisses); Assert.Equal(before.CacheHits+repeated.TileCount,after.CacheHits);
        Assert.Equal(1,after.DisplayListCount); Assert.Equal(1,after.OpenDocumentCount);
    }

    [Fact]
    public async Task LargestThumbnailUsesBoundedTilesAndRetainedSceneSurvivesDisposal()
    {
        var backend=new PdfPigBackend(); var session=await Session(backend);
        var renderer=new SkiaPdfRenderer(backend); var viewport=new PdfViewportController(session,renderer,backend,a=>a());
        using var high=await viewport.CreateThumbnailAsync(1,512,512,8);
        Assert.InRange(high.TileCount,2,64); Assert.InRange(high.RasterPixelCount,1,512L*512*64);
        await viewport.DisposeAsync(); await renderer.DisposeAsync();
        using var surface=SKSurface.Create(new SKImageInfo(512,512)); high.Draw(surface.Canvas);
        using var image=surface.Snapshot();using var pixels=SKBitmap.FromImage(image); Assert.Equal(SKColors.White,pixels.GetPixel(256,256));
    }

    [Theory]
    [InlineData(0)] [InlineData(.49)] [InlineData(8.01)] [InlineData(double.NaN)] [InlineData(double.PositiveInfinity)]
    public async Task InvalidThumbnailDensityFailsBeforeRendering(double density)
    {
        var backend=new PdfPigBackend();var session=await Session(backend);
        await using var renderer=new SkiaPdfRenderer(backend);await using var viewport=new PdfViewportController(session,renderer,backend,a=>a());
        await viewport.WaitForRenderingAsync(); var before=await renderer.GetStatisticsAsync();
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(()=>viewport.CreateThumbnailAsync(1,160,200,density));
        Assert.Equal(before.CacheMisses,(await renderer.GetStatisticsAsync()).CacheMisses);
    }

    [Fact]
    public async Task DensityOnlyChangeReplansTilesWithoutChangingZoomOffsetOrDocument()
    {
        var backend=new PdfPigBackend();var session=await Session(backend);
        await using var renderer=new SkiaPdfRenderer(backend);await using var viewport=new PdfViewportController(session,renderer,backend,a=>a());
        viewport.SetViewport(650,900,1); await viewport.WaitForRenderingAsync();
        using var one=viewport.CaptureScene(); var zoom=viewport.Zoom;var offset=viewport.Offset;var id=session.Current!.Id;
        viewport.SetViewport(650,900,2); await viewport.WaitForRenderingAsync(); using var high=viewport.CaptureScene();
        Assert.Equal(2,viewport.PixelsPerDip); Assert.Equal(zoom,viewport.Zoom); Assert.Equal(offset,viewport.Offset);Assert.Equal(id,session.Current.Id);
        Assert.InRange((double)high.RasterPixelCount/one.RasterPixelCount,3.98,4.02);
        Assert.Equal(1,(await renderer.GetStatisticsAsync()).DisplayListCount);
        viewport.SetViewport(650,900,1.25);await viewport.WaitForRenderingAsync();Assert.Equal(1.25,viewport.PixelsPerDip);Assert.Null(viewport.LastError);
    }

    [Fact]
    public async Task CancelledHighDensityThumbnailNeverReturnsPartialScene()
    {
        var backend=new PdfPigBackend();var session=await Session(backend);
        await using var renderer=new SkiaPdfRenderer(backend);await using var viewport=new PdfViewportController(session,renderer,backend,a=>a());
        await viewport.WaitForRenderingAsync(); var before=await renderer.GetStatisticsAsync();
        using var cancelled=new CancellationTokenSource();cancelled.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(()=>viewport.CreateThumbnailAsync(1,512,512,8,cancelled.Token));
        Assert.Equal(before.TileCount,(await renderer.GetStatisticsAsync()).TileCount);
    }

    [Fact]
    public async Task FractionalEdgeTileDoesNotSqueezeItsIntegerPaddingIntoTheDocument()
    {
        var backend=new PdfPigBackend();var session=await Session(backend);
        await using var renderer=new SkiaPdfRenderer(new StripeFactory());
        using var tile=await renderer.RenderTileAsync(session.Current!,new(1,new(0,0,3.25,4),1));
        Assert.Equal(4,tile.Image.Width);
        using var scene=new PdfScene(new(3.25,4),[],[new PdfTileVisual(tile.Retain(),new(0,0,3.25,4))],[]);
        using var surface=SKSurface.Create(new SKImageInfo(13,16));surface.Canvas.Scale(4);scene.Draw(surface.Canvas);
        using var image=surface.Snapshot();using var pixels=SKBitmap.FromImage(image);
        // The black source pixel is centered at 1.5 points -> 6 output pixels.
        // Stretching all four allocated pixels into 3.25 points shifts/squeezes it left.
        Assert.True(pixels.GetPixel(6,6).Red<45,$"Unexpected stripe phase: {pixels.GetPixel(6,6)}");
        Assert.True(pixels.GetPixel(3,6).Red>120);
    }

    private sealed class StripeFactory : ISkiaPdfDocumentFactory
    {
        public ISkiaPdfDocument Open(PdfSnapshot snapshot)=>new Doc();
        private sealed class Doc:ISkiaPdfDocument
        {
            public SKPicture RecordPage(int number,CancellationToken cancellationToken=default)
            {
                using var recorder=new SKPictureRecorder();var canvas=recorder.BeginRecording(new(0,0,400,600));
                canvas.Clear(SKColors.White);using var paint=new SKPaint { Color=SKColors.Black, IsAntialias=false };canvas.DrawRect(1,0,1,4,paint);return recorder.EndRecording();
            }
            public void Dispose() { }
        }
    }
}
