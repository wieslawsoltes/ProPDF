using System.ComponentModel;

namespace ProPDF.Uno;

/// <summary>Full shared-workspace Uno shell. The host owns Context/Files; detach before disposing shared renderer services.</summary>
public sealed class PdfEditor : UserControl, IDisposable
{
    public static readonly DependencyProperty ContextProperty = DependencyProperty.Register(nameof(Context), typeof(PdfEditorContext), typeof(PdfEditor), new PropertyMetadata(null, ContextChanged));
    public static readonly DependencyProperty FilesProperty = DependencyProperty.Register(nameof(Files), typeof(IPdfUnoFiles), typeof(PdfEditor), new PropertyMetadata(null, ContextChanged));
    public PdfEditorContext? Context { get => (PdfEditorContext?)GetValue(ContextProperty); set => SetValue(ContextProperty, value); }
    public IPdfUnoFiles? Files { get => (IPdfUnoFiles?)GetValue(FilesProperty); set => SetValue(FilesProperty, value); }
    public PdfWorkspace? Workspace { get; private set; }
    public PdfView View { get; } = new();
    private readonly PdfEditorChrome _chrome;
    private readonly ListView _pages = new() { Name = "PagesList", SelectionMode = ListViewSelectionMode.Single, HorizontalContentAlignment = HorizontalAlignment.Stretch };
    private readonly PdfInspectorPanels _inspector = new(false);
    private PdfUnoFiles? _defaultFiles;
    private Guid? _pagesRevision;
    private bool _disposed, _syncing;
    public PdfEditor()
    {
        RequestedTheme = ElementTheme.Light;
        _pages.ItemTemplate = (DataTemplate)new PdfTemplates()["PageThumbnailTemplate"];
        AutomationProperties.SetName(_pages, "Document pages"); AutomationProperties.SetAutomationId(_pages, "PagesList");
        _pages.SelectionChanged += (_, _) => { if (!_syncing && _pages.SelectedItem is PdfUnoPage page) Context?.Viewport.GoToPage(page.Number); };
        _chrome = new PdfEditorChrome(_inspector, _pages, View, _inspector.SelectSection);
        Content = _chrome.Root;
        Loaded += (_, _) => Attach(); Unloaded += (_, _) => Detach();
    }
    private static void ContextChanged(DependencyObject sender, DependencyPropertyChangedEventArgs e) { var owner = (PdfEditor)sender; owner.Detach(); if (owner.IsLoaded) owner.Attach(); }
    private void Attach()
    {
        if (_disposed || Workspace is not null || Context is not { } context) return;
        var files = Files ?? (_defaultFiles ??= new PdfUnoFiles());
        Workspace = new PdfWorkspace(context, new PdfUnoDialogs(this, files), action => DispatcherQueue.TryEnqueue(() => action()));
        DataContext = Workspace; View.Controller = context.Viewport; Workspace.PropertyChanged += Updated; _chrome.Connect(Workspace); RefreshPages();
    }
    private void Detach()
    {
        _chrome.Connect(null);
        if (Workspace is { } w) { w.PropertyChanged -= Updated; w.Dispose(); }
        Workspace = null; View.Controller = null; DataContext = null; _pages.ItemsSource = null; _pagesRevision = null;
    }
    private void Updated(object? sender, PropertyChangedEventArgs e) => RefreshPages();
    private void RefreshPages()
    {
        if (Workspace is not { } w) return;
        _syncing = true;
        try
        {
            var document = w.Document;
            if (_pagesRevision != document?.Id)
            {
                // Session.Current can advance before the queued workspace refresh
                // publishes Pages. Capture both identity and pages from one snapshot;
                // otherwise an early notification can cache an empty list forever.
                _pages.ItemsSource = document?.Pages.Select(page => new PdfUnoPage(w.Viewport, page.Number)).ToArray();
                _pagesRevision = document?.Id;
            }
            _pages.SelectedIndex = w.Viewport.CurrentPage - 1;
        }
        finally { _syncing = false; }
    }
    // Uno's FrameworkElement.Dispose is nonvirtual; reimplement IDisposable and explicitly release its resources too.
    public new void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        try { Detach(); _defaultFiles?.Dispose(); _defaultFiles = null; }
        finally { base.Dispose(); }
    }
}
