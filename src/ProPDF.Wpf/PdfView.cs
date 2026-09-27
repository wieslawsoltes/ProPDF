using System.Windows;
using System.Windows.Automation;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using ProPDF.Core;
using ProPDF.Presentation;
using SkiaSharp;

namespace ProPDF.Wpf;

/// <summary>Native WPF surface. PDF interpretation occurs on the shared renderer worker, not on the UI thread.</summary>
public sealed class PdfView : FrameworkElement
{
    public static readonly DependencyProperty ControllerProperty = DependencyProperty.Register(nameof(Controller), typeof(PdfViewportController),
        typeof(PdfView), new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender, ControllerChanged));
    private WriteableBitmap? _bitmap;
    public PdfViewportController? Controller { get => (PdfViewportController?)GetValue(ControllerProperty); set => SetValue(ControllerProperty, value); }
    public PdfView()
    {
        Focusable = true;
        ClipToBounds = true;
        AutomationProperties.SetName(this, "PDF document viewport");
        Loaded += (_, _) => { Subscribe(); UpdateViewport(); };
        Unloaded += (_, _) => { Unsubscribe(); Controller?.CancelInteraction(); _bitmap = null; };
    }
    private static void ControllerChanged(DependencyObject target, DependencyPropertyChangedEventArgs e)
    {
        var view = (PdfView)target;
        if (e.OldValue is PdfViewportController old) old.Invalidated -= view.ControllerInvalidated;
        if (view.IsLoaded) view.Subscribe();
        view.UpdateViewport();
    }
    private void Subscribe() { if (Controller is { } controller) { controller.Invalidated -= ControllerInvalidated; controller.Invalidated += ControllerInvalidated; } }
    private void Unsubscribe() { if (Controller is { } controller) controller.Invalidated -= ControllerInvalidated; }
    private void ControllerInvalidated(object? sender, EventArgs e) => Dispatcher.BeginInvoke(new Action(() =>
    {
        if (!IsLoaded) return;
        AutomationProperties.SetName(this, $"PDF document, page {Controller?.CurrentPage ?? 0} of {Controller?.PageCount ?? 0}");
        InvalidateVisual();
    }));
    protected override Size ArrangeOverride(Size finalSize)
    {
        if (finalSize.Width > 0 && finalSize.Height > 0) Controller?.SetViewport(finalSize.Width, finalSize.Height, VisualTreeHelper.GetDpi(this).DpiScaleX);
        return finalSize;
    }
    private void UpdateViewport()
    {
        if (ActualWidth > 0 && ActualHeight > 0) Controller?.SetViewport(ActualWidth, ActualHeight, VisualTreeHelper.GetDpi(this).DpiScaleX);
    }
    protected override void OnRender(DrawingContext context)
    {
        base.OnRender(context);
        if (Controller is not { } controller || ActualWidth < 1 || ActualHeight < 1) return;
        UpdateViewport();
        using var scene = controller.CaptureScene();
        SkiaWpfSurface.Draw(this, context, scene, ref _bitmap);
    }
    protected override void OnMouseDown(MouseButtonEventArgs e)
    {
        base.OnMouseDown(e);
        if (e.ChangedButton is not (MouseButton.Left or MouseButton.Middle)) return;
        Focus();
        var point = e.GetPosition(this);
        if (Controller?.BeginInteraction(new PdfPoint(point.X, point.Y), e.ChangedButton == MouseButton.Middle, (Keyboard.Modifiers & (ModifierKeys.Control | ModifierKeys.Shift)) != 0) == true)
        { CaptureMouse(); e.Handled = true; }
    }
    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        if (!IsMouseCaptured) return;
        var point = e.GetPosition(this);
        Controller?.MoveInteraction(new PdfPoint(point.X, point.Y));
        e.Handled = true;
    }
    protected override async void OnMouseUp(MouseButtonEventArgs e)
    {
        base.OnMouseUp(e);
        if (!IsMouseCaptured || Controller is not { } controller) return;
        var point = e.GetPosition(this);
        try { await controller.EndInteractionAsync(new PdfPoint(point.X, point.Y)); }
        catch (Exception error) { controller.ReportError(error); }
        finally { ReleaseMouseCapture(); }
        e.Handled = true;
    }
    protected override void OnLostMouseCapture(MouseEventArgs e) { Controller?.CancelInteraction(); base.OnLostMouseCapture(e); }
    protected override void OnMouseWheel(MouseWheelEventArgs e)
    {
        base.OnMouseWheel(e);
        if (Controller is not { } controller) return;
        var point = e.GetPosition(this);
        if ((Keyboard.Modifiers & ModifierKeys.Control) != 0)
            controller.SetZoom(controller.Zoom * Math.Pow(1.15, e.Delta / 120d), new PdfPoint(point.X, point.Y));
        else controller.ScrollBy(0, -e.Delta / 120d * 48);
        e.Handled = true;
    }
    protected override async void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if (Controller is not { } controller) return;
        var command = (Keyboard.Modifiers & ModifierKeys.Control) != 0;
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
                case Key.C when command: if (controller.SelectedText.Length != 0) Clipboard.SetText(controller.SelectedText); break;
                case Key.Escape: controller.CancelInteraction(); controller.ClearSelection(); break;
                default: return;
            }
            e.Handled = true;
        }
        catch (Exception error) { controller.ReportError(error); }
    }
}

internal static class SkiaWpfSurface
{
    public static void Draw(FrameworkElement owner, DrawingContext context, PdfScene scene, ref WriteableBitmap? bitmap)
    {
        var dpi = VisualTreeHelper.GetDpi(owner);
        var width = Math.Max(1, checked((int)Math.Ceiling(owner.ActualWidth * dpi.DpiScaleX)));
        var height = Math.Max(1, checked((int)Math.Ceiling(owner.ActualHeight * dpi.DpiScaleY)));
        if ((long)width * height > 32L * 1024 * 1024) throw new InvalidOperationException("The WPF backing surface exceeds its 32-megapixel limit.");
        if (bitmap is null || bitmap.PixelWidth != width || bitmap.PixelHeight != height || bitmap.DpiX != dpi.PixelsPerInchX)
            bitmap = new WriteableBitmap(width, height, dpi.PixelsPerInchX, dpi.PixelsPerInchY, PixelFormats.Pbgra32, null);
        bitmap.Lock();
        try
        {
            using var surface = SKSurface.Create(new SKImageInfo(width, height, SKColorType.Bgra8888, SKAlphaType.Premul), bitmap.BackBuffer, bitmap.BackBufferStride)
                ?? throw new InvalidOperationException("Unable to create the WPF Skia surface.");
            surface.Canvas.Clear(SKColors.Transparent);
            surface.Canvas.Scale((float)dpi.DpiScaleX, (float)dpi.DpiScaleY);
            scene.Draw(surface.Canvas);
            surface.Canvas.Flush();
            bitmap.AddDirtyRect(new Int32Rect(0, 0, width, height));
        }
        finally { bitmap.Unlock(); }
        context.DrawImage(bitmap, new Rect(0, 0, owner.ActualWidth, owner.ActualHeight));
    }
}
