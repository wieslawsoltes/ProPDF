using System.Text.Json;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Controls.Templates;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Platform;
using Avalonia.Styling;
using Avalonia.Threading;
using Avalonia.VisualTree;
using ProPDF.Core;
using ProPDF.Rendering.Skia;
using ProPDF.SampleSupport;

namespace ProPDF.Avalonia.Viewer;

/// <summary>The same sample content is hosted by a desktop Window and Avalonia's browser single-view lifetime.</summary>
public sealed class ViewerShell : UserControl, IAsyncDisposable
{
    private bool _started, _disposed;
    private readonly Border _banner;
    private int _findKeyCount;
    private string? _findKeySource;
    public ViewerShell()
    {
        var catalog = CreateFontCatalog(out var count);
        FontFaceCount = count;
        Runtime = new DemoWorkspace(action => Dispatcher.UIThread.Post(action), catalog);
        Viewer = new PdfViewer { Name = "PdfViewer", Context = Runtime.Context, IsReadOnly = true };
        // Observe only the Find shortcut, never ordinary text entry or passwords.
        AddHandler(KeyDownEvent, (_, e) =>
        {
            if (e.Key == Key.F && (e.KeyModifiers & (KeyModifiers.Control | KeyModifiers.Meta)) != 0)
            {
                _findKeyCount++;
                _findKeySource = (e.Source as Control)?.Name ?? e.Source?.GetType().Name;
            }
        }, RoutingStrategies.Tunnel, true);
        var enableEditing = new CheckBox { Name = "EnableEditingCheck", Content = "Enable editing", Margin = new Thickness(12, 0) };
        var dark = new CheckBox { Name = "DarkThemeCheck", Content = "Dark theme", Margin = new Thickness(12, 0) };
        var minimal = new CheckBox { Name = "MinimalTemplateCheck", Content = "Minimal template", Margin = new Thickness(12, 0) };
        enableEditing.PropertyChanged += (_, e) => { if (e.Property == ToggleButton.IsCheckedProperty) Viewer.IsReadOnly = enableEditing.IsChecked != true; };
        dark.PropertyChanged += (_, e) => { if (e.Property == ToggleButton.IsCheckedProperty) ApplyTheme(dark.IsChecked == true); };
        minimal.PropertyChanged += (_, e) => { if (e.Property == ToggleButton.IsCheckedProperty) SetMinimalTemplate(minimal.IsChecked == true); };
        var controls = new WrapPanel { Margin = new Thickness(12, 8) };
        controls.Children.Add(new TextBlock { Text = "AVALONIA  /  PDFPIG", FontSize = 11, FontWeight = FontWeight.SemiBold, VerticalAlignment = global::Avalonia.Layout.VerticalAlignment.Center, Margin = new Thickness(4, 0, 24, 0) });
        controls.Children.Add(enableEditing); controls.Children.Add(dark); controls.Children.Add(minimal);
        _banner = new Border { Child = controls, BorderThickness = new Thickness(0, 0, 0, 1) };
        var root = new DockPanel(); DockPanel.SetDock(_banner, Dock.Top); root.Children.Add(_banner); root.Children.Add(Viewer); Content = root;
        FontFamily = new FontFamily("avares://Avalonia.Fonts.Inter/Assets#Inter");
        ApplyTheme(false);
        AttachedToVisualTree += async (_, _) =>
        {
            if (_started) return; _started = true;
            try { await Runtime.LoadWelcomeAsync(); await Runtime.Viewport.WaitForRenderingAsync(); Ready = true; }
            catch (Exception error) { StartupError = error.ToString(); Runtime.Viewport.ReportError(error); }
        };
    }
    public PdfViewer Viewer { get; }
    public DemoWorkspace Runtime { get; }
    public int FontFaceCount { get; }
    public bool Ready { get; private set; }
    public string? StartupError { get; private set; }
    public void ApplyTheme(bool dark)
    {
        if (Application.Current is { } app) app.RequestedThemeVariant = dark ? ThemeVariant.Dark : ThemeVariant.Light;
        Foreground = new SolidColorBrush(Color.Parse(dark ? "#E8EEFC" : "#182B4F"));
        _banner.Background = new SolidColorBrush(Color.Parse(dark ? "#242C3C" : "#F5F7FA"));
        _banner.BorderBrush = new SolidColorBrush(Color.Parse(dark ? "#39445A" : "#DDE3EC"));
    }
    private static PdfFontCatalog CreateFontCatalog(out int count)
    {
        var faces = new List<PdfFontFace>();
        foreach (var uri in AssetLoader.GetAssets(new Uri("avares://Avalonia.Fonts.Inter/"), null))
        {
            var name = Path.GetFileName(uri.AbsolutePath);
            if (!name.EndsWith("-Regular.ttf", StringComparison.OrdinalIgnoreCase) && !name.EndsWith("-Bold.ttf", StringComparison.OrdinalIgnoreCase) && !name.EndsWith("-Italic.ttf", StringComparison.OrdinalIgnoreCase) && !name.EndsWith("-BoldItalic.ttf", StringComparison.OrdinalIgnoreCase)) continue;
            using var input = AssetLoader.Open(uri); using var output = new MemoryStream(); input.CopyTo(output);
            faces.Add(new PdfFontFace("Inter", output.ToArray(), name.Contains("Bold", StringComparison.OrdinalIgnoreCase), name.Contains("Italic", StringComparison.OrdinalIgnoreCase)));
        }
        if (faces.Count == 0) throw new InvalidOperationException("The sample's packaged Inter font resources were not found.");
        count = faces.Count;
        return new PdfFontCatalog(faces, "Inter", new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) { ["Helvetica"] = "Inter", ["Arial"] = "Inter", ["sans-serif"] = "Inter" });
    }
    public void SetMinimalTemplate(bool minimal)
    {
        if (!minimal) { Viewer.ClearValue(TemplatedControl.TemplateProperty); return; }
        Viewer.Template = new FuncControlTemplate<PdfViewer>((owner, scope) =>
        {
            var viewport = new PdfViewerViewport { Name = "PART_Viewport" }; scope.Register("PART_Viewport", viewport);
            var toolbar = new StackPanel { Orientation = global::Avalonia.Layout.Orientation.Horizontal, Spacing = 8, Margin = new Thickness(12) };
            toolbar.Children.Add(new TextBlock { Text = "CUSTOM TEMPLATE", VerticalAlignment = global::Avalonia.Layout.VerticalAlignment.Center, Margin = new Thickness(0, 0, 16, 0) });
            toolbar.Children.Add(new Button { Name = "MinimalPreviousButton", Content = "Previous", Command = owner.PreviousPageCommand });
            toolbar.Children.Add(new Button { Name = "MinimalNextButton", Content = "Next", Command = owner.NextPageCommand });
            toolbar.Children.Add(new Button { Name = "MinimalFitButton", Content = "Fit page", Command = owner.FitPageCommand });
            var root = new DockPanel(); DockPanel.SetDock(toolbar, Dock.Top); root.Children.Add(toolbar); root.Children.Add(viewport); return root;
        });
    }
    /// <summary>Read-only sample diagnostics. Browser tests still perform actual mouse/keyboard/file-picker interactions.</summary>
    public string Snapshot()
    {
        var controls = new Dictionary<string, object>(StringComparer.Ordinal);
        foreach (var c in this.GetVisualDescendants().OfType<Control>())
        {
            var name = c.Name;
            if (c is ListBoxItem)
            {
                name = c.DataContext switch { PdfPageInfo page => "PageItem" + page.Number, PdfFormFieldInfo field => "FieldItem_" + field.Name, PdfNavigationBookmark bookmark => "BookmarkItem_" + bookmark.Title, _ => name };
            }
            if (string.IsNullOrEmpty(name) || c.TranslatePoint(default, this) is not { } position) continue;
            var visible = c.IsVisible && c.Bounds.Width > 0 && c.Bounds.Height > 0 && c.GetVisualAncestors().All(v => v.IsVisible);
            if (!visible) continue;
            var text = c switch { TextBox box when box.Name != "PART_DialogInput" => box.Text, TextBlock block => block.Text, ContentControl content => content.Content as string, _ => null };
            controls[name] = new { x = position.X, y = position.Y, width = c.Bounds.Width, height = c.Bounds.Height, enabled = c.IsEffectivelyEnabled, visible, type = c.GetType().Name, text };
        }
        var focus = TopLevel.GetTopLevel(this)?.FocusManager?.GetFocusedElement() as Control;
        using var scene = Runtime.Viewport.CaptureScene();
        return JsonSerializer.Serialize(new
        {
            ready = Ready, startupError = StartupError, fontFaceCount = FontFaceCount,
            pages = Viewer.PageCount, page = Viewer.PageNumber, zoom = Viewer.Zoom, zoomMode = Viewer.ZoomMode.ToString(), layout = Viewer.LayoutMode.ToString(), tool = Viewer.Tool.ToString(),
            readOnly = Viewer.IsReadOnly, revision = Runtime.Session.Current?.Id, dirty = Runtime.Session.IsDirty,
            error = Viewer.ErrorMessage, busy = Viewer.IsBusy, rendering = Runtime.Viewport.IsRendering,
            searching = Viewer.IsSearching, search = Viewer.SearchText, hits = Viewer.SearchResults.Count, hitIndex = Viewer.SearchResultIndex, selectedText = Runtime.Viewport.SelectedText,
            searchBarVisible = Viewer.IsSearchBarVisible, focusedControl = focus?.Name, focusedType = focus?.GetType().Name, findKeyCount = _findKeyCount, findKeySource = _findKeySource,
            sidebar = Viewer.IsSidebarVisible, sidebarIndex = Viewer.SidebarIndex, compact = Viewer.IsCompact, dialog = Viewer.IsDialogOpen, dialogTitle = Viewer.DialogTitle,
            title = Viewer.DocumentTitle, sceneTileCount = scene.TileCount, rasterPixels = scene.RasterPixelCount,
            offsetY = Runtime.Viewport.Offset.Y, extentHeight = Runtime.Viewport.Extent.Height, viewportWidth = Runtime.Viewport.Viewport.Width,
            theme = Application.Current?.ActualThemeVariant.ToString(),
            annotationCount = Viewer.Workspace?.Annotations.Count ?? 0,
            formValues = Viewer.Workspace?.Fields.Select(f => new { f.Name, f.Value }).ToArray(),
            bookmarkCount = Viewer.Workspace?.NavigationBookmarks.Count ?? 0,
            pageBounds = Runtime.Viewport.GetPageViewportBounds(Viewer.PageNumber), controls
        });
    }
    public async ValueTask DisposeAsync()
    {
        if (_disposed) return; _disposed = true;
        Content = null; Viewer.Context = null;
        await Runtime.DisposeAsync();
        if (Viewer.PrintService is IDisposable disposable) disposable.Dispose();
    }
}
