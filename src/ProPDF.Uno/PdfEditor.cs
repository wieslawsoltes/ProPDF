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
    private readonly Grid _body = new();
    private readonly ListView _pages = new() { Name = "PagesList", SelectionMode = ListViewSelectionMode.Single, HorizontalContentAlignment = HorizontalAlignment.Stretch };
    private readonly PdfInspectorPanels _inspector = new();
    private PdfUnoFiles? _defaultFiles;
    private Guid? _pagesRevision;
    private bool _disposed, _syncing, _pagesRequested, _inspectorRequested;
    public PdfEditor()
    {
        RequestedTheme = ElementTheme.Light;
        var root = new Grid { Background = PdfUi.Brush(0xF4F6FA) };
        foreach (var height in new[] { new GridLength(48), GridLength.Auto, GridLength.Auto, new GridLength(1, GridUnitType.Star), new GridLength(30) }) root.RowDefinitions.Add(new() { Height = height });
        var header = new Grid { Background = PdfUi.Brush(0x12213D), Padding = new Thickness(18, 0, 18, 0) };
        header.ColumnDefinitions.Add(new() { Width = new GridLength(140) }); header.ColumnDefinitions.Add(new()); header.ColumnDefinitions.Add(new() { Width = GridLength.Auto });
        header.Children.Add(new TextBlock { Text = "ProPDF", FontSize = 24, FontWeight = Microsoft.UI.Text.FontWeights.Bold, Foreground = PdfUi.Brush(0xFFFFFF), VerticalAlignment = VerticalAlignment.Center });
        var name = new TextBlock { Foreground = PdfUi.Brush(0xD8E2F3), FontSize = 13, VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis };
        PdfUi.Bind(name, TextBlock.TextProperty, "DisplayName"); Grid.SetColumn(name, 1); header.Children.Add(name);
        var brand = new TextBlock { Text = "UNO · LOCAL PDF EDITOR", FontSize = 10, Foreground = PdfUi.Brush(0xACBEDF), VerticalAlignment = VerticalAlignment.Center }; Grid.SetColumn(brand, 2); header.Children.Add(brand); root.Children.Add(header);
        var tools = PdfUi.Stack(true); tools.Margin = new Thickness(8, 5, 8, 5);
        foreach (var (text, cmd) in new[] { ("New", "NewCommand"), ("Open", "OpenCommand"), ("Protected", "OpenProtectedCommand"), ("Save", "SaveCommand"), ("Save as", "SaveAsCommand"), ("Undo", "UndoCommand"), ("Redo", "RedoCommand"), ("Merge PDF", "MergeCommand") }) tools.Children.Add(PdfUi.Button(text, cmd));
        var tool = PdfUi.Choice("Tools", "SelectedTool", "Tool", "Title"); tool.Width = 158; tools.Children.Add(tool);
        var toolText = PdfUi.Input("ToolText", "Text / comment / field name"); toolText.Width = 215; tools.Children.Add(toolText);
        tools.Children.Add(PdfUi.Button("Copy text", "CopyCommand"));
        AddRow(root, ToolScroll(tools), 1);
        var navigation = PdfUi.Stack(true); navigation.Margin = new Thickness(8, 3, 8, 5);
        var pagesToggle = new Button { Content = "Pages", Padding = new Thickness(10, 5, 10, 5) }; pagesToggle.Click += (_, _) => { _pagesRequested = !_pagesRequested; Responsive(); }; navigation.Children.Add(pagesToggle);
        navigation.Children.Add(PdfUi.Button("‹", "PreviousPageCommand")); navigation.Children.Add(PdfUi.Button("›", "NextPageCommand"));
        var pageInput = PdfUi.Input("PageInput", "Page"); pageInput.Width = 48; navigation.Children.Add(pageInput); navigation.Children.Add(PdfUi.Button("Go", "GoToPageCommand"));
        navigation.Children.Add(PdfUi.BoundText("PageLabel")); navigation.Children.Add(PdfUi.Button("−", "ZoomOutCommand")); navigation.Children.Add(PdfUi.BoundText("ZoomLabel")); navigation.Children.Add(PdfUi.Button("+", "ZoomInCommand"));
        navigation.Children.Add(PdfUi.Button("Fit width", "FitWidthCommand")); navigation.Children.Add(PdfUi.Button("Fit page", "FitPageCommand"));
        var layout = PdfUi.Choice("LayoutModes", "LayoutMode", "Page layout"); layout.Width = 130; navigation.Children.Add(layout);
        var search = PdfUi.Input("SearchQuery", "Search document"); search.Width = 165; navigation.Children.Add(search);
        search.KeyDown += async (_, e) => { if (e.Key == VirtualKey.Enter && Workspace is { } w) { await w.SearchCommand.ExecuteAsync(); e.Handled = true; } };
        navigation.Children.Add(PdfUi.Button("Find", "SearchCommand")); navigation.Children.Add(PdfUi.Button("Next match", "NextMatchCommand")); navigation.Children.Add(PdfUi.Button("Previous match", "PreviousMatchCommand"));
        navigation.Children.Add(PdfUi.BoundText("MatchLabel"));
        var inspectToggle = new Button { Content = "Inspector", Padding = new Thickness(10, 5, 10, 5) }; inspectToggle.Click += (_, _) => { _inspectorRequested = !_inspectorRequested; Responsive(); }; navigation.Children.Add(inspectToggle);
        AddRow(root, ToolScroll(navigation), 2);
        _body.ColumnDefinitions.Add(new() { Width = new GridLength(174) }); _body.ColumnDefinitions.Add(new() { Width = new GridLength(1, GridUnitType.Star) }); _body.ColumnDefinitions.Add(new() { Width = new GridLength(316) });
        var left = new Grid(); left.RowDefinitions.Add(new() { Height = GridLength.Auto }); left.RowDefinitions.Add(new() { Height = new GridLength(1, GridUnitType.Star) });
        var pagesActions = PdfUi.Stack(); pagesActions.Margin = new Thickness(8);
        pagesActions.Children.Add(PdfUi.Label("PAGES", true));
        PdfUi.Actions(pagesActions, ("Rotate", "RotateCommand"), ("Insert", "InsertPageCommand"));
        PdfUi.Actions(pagesActions, ("Duplicate", "DuplicatePageCommand"), ("Delete", "DeletePageCommand"));
        PdfUi.Actions(pagesActions, ("↑", "MoveUpCommand"), ("↓", "MoveDownCommand"), ("Export", "ExtractPageCommand")); left.Children.Add(pagesActions);
        _pages.ItemTemplate = (DataTemplate)new PdfTemplates()["PageThumbnailTemplate"];
        AutomationProperties.SetName(_pages, "Document pages"); AutomationProperties.SetAutomationId(_pages, "PagesList");
        _pages.SelectionChanged += (_, _) => { if (!_syncing && _pages.SelectedItem is PdfUnoPage page) Context?.Viewport.GoToPage(page.Number); };
        Grid.SetRow(_pages, 1); left.Children.Add(_pages); _body.Children.Add(left);
        var center = new Grid(); center.RowDefinitions.Add(new() { Height = GridLength.Auto }); center.RowDefinitions.Add(new() { Height = new GridLength(1, GridUnitType.Star) });
        var edits = PdfUi.Stack(true); edits.Margin = new Thickness(8, 4, 8, 4);
        foreach (var (text, cmd) in new[] { ("Crop selection", "CropCommand"), ("Image in selection", "InsertImageCommand"), ("Apply redactions", "ApplyRedactionsCommand"), ("Clear marks", "ClearRedactionsCommand") }) edits.Children.Add(PdfUi.Button(text, cmd));
        center.Children.Add(ToolScroll(edits)); Grid.SetRow(View, 1); center.Children.Add(View); Grid.SetColumn(center, 1); _body.Children.Add(center);
        _inspector.Background = PdfUi.Brush(0xFFFFFF); Grid.SetColumn(_inspector, 2); _body.Children.Add(_inspector); AddRow(root, _body, 3);
        var status = new TextBlock { Name = "EditorStatus", FontSize = 11, Margin = new Thickness(12, 0, 12, 0), VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis };
        PdfUi.Bind(status, TextBlock.TextProperty, "Status"); AddRow(root, status, 4); Content = root;
        Loaded += (_, _) => Attach(); Unloaded += (_, _) => Detach(); SizeChanged += (_, _) => Responsive();
        KeyDown += async (_, e) =>
        {
            if (e.Handled || Workspace is not { } w) return;
            var control = (Microsoft.UI.Input.InputKeyboardSource.GetKeyStateForCurrentThread(VirtualKey.Control) & Windows.UI.Core.CoreVirtualKeyStates.Down) != 0;
            if (!control) return;
            if (e.Key == VirtualKey.S) { e.Handled = true; await w.SaveCommand.ExecuteAsync(); }
            else if (e.Key == VirtualKey.O) { e.Handled = true; await w.OpenCommand.ExecuteAsync(); }
        };
    }
    private static ScrollViewer ToolScroll(UIElement child) => new() { Content = child, HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
        VerticalScrollBarVisibility = ScrollBarVisibility.Disabled, HorizontalScrollMode = ScrollMode.Enabled, VerticalScrollMode = ScrollMode.Disabled, Background = PdfUi.Brush(0xFFFFFF) };
    private static void AddRow(Grid parent, UIElement child, int row) { Grid.SetRow(child, row); parent.Children.Add(child); }
    private static void ContextChanged(DependencyObject sender, DependencyPropertyChangedEventArgs e) { var owner = (PdfEditor)sender; owner.Detach(); if (owner.IsLoaded) owner.Attach(); }
    private void Attach()
    {
        if (_disposed || Workspace is not null || Context is not { } context) return;
        var files = Files ?? (_defaultFiles ??= new PdfUnoFiles());
        Workspace = new PdfWorkspace(context, new PdfUnoDialogs(this, files), action => DispatcherQueue.TryEnqueue(() => action()));
        DataContext = Workspace; View.Controller = context.Viewport; Workspace.PropertyChanged += Updated; RefreshPages(); Responsive();
    }
    private void Detach()
    {
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
    private void Responsive()
    {
        if (ActualWidth < 1) return;
        _body.ColumnDefinitions[0].Width = new GridLength(ActualWidth >= 1250 || _pagesRequested ? 174 : 0);
        _body.ColumnDefinitions[2].Width = new GridLength(ActualWidth >= 1000 || _inspectorRequested ? Math.Min(316, ActualWidth * .75) : 0);
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
