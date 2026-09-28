using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using ProPDF.Core;
using ProPDF.Presentation;

namespace ProPDF.Wpf;

public sealed class PdfThumbnail : FrameworkElement
{
    public static readonly DependencyProperty ControllerProperty = DependencyProperty.Register(nameof(Controller), typeof(PdfViewportController), typeof(PdfThumbnail), new PropertyMetadata(null, Changed));
    public static readonly DependencyProperty PageNumberProperty = DependencyProperty.Register(nameof(PageNumber), typeof(int), typeof(PdfThumbnail), new PropertyMetadata(1, Changed));
    private CancellationTokenSource? _request;
    private PdfScene? _scene;
    private WriteableBitmap? _bitmap;
    private long _generation;
    public PdfViewportController? Controller { get => (PdfViewportController?)GetValue(ControllerProperty); set => SetValue(ControllerProperty, value); }
    public int PageNumber { get => (int)GetValue(PageNumberProperty); set => SetValue(PageNumberProperty, value); }
    /// <summary>Pixels in the currently retained thumbnail, excluding allocation overhead.</summary>
    public long RasterPixelCount => _scene?.RasterPixelCount ?? 0;
    public PdfThumbnail()
    {
        Width = 152; Height = 192;
        Loaded += (_, _) => { if (Controller is { } controller) controller.Session.Changed += DocumentChanged; Reload(); };
        Unloaded += (_, _) => { if (Controller is { } controller) controller.Session.Changed -= DocumentChanged; Reload(); };
    }
    private static void Changed(DependencyObject target, DependencyPropertyChangedEventArgs e)
    {
        var view = (PdfThumbnail)target;
        if (e.Property == ControllerProperty)
        {
            if (e.OldValue is PdfViewportController old) old.Session.Changed -= view.DocumentChanged;
            if (view.IsLoaded && view.Controller is { } controller) controller.Session.Changed += view.DocumentChanged;
        }
        view.Reload();
    }
    private void DocumentChanged(object? sender, PdfSessionChangedEventArgs e)
    {
        if (e.Kind != PdfChangeKind.Saved) Dispatcher.BeginInvoke(new Action(Reload));
    }
    private async void Reload()
    {
        var generation = ++_generation;
        try { _request?.Cancel(); } catch (ObjectDisposedException) { }
        _scene?.Dispose(); _scene = null; _bitmap = null;
        InvalidateVisual();
        if (!IsLoaded || Controller is not { Document: { } document } controller || PageNumber < 1 || PageNumber > document.Pages.Count) return;
        var request = new CancellationTokenSource();
        _request = request;
        try
        {
            var scene = await controller.CreateThumbnailAsync(PageNumber, 152, 192, VisualTreeHelper.GetDpi(this).DpiScaleX, request.Token);
            if (!IsLoaded || generation != _generation) scene.Dispose();
            else { _scene = scene; InvalidateVisual(); }
        }
        catch (OperationCanceledException) { }
        catch (ObjectDisposedException) { }
        catch (Exception error) { controller.ReportError(error); }
        finally { request.Dispose(); }
    }
    protected override void OnDpiChanged(DpiScale oldDpi, DpiScale newDpi)
    { base.OnDpiChanged(oldDpi, newDpi); Reload(); }
    protected override void OnRender(DrawingContext context)
    {
        base.OnRender(context);
        if (_scene is not null) SkiaWpfSurface.Draw(this, context, _scene, ref _bitmap);
        else context.DrawRectangle(Brushes.WhiteSmoke, null, new Rect(0, 0, ActualWidth, ActualHeight));
    }
}
