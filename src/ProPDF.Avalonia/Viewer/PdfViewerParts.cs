using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using ProPDF.Core;
using ProPDF.Presentation;

namespace ProPDF.Avalonia;

/// <summary>Replaceable toolbar. Its Owner binding never replaces the host's DataContext.</summary>
public class PdfViewerToolbar : TemplatedControl
{
    public static readonly StyledProperty<PdfViewer?> OwnerProperty = AvaloniaProperty.Register<PdfViewerToolbar, PdfViewer?>(nameof(Owner));
    public PdfViewer? Owner { get => GetValue(OwnerProperty); set => SetValue(OwnerProperty, value); }
}

public class PdfViewerSidebar : TemplatedControl
{
    public static readonly StyledProperty<PdfViewer?> OwnerProperty = AvaloniaProperty.Register<PdfViewerSidebar, PdfViewer?>(nameof(Owner));
    public PdfViewer? Owner { get => GetValue(OwnerProperty); set => SetValue(OwnerProperty, value); }
}

/// <summary>Templated wrapper around the existing asynchronous, density-aware thumbnail renderer.</summary>
public class PdfViewerThumbnail : TemplatedControl
{
    public static readonly StyledProperty<PdfViewportController?> ControllerProperty = AvaloniaProperty.Register<PdfViewerThumbnail, PdfViewportController?>(nameof(Controller));
    public static readonly StyledProperty<int> PageNumberProperty = AvaloniaProperty.Register<PdfViewerThumbnail, int>(nameof(PageNumber), 1);
    public PdfViewportController? Controller { get => GetValue(ControllerProperty); set => SetValue(ControllerProperty, value); }
    public int PageNumber { get => GetValue(PageNumberProperty); set => SetValue(PageNumberProperty, value); }
}

public sealed class PdfViewerPageTappedEventArgs(int pageNumber, PdfPoint point) : EventArgs
{
    public int PageNumber { get; } = pageNumber;
    public PdfPoint Point { get; } = point;
}

/// <summary>Templated viewport; the three named parts are optional. The host owns Controller.</summary>
public class PdfViewerViewport : TemplatedControl
{
    public static readonly StyledProperty<PdfViewportController?> ControllerProperty = AvaloniaProperty.Register<PdfViewerViewport, PdfViewportController?>(nameof(Controller));
    public static readonly StyledProperty<bool> IsReadOnlyProperty = AvaloniaProperty.Register<PdfViewerViewport, bool>(nameof(IsReadOnly), true);
    private PdfView? _surface;
    private ScrollBar? _vertical;
    private ScrollBar? _horizontal;
    private PdfViewportController? _subscribed;
    private bool _attached, _syncing;
    private int _queued;
    private Point? _press;
    public PdfViewportController? Controller { get => GetValue(ControllerProperty); set => SetValue(ControllerProperty, value); }
    public bool IsReadOnly { get => GetValue(IsReadOnlyProperty); set => SetValue(IsReadOnlyProperty, value); }
    public event EventHandler<PdfViewerPageTappedEventArgs>? PageTapped;

