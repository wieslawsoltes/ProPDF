using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Rendering.SceneGraph;
using Avalonia.Skia;
using Avalonia.Threading;
using ProPDF.Core;
using ProPDF.Presentation;

namespace ProPDF.Avalonia;

/// <summary>A native Avalonia PDF surface. The application owns Controller and its dependencies.</summary>
public sealed class PdfView : Control
{
    public static readonly StyledProperty<PdfViewportController?> ControllerProperty =
        AvaloniaProperty.Register<PdfView, PdfViewportController?>(nameof(Controller));
    private bool _attached;
    private TopLevel? _root;
    public PdfViewportController? Controller { get => GetValue(ControllerProperty); set => SetValue(ControllerProperty, value); }

    public PdfView()
    {
        Focusable = true;
        ClipToBounds = true;
        AutomationProperties.SetName(this, "PDF document viewport");
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property != ControllerProperty) return;
        if (change.OldValue is PdfViewportController old) old.Invalidated -= ControllerInvalidated;
        if (_attached && Controller is { } current) current.Invalidated += ControllerInvalidated;
        UpdateViewport(Bounds.Size);
        InvalidateVisual();
    }
    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        _attached = true;
        _root = TopLevel.GetTopLevel(this);
        if (_root is not null) _root.ScalingChanged += ScalingChanged;
        if (Controller is { } controller) controller.Invalidated += ControllerInvalidated;
        UpdateViewport(Bounds.Size);
    }
    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        _attached = false;
        if (_root is not null) _root.ScalingChanged -= ScalingChanged;
        _root = null;
        if (Controller is { } controller) { controller.Invalidated -= ControllerInvalidated; controller.CancelInteraction(); }
        base.OnDetachedFromVisualTree(e);
    }
    private void ControllerInvalidated(object? sender, EventArgs e) => Dispatcher.UIThread.Post(() =>
    {
        if (!_attached) return;
        AutomationProperties.SetName(this, $"PDF document, page {Controller?.CurrentPage ?? 0} of {Controller?.PageCount ?? 0}");
        InvalidateVisual();
    });
    protected override Size ArrangeOverride(Size finalSize)
    {
        UpdateViewport(finalSize);
        return finalSize;
    }
    private void ScalingChanged(object? sender, EventArgs e) { UpdateViewport(Bounds.Size); InvalidateVisual(); }
    private void UpdateViewport(Size size)
    {
        if (size.Width > 0 && size.Height > 0) Controller?.SetViewport(size.Width, size.Height, TopLevel.GetTopLevel(this)?.RenderScaling ?? 1);
    }
    public override void Render(DrawingContext context)
    {
        base.Render(context);
        UpdateViewport(Bounds.Size);
        if (Controller is { } controller) context.Custom(new PdfDrawOperation(controller.CaptureScene()));
        else context.DrawRectangle(Brushes.GhostWhite, null, new Rect(Bounds.Size));
    }
    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        var properties = e.GetCurrentPoint(this).Properties;
        if (!properties.IsLeftButtonPressed && !properties.IsMiddleButtonPressed) return;
        Focus();
        var point = e.GetPosition(this);
        if (Controller?.BeginInteraction(new PdfPoint(point.X, point.Y), properties.IsMiddleButtonPressed, (e.KeyModifiers & (KeyModifiers.Control | KeyModifiers.Meta | KeyModifiers.Shift)) != 0) == true)
        {
            e.Pointer.Capture(this);
            e.Handled = true;
        }
    }
    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);
        if (e.Pointer.Captured != this) return;
        var point = e.GetPosition(this);
        Controller?.MoveInteraction(new PdfPoint(point.X, point.Y));
        e.Handled = true;
    }
    protected override async void OnPointerReleased(PointerReleasedEventArgs e)
    {
        base.OnPointerReleased(e);
        if (e.Pointer.Captured != this || Controller is not { } controller) return;
        var point = e.GetPosition(this);
        try { await controller.EndInteractionAsync(new PdfPoint(point.X, point.Y)); }
        catch (Exception error) { controller.ReportError(error); }
        finally { e.Pointer.Capture(null); }
        e.Handled = true;
    }
    protected override void OnPointerCaptureLost(PointerCaptureLostEventArgs e)
    {
        Controller?.CancelInteraction();
        base.OnPointerCaptureLost(e);
    }
    protected override void OnPointerWheelChanged(PointerWheelEventArgs e)
    {
        base.OnPointerWheelChanged(e);
        if (Controller is not { } controller) return;
        var point = e.GetPosition(this);
        if ((e.KeyModifiers & (KeyModifiers.Control | KeyModifiers.Meta)) != 0)
            controller.SetZoom(controller.Zoom * Math.Pow(1.15, e.Delta.Y), new PdfPoint(point.X, point.Y));
        else controller.ScrollBy(-e.Delta.X * 48, -e.Delta.Y * 48);
        e.Handled = true;
    }
    protected override async void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if (Controller is not { } controller) return;
        var command = (e.KeyModifiers & (KeyModifiers.Control | KeyModifiers.Meta)) != 0;
        try
        {
            switch (e.Key)
            {
                case Key.PageDown: controller.ScrollBy(0, controller.Viewport.Height * 0.85); break;
                case Key.PageUp: controller.ScrollBy(0, -controller.Viewport.Height * 0.85); break;
                case Key.Home: controller.GoToPage(1); break;
                case Key.End when controller.PageCount > 0: controller.GoToPage(controller.PageCount); break;
                case Key.Down: controller.ScrollBy(0, 48); break;
                case Key.Up: controller.ScrollBy(0, -48); break;
                case Key.Left: controller.ScrollBy(-48, 0); break;
                case Key.Right: controller.ScrollBy(48, 0); break;
                case Key.Add: case Key.OemPlus: controller.SetZoom(controller.Zoom * 1.15); break;
                case Key.Subtract: case Key.OemMinus: controller.SetZoom(controller.Zoom / 1.15); break;
                case Key.D0 when command: controller.FitPage(); break;
                case Key.Z when command: await controller.Session.UndoAsync(); break;
                case Key.Y when command: await controller.Session.RedoAsync(); break;
                case Key.C when command:
                    if (TopLevel.GetTopLevel(this)?.Clipboard is { } clipboard) await clipboard.SetTextAsync(controller.SelectedText);
                    break;
                case Key.Escape: controller.CancelInteraction(); controller.ClearSelection(); break;
                default: return;
            }
            e.Handled = true;
        }
        catch (Exception error) { controller.ReportError(error); }
    }
}

internal sealed class PdfDrawOperation(PdfScene scene) : ICustomDrawOperation
{
    public Rect Bounds { get; } = new(0, 0, scene.Viewport.Width, scene.Viewport.Height);
    public bool HitTest(Point point) => Bounds.Contains(point);
    public bool Equals(ICustomDrawOperation? other) => ReferenceEquals(this, other);
    public void Render(ImmediateDrawingContext context)
    {
        var feature = context.TryGetFeature<ISkiaSharpApiLeaseFeature>()
            ?? throw new NotSupportedException("ProPDF.Avalonia requires the Avalonia Skia renderer. Configure UseSkia().");
        using var lease = feature.Lease();
        scene.Draw(lease.SkCanvas);
    }
    public void Dispose() => scene.Dispose();
}
