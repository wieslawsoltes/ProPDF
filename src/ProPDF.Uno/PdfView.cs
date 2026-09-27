using Uno.WinUI.Graphics2DSK;
using SkiaSharp;
using Windows.ApplicationModel.DataTransfer;

namespace ProPDF.Uno;

/// <summary>Reusable Skia-backed Uno viewer. One controller belongs to one viewport; the host owns its lifetime.</summary>
public sealed class PdfView : UserControl
{
    public static readonly DependencyProperty ControllerProperty = DependencyProperty.Register(nameof(Controller),
        typeof(PdfViewportController), typeof(PdfView), new PropertyMetadata(null, Changed));
    private readonly SceneSurface _surface = new();
    private readonly Grid _input = new() { Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent) };
    private readonly ScrollBar _vertical = new() { Orientation = Orientation.Vertical, Width = 12, MinWidth = 0 };
    private readonly ScrollBar _horizontal = new() { Orientation = Orientation.Horizontal, Height = 12, MinHeight = 0 };
    private bool _syncing, _captured, _queued;
    public PdfViewportController? Controller { get => (PdfViewportController?)GetValue(ControllerProperty); set => SetValue(ControllerProperty, value); }
    public PdfView()
    {
        IsTabStop = true; UseSystemFocusVisuals = true;
        AutomationProperties.SetName(this, "PDF document viewport");
        AutomationProperties.SetAutomationId(this, "PdfViewport");
        var grid = new Grid();
        grid.RowDefinitions.Add(new() { Height = new(1, GridUnitType.Star) }); grid.RowDefinitions.Add(new() { Height = GridLength.Auto });
        grid.ColumnDefinitions.Add(new() { Width = new(1, GridUnitType.Star) }); grid.ColumnDefinitions.Add(new() { Width = GridLength.Auto });
        _input.Children.Add(_surface); grid.Children.Add(_input);
        Grid.SetColumn(_vertical, 1); grid.Children.Add(_vertical); Grid.SetRow(_horizontal, 1); grid.Children.Add(_horizontal); Content = grid;
        _input.SizeChanged += (_, _) => UpdateViewport();
        _vertical.ValueChanged += (_, _) => { if (!_syncing && Controller is { } c) c.SetOffset(c.Offset.X, _vertical.Value); };
        _horizontal.ValueChanged += (_, _) => { if (!_syncing && Controller is { } c) c.SetOffset(_horizontal.Value, c.Offset.Y); };
        Loaded += (_, _) => { Subscribe(); UpdateViewport(); Refresh(); };
        Unloaded += (_, _) => { Unsubscribe(); _captured = false; Controller?.CancelInteraction(); _surface.SetScene(null); };
        _input.PointerPressed += Pressed; _input.PointerMoved += Moved; _input.PointerReleased += Released;
        _input.PointerCaptureLost += (_, _) => { _captured = false; Controller?.CancelInteraction(); };
        _input.PointerCanceled += (_, _) => { _captured = false; Controller?.CancelInteraction(); };
        _input.PointerWheelChanged += (_, e) =>
        {
            if (Controller is not { } c) return;
            var p = e.GetCurrentPoint(_input); var delta = p.Properties.MouseWheelDelta / 120d;
            if ((e.KeyModifiers & VirtualKeyModifiers.Control) != 0) c.SetZoom(c.Zoom * Math.Pow(1.15, delta), new(p.Position.X, p.Position.Y));
            else if (p.Properties.IsHorizontalMouseWheel || (e.KeyModifiers & VirtualKeyModifiers.Shift) != 0) c.ScrollBy(-delta * 48, 0);
            else c.ScrollBy(0, -delta * 48);
            e.Handled = true;
        };
        KeyDown += KeyPressed;
    }
    private static void Changed(DependencyObject sender, DependencyPropertyChangedEventArgs e)
    {
        var view = (PdfView)sender;
        if (e.OldValue is PdfViewportController old) old.Invalidated -= view.Invalidated;
        if (view.IsLoaded) view.Subscribe();
        view.UpdateViewport(); view.Refresh();
    }
    private void Subscribe() { if (Controller is { } c) { c.Invalidated -= Invalidated; c.Invalidated += Invalidated; } }
    private void Unsubscribe() { if (Controller is { } c) c.Invalidated -= Invalidated; }
    private void UpdateViewport()
    {
        if (!IsLoaded || _input.ActualWidth < 1 || _input.ActualHeight < 1) return;
        Controller?.SetViewport(_input.ActualWidth, _input.ActualHeight, XamlRoot?.RasterizationScale ?? 1);
    }
    private void Invalidated(object? sender, EventArgs e)
    {
        // Always marshal before accessing Uno dependency properties. Multiple progressive tile notifications coalesce.
        DispatcherQueue.TryEnqueue(() =>
        {
            if (!IsLoaded || _queued) return;
            _queued = true;
            DispatcherQueue.TryEnqueue(() => { _queued = false; if (IsLoaded) Refresh(); });
        });
    }
    private void Refresh()
    {
        if (!IsLoaded || Controller is not { } c) { _surface.SetScene(null); return; }
        _syncing = true;
        try
        {
            _vertical.Maximum = Math.Max(0, c.Extent.Height - c.Viewport.Height); _vertical.ViewportSize = c.Viewport.Height; _vertical.Value = c.Offset.Y;
            _horizontal.Maximum = Math.Max(0, c.Extent.Width - c.Viewport.Width); _horizontal.ViewportSize = c.Viewport.Width; _horizontal.Value = c.Offset.X;
            _surface.SetScene(c.CaptureScene());
        }
        finally { _syncing = false; }
        AutomationProperties.SetName(this, $"PDF document, page {c.CurrentPage} of {c.PageCount}");
    }
    private void Pressed(object sender, PointerRoutedEventArgs e)
    {
        var p = e.GetCurrentPoint(_input);
        if (p.Properties.IsRightButtonPressed && !p.Properties.IsLeftButtonPressed && !p.Properties.IsMiddleButtonPressed) return;
        Focus(FocusState.Pointer);
        if (Controller?.BeginInteraction(new(p.Position.X, p.Position.Y), p.Properties.IsMiddleButtonPressed,
            (e.KeyModifiers & (VirtualKeyModifiers.Control | VirtualKeyModifiers.Shift | VirtualKeyModifiers.Windows)) != 0) == true)
        { _captured = _input.CapturePointer(e.Pointer); e.Handled = true; }
    }
    private void Moved(object sender, PointerRoutedEventArgs e)
    {
        if (!_captured) return; var p = e.GetCurrentPoint(_input).Position;
        Controller?.MoveInteraction(new(p.X, p.Y)); e.Handled = true;
    }
    private async void Released(object sender, PointerRoutedEventArgs e)
    {
        if (!_captured || Controller is not { } c) return;
        var p = e.GetCurrentPoint(_input).Position;
        try { await c.EndInteractionAsync(new(p.X, p.Y)); }
        catch (Exception error) { c.ReportError(error); }
        finally { _captured = false; _input.ReleasePointerCapture(e.Pointer); }
        e.Handled = true;
    }
    private async void KeyPressed(object sender, KeyRoutedEventArgs e)
    {
        if (Controller is not { } c) return;
        var state = Microsoft.UI.Input.InputKeyboardSource.GetKeyStateForCurrentThread(VirtualKey.Control);
        var command = (state & Windows.UI.Core.CoreVirtualKeyStates.Down) != 0;
        try
        {
            switch (e.Key)
            {
                case VirtualKey.PageDown: c.ScrollBy(0, c.Viewport.Height * .85); break;
                case VirtualKey.PageUp: c.ScrollBy(0, -c.Viewport.Height * .85); break;
                case VirtualKey.Home when c.PageCount > 0: c.GoToPage(1); break;
                case VirtualKey.End when c.PageCount > 0: c.GoToPage(c.PageCount); break;
                case VirtualKey.Down: c.ScrollBy(0, 48); break;
                case VirtualKey.Up: c.ScrollBy(0, -48); break;
                case VirtualKey.Left: c.ScrollBy(-48, 0); break;
                case VirtualKey.Right: c.ScrollBy(48, 0); break;
                case VirtualKey.Add: c.SetZoom(c.Zoom * 1.15); break;
                case VirtualKey.Subtract: c.SetZoom(c.Zoom / 1.15); break;
                case VirtualKey.Number0 when command: c.FitPage(); break;
                case VirtualKey.Z when command: await c.Session.UndoAsync(); break;
                case VirtualKey.Y when command: await c.Session.RedoAsync(); break;
                case VirtualKey.C when command:
                    if (c.SelectedText.Length != 0) { var data = new DataPackage(); data.SetText(c.SelectedText); Clipboard.SetContent(data); }
                    break;
                case VirtualKey.Escape: c.CancelInteraction(); c.ClearSelection(); break;
                default: return;
            }
            e.Handled = true;
        }
        catch (Exception error) { c.ReportError(error); }
    }
}

/// <summary>Thread-safe scene ownership for Uno's independently scheduled Skia composition.</summary>
internal sealed class SceneSurface : SKCanvasElement
{
    private readonly object _gate = new();
    private PdfScene? _scene;
    public void SetScene(PdfScene? scene)
    {
        lock (_gate) { var previous = _scene; _scene = scene; previous?.Dispose(); }
        Invalidate();
    }
    protected override void RenderOverride(SKCanvas canvas, Size area)
    {
        lock (_gate)
        {
            if (_scene is { } scene) scene.Draw(canvas);
            else canvas.DrawColor(new SKColor(235, 239, 246));
        }
    }
}
