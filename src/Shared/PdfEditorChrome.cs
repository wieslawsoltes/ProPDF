// The same native visual tree is compiled into all three UI adapters. Platform
// differences are confined to binding, geometry, automation and keyboard APIs.
using System.ComponentModel;
using System.Windows.Input;
using ProPDF.Presentation;
#if PROPDF_AVALONIA
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Data;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using FrameworkElement = Avalonia.Controls.Control;
using UiProperty = Avalonia.AvaloniaProperty;
using UiPath = Avalonia.Controls.Shapes.Path;
namespace ProPDF.Avalonia;
#elif PROPDF_WPF
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Media;
using UiProperty = System.Windows.DependencyProperty;
using UiPath = System.Windows.Shapes.Path;
namespace ProPDF.Wpf;
#else
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Data;
using Microsoft.UI.Xaml.Media;
using UiProperty = Microsoft.UI.Xaml.DependencyProperty;
using UiPath = Microsoft.UI.Xaml.Shapes.Path;
namespace ProPDF.Uno;
#endif

/// <summary>Document-first chrome shared verbatim by Avalonia, WPF and Uno.</summary>
internal sealed class PdfEditorChrome
{
    private const uint Ink = 0x292929, Muted = 0x686868, Line = 0xDEDEDE, Blue = 0x1465DC;
    private readonly Grid _body = new(), _center = new();
    private readonly Border _left = new(), _right = new();
    private readonly FrameworkElement _inspector, _catalog, _file, _organize, _redact, _toolSettings;
    private readonly StackPanel _sectionTabs = Stack(true), _history = Stack(true);
    private readonly Border _searchBar;
    private readonly TextBox _searchInput;
    private readonly Button _searchButton, _pagesButton, _closeTools, _backTools;
    private readonly TextBlock _title, _brandHint;
    private readonly Dictionary<string, Button> _quick = new();
    private readonly Dictionary<string, Button> _tabs = new();
    private readonly Action<int> _selectInspector;
    private PdfWorkspace? _workspace;
    public Grid Root { get; } = new() { Background = Brush(0xF5F5F5) };
    internal PdfEditorChrome(FrameworkElement inspector, FrameworkElement pages, PdfView view, Action<int> selectInspector)
    {
        _inspector = inspector; _selectInspector = selectInspector;
        foreach (var height in new[] { new GridLength(40), new GridLength(48), GridLength.Auto, new GridLength(1, GridUnitType.Star), new GridLength(28) })
            Root.RowDefinitions.Add(new() { Height = height });

        // Application bar: a modest product identity and a real, single-document tab.
        var titleBar = Columns(140, -1, 0); titleBar.Margin = new Thickness(8, 0, 12, 0);
        var product = Stack(true);
        product.Children.Add(Icon(PdfShellIcons.File, 0xD7373F));
        product.Children.Add(Text("ProPDF", 16, true)); Add(titleBar, product, 0, 0);
        var document = new Border { Background = Brush(0xFFFFFF), BorderBrush = Brush(0xD7373F), BorderThickness = new Thickness(0, 0, 0, 2),
            Padding = new Thickness(16, 0, 16, 0), MaxWidth = 340, HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(0, 5, 0, 0) };
        var name = Text("", 12); Bind(name, TextBlock.TextProperty, "DisplayName"); name.TextTrimming = TextTrimming.CharacterEllipsis;
        document.Child = name; Identify(document, "DocumentTab", "Current PDF document"); Add(titleBar, document, 0, 1);
        _brandHint = Text("Local PDF workspace", 11, false, Muted); _brandHint.HorizontalAlignment = HorizontalAlignment.Right; Add(titleBar, _brandHint, 0, 2);
        Add(Root, titleBar, 0);

        // Global bar: task navigation on the left, file operations on the right.
        var global = Columns(-1, 0); global.Margin = new Thickness(8, 0, 10, 0);
        var tasks = Stack(true);
        tasks.Children.Add(SectionActionButton("Menu", PdfShellSection.File, PdfShellIcons.Menu, "FileMenuButton"));
        tasks.Children.Add(SectionActionButton("All tools", PdfShellSection.AllTools, null, "AllToolsButton"));
        _sectionTabs.Children.Add(SectionActionButton("Edit", PdfShellSection.Edit, null, "EditSectionButton"));
        _sectionTabs.Children.Add(SectionActionButton("Export", PdfShellSection.Export, null, "ExportSectionButton"));
        _sectionTabs.Children.Add(SectionActionButton("Prepare form", PdfShellSection.Forms, null, "FormsSectionButton"));
        tasks.Children.Add(_sectionTabs); Add(global, tasks);
        var actions = Stack(true);
        _history.Children.Add(ActionButton("Undo", "UndoCommand", PdfShellIcons.Undo, false));
        _history.Children.Add(ActionButton("Redo", "RedoCommand", PdfShellIcons.Redo, false)); actions.Children.Add(_history);
        _searchButton = ActionButton("Find in document (Ctrl+F)", "Shell.ToggleSearchCommand", PdfShellIcons.Search, false, "FindPaneButton"); actions.Children.Add(_searchButton);
        actions.Children.Add(ActionButton("Open", "OpenCommand", PdfShellIcons.Open, false));
        var save = ActionButton("Save", "SaveCommand", PdfShellIcons.Save); save.Background = Brush(Blue); save.Foreground = Brush(0xFFFFFF); Recolor(save, 0xFFFFFF); actions.Children.Add(save);
        Add(global, actions, 0, 1); Add(Root, Edge(global, new Thickness(0, 1, 0, 1)), 1);

        var search = Stack(true); search.Margin = new Thickness(12, 6, 12, 6);
        _searchInput = Input("SearchQuery", "Find in document"); _searchInput.Width = 210; search.Children.Add(_searchInput);
        search.Children.Add(ActionButton("Find", "SearchCommand"));
        search.Children.Add(ActionButton("Previous match", "PreviousMatchCommand", PdfShellIcons.ChevronLeft, false));
        search.Children.Add(ActionButton("Next match", "NextMatchCommand", PdfShellIcons.ChevronRight, false));
        var matches = Text("", 11, false, Muted); Bind(matches, TextBlock.TextProperty, "MatchLabel"); search.Children.Add(matches);
        search.Children.Add(ActionButton("Close search", "Shell.ToggleSearchCommand", PdfShellIcons.Close, false, "CloseSearchButton"));
        _searchBar = Edge(Scroll(search, true), new Thickness(0, 0, 0, 1)); Add(Root, _searchBar, 2);

        _body.ColumnDefinitions.Add(new() { Width = new GridLength(300) }); _body.ColumnDefinitions.Add(new() { Width = new GridLength(1, GridUnitType.Star) });
        _body.ColumnDefinitions.Add(new() { Width = new GridLength(0) }); _body.ColumnDefinitions.Add(new() { Width = new GridLength(48) });
        Add(Root, _body, 3);
        _center.RowDefinitions.Add(new() { Height = new GridLength(1, GridUnitType.Star) }); _center.RowDefinitions.Add(new() { Height = new GridLength(44) });
        Add(_center, view); Add(_body, _center, 0, 1);
        var quick = Stack(); quick.Margin = new Thickness(4);
        foreach (var (tool, label, icon) in new[] { ("Pan", "Hand / pan", PdfShellIcons.Pan), ("SelectText", "Select text", PdfShellIcons.Select),
            ("Highlight", "Highlight text", PdfShellIcons.Highlight), ("Note", "Add a sticky note", PdfShellIcons.Comment),
            ("Ink", "Draw freehand", PdfShellIcons.Ink), ("Text", "Add text", PdfShellIcons.Text) })
        {
            var button = ActionButton(label, "ActivateToolCommand", icon, false, "Quick" + tool + "Button"); button.CommandParameter = tool;
            button.Width = 36; button.Height = 36; _quick.Add(tool, button); quick.Children.Add(button);
        }
        var palette = Edge(quick, new Thickness(1)); palette.CornerRadius = new CornerRadius(8); palette.Margin = new Thickness(12, 14, 0, 0);
        palette.HorizontalAlignment = HorizontalAlignment.Left; palette.VerticalAlignment = VerticalAlignment.Top;
        Identify(palette, "QuickToolsPalette", "Quick annotation tools"); Add(_center, palette);
        var nav = Stack(true); nav.HorizontalAlignment = HorizontalAlignment.Center;
        nav.Children.Add(ActionButton("Previous page", "PreviousPageCommand", PdfShellIcons.ChevronLeft, false));
        var pageInput = Input("PageInput", "Page number; press Enter to navigate"); pageInput.Width = 40; nav.Children.Add(pageInput);
        nav.Children.Add(ActionButton("Go", "GoToPageCommand")); nav.Children.Add(ActionButton("Next page", "NextPageCommand", PdfShellIcons.ChevronRight, false));
        var pageLabel = Text("", 11, false, Muted); Bind(pageLabel, TextBlock.TextProperty, "PageLabel"); nav.Children.Add(pageLabel);
        nav.Children.Add(ActionButton("Zoom out", "ZoomOutCommand", PdfShellIcons.Minus, false));
        var zoom = Text("", 12); zoom.Width = 46; zoom.TextAlignment = TextAlignment.Center; Bind(zoom, TextBlock.TextProperty, "ZoomLabel"); nav.Children.Add(zoom);
        nav.Children.Add(ActionButton("Zoom in", "ZoomInCommand", PdfShellIcons.Plus, false));
        Identify(nav, "PageNavigation", "Page navigation and zoom"); Add(_center, Edge(Scroll(nav, true), new Thickness(0, 1, 0, 0)), 1);

        // The rail is always reachable; drawers overlay rather than crushing the PDF on small windows.
        var rail = Stack(); rail.Margin = new Thickness(4, 10, 4, 8);
        _pagesButton = ActionButton("Page thumbnails", "Shell.TogglePagesCommand", PdfShellIcons.Pages, false, "PagesPaneButton"); rail.Children.Add(_pagesButton);
        rail.Children.Add(SectionActionButton("Bookmarks & links", PdfShellSection.Navigate, PdfShellIcons.Bookmark, "BookmarksPaneButton", false));
        rail.Children.Add(SectionActionButton("Comments", PdfShellSection.Review, PdfShellIcons.Comment, "CommentsPaneButton", false));
        rail.Children.Add(SectionActionButton("Document properties", PdfShellSection.Document, PdfShellIcons.Info, "PropertiesPaneButton", false));
        rail.Children.Add(new Border { Height = 1, Background = Brush(Line), Margin = new Thickness(5, 10, 5, 10) });
        rail.Children.Add(ActionButton("Rotate page clockwise", "RotateCommand", PdfShellIcons.Rotate, false));
        rail.Children.Add(ActionButton("Fit width", "FitWidthCommand", PdfShellIcons.FitWidth, false));
        rail.Children.Add(ActionButton("Fit page", "FitPageCommand", PdfShellIcons.FitPage, false));
        Add(_body, Edge(Scroll(rail), new Thickness(1, 0, 0, 0)), 0, 3);

        var pagePane = new Grid(); pagePane.RowDefinitions.Add(new() { Height = new GridLength(48) }); pagePane.RowDefinitions.Add(new() { Height = new GridLength(1, GridUnitType.Star) });
        var pageHeader = Columns(-1, 0); pageHeader.Margin = new Thickness(14, 0, 8, 0); Add(pageHeader, Text("Page thumbnails", 13, true));
        Add(pageHeader, ActionButton("Close thumbnails", "Shell.TogglePagesCommand", PdfShellIcons.Close, false, "ClosePagesButton"), 0, 1);
        Add(pagePane, pageHeader); Add(pagePane, pages, 1); _right.Child = pagePane; _right.Background = Brush(0xFAFAFA); _right.BorderBrush = Brush(Line); _right.BorderThickness = new Thickness(1, 0, 0, 0);
        Identify(_right, "PagesPane", "Page thumbnails pane"); Add(_body, _right, 0, 2);

        var tools = new Grid(); tools.RowDefinitions.Add(new() { Height = GridLength.Auto }); tools.RowDefinitions.Add(new() { Height = GridLength.Auto }); tools.RowDefinitions.Add(new() { Height = new GridLength(1, GridUnitType.Star) });
        var toolsHeader = Columns(0, -1, 0); toolsHeader.Margin = new Thickness(10, 8, 8, 6);
        _backTools = SectionActionButton("Back to all tools", PdfShellSection.AllTools, PdfShellIcons.ChevronLeft, "BackToToolsButton", false); Add(toolsHeader, _backTools);
        _title = Text("All tools", 18, true); Add(toolsHeader, _title, 0, 1);
        _closeTools = ActionButton("Close tools pane", "Shell.CloseToolsCommand", PdfShellIcons.Close, false, "CloseToolsButton"); Add(toolsHeader, _closeTools, 0, 2); Add(tools, toolsHeader);
        var toolSettings = Stack(); toolSettings.Margin = new Thickness(16, 0, 16, 12);
        var tool = Choice("Tools", "SelectedTool", "Drawing / selection tool", "Title"); toolSettings.Children.Add(tool);
        toolSettings.Children.Add(Input("ToolText", "Text, comment or field name"));
        _toolSettings = Edge(toolSettings, new Thickness(0, 0, 0, 1)); Add(tools, _toolSettings, 1);
        var sectionBody = new Grid(); Add(tools, sectionBody, 2);
        _catalog = Catalog(); _file = FilePanel(); _organize = OrganizePanel(); _redact = RedactPanel();
        foreach (var child in new[] { _catalog, _file, _inspector, _organize, _redact }) Add(sectionBody, child);
        _left.Child = tools; _left.Background = Brush(0xFFFFFF); _left.BorderBrush = Brush(Line); _left.BorderThickness = new Thickness(0, 0, 1, 0);
        Identify(_left, "ToolsPane", "Task tools pane"); Add(_body, _left);
        var status = Text("", 11, false, Muted); status.Margin = new Thickness(12, 0, 12, 0); status.TextTrimming = TextTrimming.CharacterEllipsis;
        Identify(status, "EditorStatus", "Document status"); Bind(status, TextBlock.TextProperty, "Status"); Add(Root, Edge(status, new Thickness(0, 1, 0, 0)), 4);
        Root.SizeChanged += (_, e) => Resize(e.NewSize.Width);
        Root.KeyDown += async (_, e) =>
        {
            if (e.Handled || _workspace is not { } w) return;
#if PROPDF_AVALONIA
            var control = (e.KeyModifiers & (KeyModifiers.Control | KeyModifiers.Meta)) != 0;
            var shift = (e.KeyModifiers & KeyModifiers.Shift) != 0;
#elif PROPDF_WPF
            var control = (Keyboard.Modifiers & ModifierKeys.Control) != 0;
            var shift = (Keyboard.Modifiers & ModifierKeys.Shift) != 0;
#else
            var control = (Microsoft.UI.Input.InputKeyboardSource.GetKeyStateForCurrentThread(Windows.System.VirtualKey.Control) & Windows.UI.Core.CoreVirtualKeyStates.Down) != 0;
            var shift = (Microsoft.UI.Input.InputKeyboardSource.GetKeyStateForCurrentThread(Windows.System.VirtualKey.Shift) & Windows.UI.Core.CoreVirtualKeyStates.Down) != 0;
#endif
#if PROPDF_AVALONIA
            var source = e.Source;
#else
            var source = e.OriginalSource;
#endif
            if (e.Key.ToString() == "Escape" && w.Shell.Dismiss()) { e.Handled = true; Focus(_searchButton); }
            else if (e.Key.ToString() == "Enter" && ReferenceEquals(source, _searchInput)) { e.Handled = true; await w.SearchCommand.ExecuteAsync(); }
            else if (e.Key.ToString() == "Enter" && ReferenceEquals(source, pageInput)) { e.Handled = true; await w.GoToPageCommand.ExecuteAsync(); }
            else if (control && e.Key.ToString() == "F") { e.Handled = true; w.Shell.SetSearchOpen(true); Focus(_searchInput); }
            else if (control && e.Key.ToString() == "S") { e.Handled = true; await (shift ? w.SaveAsCommand : w.SaveCommand).ExecuteAsync(); }
            else if (control && e.Key.ToString() == "O") { e.Handled = true; await w.OpenCommand.ExecuteAsync(); }
        };
        Apply();
    }
    public void Connect(PdfWorkspace? workspace)
    {
        if (_workspace is { } previous) { previous.Shell.PropertyChanged -= ShellChanged; previous.PropertyChanged -= WorkspaceChanged; }
        _workspace = workspace;
        if (workspace is not null) { workspace.Shell.PropertyChanged += ShellChanged; workspace.PropertyChanged += WorkspaceChanged; }
        Resize(Width(Root)); Apply();
    }
    private void WorkspaceChanged(object? sender, PropertyChangedEventArgs args)
    {
        // Selection changes do not rebuild the visual tree or request PDF rasterization.
        foreach (var (tool, button) in _quick) Selected(button, _workspace?.SelectedTool?.Tool.ToString() == tool);
    }
    private void ShellChanged(object? sender, PropertyChangedEventArgs args) => Apply();
    private void Resize(double width) { if (width > 0) _workspace?.Shell.SetAvailableWidth(width); }
    private void Apply()
    {
        var s = _workspace?.Shell;
        var compact = s?.IsCompact ?? false; var showTools = s?.ToolsVisible ?? true; var showPages = s?.PagesVisible ?? false;
        Show(_left, showTools); Show(_right, showPages); Show(_searchBar, s?.IsSearchOpen ?? false);
        Show(_sectionTabs, !compact); Show(_history, !(s?.IsSmall ?? false)); Show(_brandHint, !compact);
        _body.ColumnDefinitions[0].Width = new GridLength(!compact && showTools ? 300 : 0);
        _body.ColumnDefinitions[2].Width = new GridLength(!compact && showPages ? 188 : 0);
        Grid.SetColumn(_left, compact ? 1 : 0); _left.Width = compact ? s!.PanelWidth : double.NaN;
        _left.HorizontalAlignment = compact ? HorizontalAlignment.Left : HorizontalAlignment.Stretch;
        Grid.SetColumn(_right, compact ? 1 : 2); _right.Width = compact ? Math.Min(240, s!.PanelWidth) : double.NaN;
        _right.HorizontalAlignment = compact ? HorizontalAlignment.Right : HorizontalAlignment.Stretch;
        _title.Text = s?.Title ?? "All tools"; Show(_backTools, s?.IsInspector == true || s?.IsFile == true);
        Show(_catalog, s?.IsAllTools ?? true); Show(_file, s?.IsFile ?? false);
        Show(_toolSettings, s?.IsInspector == true && s.Section is not (PdfShellSection.Export or PdfShellSection.Document));
        Show(_inspector, s?.IsInspector == true && s.InspectorIndex < 6);
        Show(_organize, s?.Section == PdfShellSection.Organize); Show(_redact, s?.Section == PdfShellSection.Redact);
        if (s?.IsInspector == true && s.InspectorIndex < 6) _selectInspector(s.InspectorIndex);
        Selected(_pagesButton, showPages);
        foreach (var (section, button) in _tabs) Selected(button, showTools && s?.Section.ToString() == section);
        WorkspaceChanged(null, new PropertyChangedEventArgs(null));
    }
    private FrameworkElement Catalog()
    {
        var p = Stack(); p.Margin = new Thickness(12, 0, 12, 12);
        var hint = Text("Everything you need for your document", 12, false, Muted); hint.Margin = new Thickness(6, 0, 4, 14); p.Children.Add(hint);
        foreach (var category in PdfShellState.Categories)
        {
            var button = SectionActionButton(category.Title, category.Section, null, "Tool" + category.Section + "Button");
            var row = Columns(38, -1); var icon = Icon(category.Icon, category.Accent); Add(row, icon);
            var labels = Stack(); labels.Margin = new Thickness(4, 0, 0, 0);
            labels.Children.Add(Text(category.Title, 13, true)); var description = Text(category.Description, 11, false, Muted); description.TextWrapping = TextWrapping.Wrap; labels.Children.Add(description); Add(row, labels, 0, 1);
            button.Content = row; button.Padding = new Thickness(6, 10, 6, 10); button.HorizontalContentAlignment = HorizontalAlignment.Stretch; p.Children.Add(button);
        }
        var note = Text("Your files stay local. Changes are saved only when you choose Save.", 11, false, Muted); note.TextWrapping = TextWrapping.Wrap; note.Margin = new Thickness(8, 18, 8, 8); p.Children.Add(note);
        return Scroll(p);
    }
    private FrameworkElement FilePanel()
    {
        var p = PanelBody(); p.Children.Add(Text("Document", 12, true));
        foreach (var (text, command, icon) in new[] { ("Create blank PDF", "NewCommand", PdfShellIcons.Plus), ("Open protected PDF…", "OpenProtectedCommand", PdfShellIcons.Open),
            ("Save a copy…", "SaveAsCommand", PdfShellIcons.Save), ("Combine with another PDF…", "MergeCommand", PdfShellIcons.Pages),
            ("Extract current page…", "ExtractPageCommand", PdfShellIcons.Export), ("Copy selected text", "CopyCommand", PdfShellIcons.Text) })
            p.Children.Add(ActionButton(text, command, icon));
        p.Children.Add(Text("History", 12, true)); p.Children.Add(ActionButton("Undo", "UndoCommand", PdfShellIcons.Undo, true, "FileUndoButton")); p.Children.Add(ActionButton("Redo", "RedoCommand", PdfShellIcons.Redo, true, "FileRedoButton"));
        p.Children.Add(Text("Page display", 12, true)); p.Children.Add(Choice("LayoutModes", "LayoutMode", "Page layout"));
        p.Children.Add(SectionActionButton("Document properties", PdfShellSection.Document, PdfShellIcons.Info, "FilePropertiesButton"));
        var note = Text("Ctrl+O  Open     Ctrl+S  Save\nCtrl+Shift+S  Save a copy\nCtrl+F  Find     Esc  Dismiss", 12, false, Muted); note.TextWrapping = TextWrapping.Wrap; note.Margin = new Thickness(0, 18, 0, 0); p.Children.Add(note);
        return Scroll(p);
    }
    private FrameworkElement OrganizePanel()
    {
        var p = PanelBody(); var hint = Text("Organize the current page, or combine another PDF with this document.", 12, false, Muted); hint.TextWrapping = TextWrapping.Wrap; p.Children.Add(hint);
        foreach (var (text, command) in new[] { ("Rotate clockwise", "RotateCommand"), ("Insert blank page", "InsertPageCommand"), ("Duplicate page", "DuplicatePageCommand"),
            ("Move page earlier", "MoveUpCommand"), ("Move page later", "MoveDownCommand"), ("Delete page…", "DeletePageCommand"), ("Crop selected region", "CropCommand"), ("Place image in selection…", "InsertImageCommand") })
            p.Children.Add(ActionButton(text, command, null, true, "Organize" + command.Replace("Command", "Button", StringComparison.Ordinal)));
        p.Children.Add(SectionActionButton("Combine / extract files", PdfShellSection.File, PdfShellIcons.Pages, "OrganizeFilesButton")); return Scroll(p);
    }
    private FrameworkElement RedactPanel()
    {
        var p = PanelBody(); var hint = Text("Mark sensitive regions on the page, review every mark, then apply. Marks alone do not remove PDF content.", 12, false, Muted); hint.TextWrapping = TextWrapping.Wrap; p.Children.Add(hint);
        var mark = ActionButton("Mark a redaction", "ActivateToolCommand", PdfShellIcons.Redact, true, "MarkRedactionButton"); mark.CommandParameter = "Redact"; p.Children.Add(mark);
        p.Children.Add(ActionButton("Apply redactions…", "ApplyRedactionsCommand", null, true, "RedactApplyButton")); p.Children.Add(ActionButton("Clear all marks", "ClearRedactionsCommand", null, true, "RedactClearButton"));
        var warning = Text("Original files and undo history retain earlier content. Review metadata and attachments separately. This preview is not a certified sanitization tool.", 12, false, 0xA13A32); warning.TextWrapping = TextWrapping.Wrap; warning.Margin = new Thickness(0, 12, 0, 0); p.Children.Add(warning); return Scroll(p);
    }
    private Button SectionActionButton(string title, PdfShellSection section, string? icon, string name, bool label = true)
    {
        var button = ActionButton(title, "Shell.ShowSectionCommand", icon, label, name); button.CommandParameter = section.ToString();
        if (name.EndsWith("SectionButton", StringComparison.Ordinal) || name == "AllToolsButton" || name == "FileMenuButton") _tabs[section.ToString()] = button;
        return button;
    }
    private static StackPanel PanelBody() { var p = Stack(); p.Margin = new Thickness(16, 8, 16, 16); return p; }
    private static Grid Columns(params double[] widths)
    {
        var grid = new Grid(); foreach (var width in widths) grid.ColumnDefinitions.Add(new() { Width = width < 0 ? new GridLength(1, GridUnitType.Star) : width == 0 ? GridLength.Auto : new GridLength(width) }); return grid;
    }
    private static void Add(Grid parent, FrameworkElement child, int row = 0, int column = 0) { Grid.SetRow(child, row); Grid.SetColumn(child, column); parent.Children.Add(child); }
    private static StackPanel Stack(bool horizontal = false) => new() { Orientation = horizontal ? Orientation.Horizontal : Orientation.Vertical, VerticalAlignment = horizontal ? VerticalAlignment.Center : VerticalAlignment.Stretch };
    private static Border Edge(FrameworkElement child, Thickness thickness) => new() { Child = child, Background = Brush(0xFFFFFF), BorderBrush = Brush(Line), BorderThickness = thickness };
    private static ScrollViewer Scroll(FrameworkElement child, bool horizontal = false)
    {
        var scroll = new ScrollViewer { Content = child, HorizontalScrollBarVisibility = horizontal ? ScrollBarVisibility.Auto : ScrollBarVisibility.Disabled,
            VerticalScrollBarVisibility = horizontal ? ScrollBarVisibility.Disabled : ScrollBarVisibility.Auto };
#if !PROPDF_AVALONIA && !PROPDF_WPF
        scroll.HorizontalScrollMode = horizontal ? ScrollMode.Enabled : ScrollMode.Disabled; scroll.VerticalScrollMode = horizontal ? ScrollMode.Disabled : ScrollMode.Enabled;
#endif
        return scroll;
    }
    private static TextBlock Text(string text, double size, bool bold = false, uint color = Ink)
    {
        var result = new TextBlock { Text = text, FontSize = size, Foreground = Brush(color), VerticalAlignment = VerticalAlignment.Center };
#if PROPDF_AVALONIA
        result.FontWeight = bold ? FontWeight.SemiBold : FontWeight.Normal;
#elif PROPDF_WPF
        result.FontWeight = bold ? FontWeights.SemiBold : FontWeights.Normal;
#else
        result.FontWeight = bold ? Microsoft.UI.Text.FontWeights.SemiBold : Microsoft.UI.Text.FontWeights.Normal;
#endif
        return result;
    }
    private static Button ActionButton(string title, string command, string? icon = null, bool label = true, string? name = null)
    {
        var button = new Button { Name = name ?? command.Replace("Command", "Button", StringComparison.Ordinal), Padding = new Thickness(label ? 10 : 8, 7, label ? 10 : 8, 7),
            Margin = new Thickness(2), MinHeight = 32, MinWidth = 0, FontSize = 12, Background = Brush(0xFFFFFF), Foreground = Brush(Ink), BorderThickness = new Thickness(0), VerticalAlignment = VerticalAlignment.Center };
        if (icon is null) button.Content = title;
        else { var content = Stack(true); content.Children.Add(Icon(icon)); if (label) { var text = Text(title, 12); text.Margin = new Thickness(8, 0, 0, 0); content.Children.Add(text); } button.Content = content; }
        Identify(button, button.Name, title); Bind(button, Button.CommandProperty, command);
#if PROPDF_AVALONIA
        ToolTip.SetTip(button, title);
#elif PROPDF_WPF
        button.ToolTip = title;
#else
        ToolTipService.SetToolTip(button, title); button.CornerRadius = new CornerRadius(5);
#endif
        return button;
    }
    private static FrameworkElement Icon(string data, uint color = Ink)
    {
        var path = new UiPath { Width = 18, Height = 18, Stretch = Stretch.Uniform, Stroke = Brush(color), StrokeThickness = 1.6, VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Center };
#if PROPDF_AVALONIA || PROPDF_WPF
        path.Data = Geometry.Parse(data);
#else
        path.Data = (Geometry)Microsoft.UI.Xaml.Markup.XamlBindingHelper.ConvertValue(typeof(Geometry), data);
#endif
        return path;
    }
    private static void Recolor(Button button, uint color)
    {
        if (button.Content is not StackPanel content) return;
        foreach (var item in content.Children) { if (item is UiPath path) path.Stroke = Brush(color); else if (item is TextBlock text) text.Foreground = Brush(color); }
    }
    private static void Selected(Button button, bool selected)
    {
        var color = selected ? 0xE8F0FEu : 0xFFFFFFu;
        if (button.Background is SolidColorBrush current && current.Color.R == (byte)(color >> 16) && current.Color.G == (byte)(color >> 8) && current.Color.B == (byte)color) return;
        button.Background = Brush(selected ? 0xE8F0FEu : 0xFFFFFFu); button.Foreground = Brush(selected ? Blue : Ink); Recolor(button, selected ? Blue : Ink);
    }
    private static TextBox Input(string path, string label)
    {
        var input = new TextBox { Name = path + "Input", MinHeight = 32, MinWidth = 0, FontSize = 12, Margin = new Thickness(2, 3, 2, 3) };
#if PROPDF_AVALONIA
        input.PlaceholderText = label;
#elif PROPDF_WPF
        input.ToolTip = label; input.Padding = new Thickness(6, 4, 6, 4);
#else
        input.PlaceholderText = label;
#endif
        Identify(input, input.Name, label); Bind(input, TextBox.TextProperty, path, true); return input;
    }
    private static ComboBox Choice(string source, string selected, string label, string? display = null)
    {
        var box = new ComboBox { Name = selected + "Choice", MinHeight = 32, MinWidth = 0, FontSize = 12, HorizontalAlignment = HorizontalAlignment.Stretch, Margin = new Thickness(2, 3, 2, 3) };
#if PROPDF_AVALONIA
        if (display is not null) box.ItemTemplate = new global::Avalonia.Controls.Templates.FuncDataTemplate<PdfToolDescriptor>((item, _) => Text(item?.Title ?? "", 12));
#else
        if (display is not null) box.DisplayMemberPath = display;
#endif
        Identify(box, box.Name, label); Bind(box, ItemsControl.ItemsSourceProperty, source); Bind(box, ComboBox.SelectedItemProperty, selected, true); return box;
    }
    private static void Bind(FrameworkElement target, UiProperty property, string path, bool twoWay = false)
    {
        var binding = new Binding { Path =
#if !PROPDF_AVALONIA && !PROPDF_WPF
            new PropertyPath(path),
#elif PROPDF_WPF
            new PropertyPath(path),
#else
            path,
#endif
            Mode = twoWay ? BindingMode.TwoWay : BindingMode.OneWay };
#if PROPDF_AVALONIA
        target.Bind(property, binding);
#else
        if (twoWay) binding.UpdateSourceTrigger = UpdateSourceTrigger.PropertyChanged;
        target.SetBinding(property, binding);
#endif
    }
    private static SolidColorBrush Brush(uint rgb) => new(
#if PROPDF_AVALONIA || PROPDF_WPF
        Color.FromRgb((byte)(rgb >> 16), (byte)(rgb >> 8), (byte)rgb)
#else
        Windows.UI.Color.FromArgb(255, (byte)(rgb >> 16), (byte)(rgb >> 8), (byte)rgb)
#endif
    );
    private static void Identify(FrameworkElement element, string name, string title) { element.Name = name; AutomationProperties.SetName(element, title); AutomationProperties.SetAutomationId(element, name); }
    private static void Show(FrameworkElement element, bool visible)
    {
#if PROPDF_AVALONIA
        element.IsVisible = visible;
#else
        element.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;
#endif
    }
    private static double Width(FrameworkElement element) =>
#if PROPDF_AVALONIA
        element.Bounds.Width;
#else
        element.ActualWidth;
#endif
    private static void Focus(FrameworkElement element)
    {
#if PROPDF_AVALONIA || PROPDF_WPF
        element.Focus();
#else
        if (element is Control control) control.Focus(FocusState.Programmatic);
#endif
    }
}
