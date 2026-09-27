using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Threading;
using ProPDF.Core;
using ProPDF.Presentation;

namespace ProPDF.Avalonia;

/// <summary>Lazy page thumbnail. Native resources are released when an item leaves the visual tree.</summary>
public sealed class PdfThumbnail : Control
{
    public static readonly StyledProperty<PdfViewportController?> ControllerProperty = AvaloniaProperty.Register<PdfThumbnail, PdfViewportController?>(nameof(Controller));
    public static readonly StyledProperty<int> PageNumberProperty = AvaloniaProperty.Register<PdfThumbnail, int>(nameof(PageNumber), 1);
    private CancellationTokenSource? _request;
    private PdfScene? _scene;
    private bool _attached;
    private long _generation;
    public PdfViewportController? Controller { get => GetValue(ControllerProperty); set => SetValue(ControllerProperty, value); }
    public int PageNumber { get => GetValue(PageNumberProperty); set => SetValue(PageNumberProperty, value); }
    public PdfThumbnail() { Width = 152; Height = 192; ClipToBounds = true; }
    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == ControllerProperty)
        {
            if (change.OldValue is PdfViewportController old) old.Session.Changed -= DocumentChanged;
            if (_attached && Controller is { } current) current.Session.Changed += DocumentChanged;
        }
        if (change.Property == ControllerProperty || change.Property == PageNumberProperty) Reload();
    }
    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        _attached = true;
        if (Controller is { } controller) controller.Session.Changed += DocumentChanged;
        Reload();
    }
    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        _attached = false;
        if (Controller is { } controller) controller.Session.Changed -= DocumentChanged;
        Reload();
        base.OnDetachedFromVisualTree(e);
    }
    private void DocumentChanged(object? sender, PdfSessionChangedEventArgs e)
    {
        if (e.Kind != PdfChangeKind.Saved) Dispatcher.UIThread.Post(Reload);
    }
    private async void Reload()
    {
        var generation = ++_generation;
        try { _request?.Cancel(); } catch (ObjectDisposedException) { }
        _scene?.Dispose();
        _scene = null;
        InvalidateVisual();
        if (!_attached || Controller is not { Document: { } document } controller || PageNumber < 1 || PageNumber > document.Pages.Count) return;
        var request = new CancellationTokenSource();
        _request = request;
        try
        {
            var scene = await controller.CreateThumbnailAsync(PageNumber, 152, 192, request.Token);
            if (!_attached || generation != _generation) scene.Dispose();
            else { _scene = scene; InvalidateVisual(); }
        }
        catch (OperationCanceledException) { }
        catch (ObjectDisposedException) { }
        catch (Exception error) { controller.ReportError(error); }
        finally { request.Dispose(); }
    }
    public override void Render(DrawingContext context)
    {
        base.Render(context);
        if (_scene is not null) context.Custom(new PdfDrawOperation(_scene.Retain()));
        else context.DrawRectangle(Brushes.WhiteSmoke, null, new Rect(Bounds.Size));
    }
}