    public PdfViewerViewport()
    {
        AddHandler(PointerPressedEvent, PointerPressed, RoutingStrategies.Tunnel, true);
        AddHandler(PointerReleasedEvent, PointerReleased, RoutingStrategies.Bubble, true);
        AddHandler(KeyDownEvent, GuardKeys, RoutingStrategies.Tunnel);
    }
    private void GuardKeys(object? sender, KeyEventArgs e)
    {
        if (IsReadOnly && (e.KeyModifiers & (KeyModifiers.Control | KeyModifiers.Meta)) != 0 && e.Key is Key.Z or Key.Y) e.Handled = true;
    }
    private void PointerPressed(object? sender, PointerPressedEventArgs e)
    {
        if (_surface is null) return;
        if (IsReadOnly && Controller is { } c && c.Tool is not (PdfTool.Pan or PdfTool.SelectText or PdfTool.SelectRegion)) c.Tool = PdfTool.Pan;
        _press = e.GetPosition(_surface);
    }
    private void PointerReleased(object? sender, PointerReleasedEventArgs e)
    {
        var start = _press;
        _press = null;
        if (_surface is null || start is null || Controller is not { } c || c.Tool is not (PdfTool.Pan or PdfTool.SelectText)) return;
        var end = e.GetPosition(_surface);
        if ((end - start.Value).Length > 4) return;
        if (c.HitTest(new PdfPoint(end.X, end.Y)) is { } hit) PageTapped?.Invoke(this, new PdfViewerPageTappedEventArgs(hit.PageNumber, hit.Point));
    }
    protected override void OnApplyTemplate(TemplateAppliedEventArgs e)
    {
        DetachParts();
        base.OnApplyTemplate(e);
        _surface = e.NameScope.Find<PdfView>("PART_Surface");
        _vertical = e.NameScope.Find<ScrollBar>("PART_VerticalScrollBar");
        _horizontal = e.NameScope.Find<ScrollBar>("PART_HorizontalScrollBar");
        if (_vertical is not null) _vertical.PropertyChanged += ScrollChanged;
        if (_horizontal is not null) _horizontal.PropertyChanged += ScrollChanged;
        Synchronize();
    }
    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == ControllerProperty) { Subscribe(); Synchronize(); }
        if (change.Property == IsReadOnlyProperty && IsReadOnly) Controller?.CancelInteraction();
    }
    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e); _attached = true; Subscribe(); Synchronize();
    }
    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        _attached = false; Subscribe(); Controller?.CancelInteraction(); _press = null;
        if (_surface is not null) _surface.Controller = null;
        base.OnDetachedFromVisualTree(e);
    }
    private void Subscribe()
    {
        if (_subscribed is not null) _subscribed.Invalidated -= Invalidated;
        _subscribed = _attached ? Controller : null;
        if (_subscribed is not null) _subscribed.Invalidated += Invalidated;
    }
    private void Invalidated(object? sender, EventArgs e)
    {
        if (Interlocked.Exchange(ref _queued, 1) != 0) return;
        Dispatcher.UIThread.Post(() => { Interlocked.Exchange(ref _queued, 0); if (_attached) Synchronize(); });
    }
    private void Synchronize()
    {
        _syncing = true;
        try
        {
            if (_surface is not null) _surface.Controller = _attached ? Controller : null;
            var c = Controller;
            UpdateBar(_vertical, c?.Extent.Height ?? 0, c?.Viewport.Height ?? 0, c?.Offset.Y ?? 0);
            UpdateBar(_horizontal, c?.Extent.Width ?? 0, c?.Viewport.Width ?? 0, c?.Offset.X ?? 0);
        }
        finally { _syncing = false; }
    }
    private static void UpdateBar(ScrollBar? bar, double extent, double viewport, double offset)
    {
        if (bar is null) return;
        bar.Minimum = 0; bar.Maximum = Math.Max(0, extent - viewport); bar.ViewportSize = viewport;
        bar.SmallChange = 48; bar.LargeChange = Math.Max(48, viewport * .85); bar.Value = offset; bar.IsEnabled = bar.Maximum > 0;
    }
    private void ScrollChanged(object? sender, AvaloniaPropertyChangedEventArgs e)
    {
        if (_syncing || e.Property != RangeBase.ValueProperty || Controller is not { } c) return;
        c.SetOffset(_horizontal?.Value ?? c.Offset.X, _vertical?.Value ?? c.Offset.Y);
    }
    private void DetachParts()
    {
        if (_vertical is not null) _vertical.PropertyChanged -= ScrollChanged;
        if (_horizontal is not null) _horizontal.PropertyChanged -= ScrollChanged;
        if (_surface is not null) _surface.Controller = null;
        _surface = null; _vertical = null; _horizontal = null;
    }
}
