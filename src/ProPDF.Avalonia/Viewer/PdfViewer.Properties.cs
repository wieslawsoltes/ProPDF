using Avalonia;
using Avalonia.Controls.Primitives;
using Avalonia.Data;
using Avalonia.Interactivity;
using ProPDF.Core;
using ProPDF.Presentation;

namespace ProPDF.Avalonia;

/// <summary>A composable, templated PDF viewer. Context and its session/renderer remain owned by the host.</summary>
public partial class PdfViewer : TemplatedControl
{
    public static readonly StyledProperty<PdfEditorContext?> ContextProperty = AvaloniaProperty.Register<PdfViewer, PdfEditorContext?>(nameof(Context));
    public static readonly StyledProperty<IPdfWorkspaceDialogs?> DialogsProperty = AvaloniaProperty.Register<PdfViewer, IPdfWorkspaceDialogs?>(nameof(Dialogs));
    public static readonly StyledProperty<IPdfViewerPrintService?> PrintServiceProperty = AvaloniaProperty.Register<PdfViewer, IPdfViewerPrintService?>(nameof(PrintService));
    public static readonly StyledProperty<bool> IsReadOnlyProperty = AvaloniaProperty.Register<PdfViewer, bool>(nameof(IsReadOnly), true);
    public static readonly StyledProperty<bool> IsToolbarVisibleProperty = AvaloniaProperty.Register<PdfViewer, bool>(nameof(IsToolbarVisible), true);
    public static readonly StyledProperty<bool> IsSidebarVisibleProperty = AvaloniaProperty.Register<PdfViewer, bool>(nameof(IsSidebarVisible), true, defaultBindingMode: BindingMode.TwoWay);
    public static readonly StyledProperty<bool> IsSearchBarVisibleProperty = AvaloniaProperty.Register<PdfViewer, bool>(nameof(IsSearchBarVisible), false, defaultBindingMode: BindingMode.TwoWay);
    public static readonly StyledProperty<bool> IsStatusBarVisibleProperty = AvaloniaProperty.Register<PdfViewer, bool>(nameof(IsStatusBarVisible), true);
    public static readonly StyledProperty<double> SidebarWidthProperty = AvaloniaProperty.Register<PdfViewer, double>(nameof(SidebarWidth), 264, coerce: (_, x) => double.IsFinite(x) ? Math.Clamp(x, 180, 480) : 264);
    public static readonly StyledProperty<int> SidebarIndexProperty = AvaloniaProperty.Register<PdfViewer, int>(nameof(SidebarIndex), 0, defaultBindingMode: BindingMode.TwoWay, coerce: (_, x) => Math.Clamp(x, 0, 7));
    public static readonly StyledProperty<int> PageNumberProperty = AvaloniaProperty.Register<PdfViewer, int>(nameof(PageNumber), 1, defaultBindingMode: BindingMode.TwoWay, coerce: (_, x) => Math.Max(1, x));
    public static readonly StyledProperty<double> ZoomProperty = AvaloniaProperty.Register<PdfViewer, double>(nameof(Zoom), 1, defaultBindingMode: BindingMode.TwoWay, coerce: (_, x) => double.IsFinite(x) ? Math.Clamp(x, .05, 16) : 1);
    public static readonly StyledProperty<PdfViewerZoomMode> ZoomModeProperty = AvaloniaProperty.Register<PdfViewer, PdfViewerZoomMode>(nameof(ZoomMode), PdfViewerZoomMode.FitWidth, defaultBindingMode: BindingMode.TwoWay, validate: Enum.IsDefined);
    public static readonly StyledProperty<PdfLayoutMode> LayoutModeProperty = AvaloniaProperty.Register<PdfViewer, PdfLayoutMode>(nameof(LayoutMode), PdfLayoutMode.Continuous, defaultBindingMode: BindingMode.TwoWay, validate: Enum.IsDefined);
    public static readonly StyledProperty<PdfTool> ToolProperty = AvaloniaProperty.Register<PdfViewer, PdfTool>(nameof(Tool), PdfTool.Pan, defaultBindingMode: BindingMode.TwoWay, validate: Enum.IsDefined);
    public static readonly StyledProperty<string> PageTextProperty = AvaloniaProperty.Register<PdfViewer, string>(nameof(PageText), "1", defaultBindingMode: BindingMode.TwoWay);
    public static readonly StyledProperty<string> ZoomTextProperty = AvaloniaProperty.Register<PdfViewer, string>(nameof(ZoomText), "100%", defaultBindingMode: BindingMode.TwoWay);
    public static readonly StyledProperty<string> SearchTextProperty = AvaloniaProperty.Register<PdfViewer, string>(nameof(SearchText), "", defaultBindingMode: BindingMode.TwoWay);
    public static readonly StyledProperty<bool> MatchCaseProperty = AvaloniaProperty.Register<PdfViewer, bool>(nameof(MatchCase), false, defaultBindingMode: BindingMode.TwoWay);
    public static readonly StyledProperty<bool> WholeWordProperty = AvaloniaProperty.Register<PdfViewer, bool>(nameof(WholeWord), false, defaultBindingMode: BindingMode.TwoWay);
    public static readonly StyledProperty<bool> UseRegularExpressionProperty = AvaloniaProperty.Register<PdfViewer, bool>(nameof(UseRegularExpression), false, defaultBindingMode: BindingMode.TwoWay);
    public static readonly StyledProperty<int> SearchResultIndexProperty = AvaloniaProperty.Register<PdfViewer, int>(nameof(SearchResultIndex), -1, defaultBindingMode: BindingMode.TwoWay);
    public static readonly StyledProperty<string> DialogValueProperty = AvaloniaProperty.Register<PdfViewer, string>(nameof(DialogValue), "", defaultBindingMode: BindingMode.TwoWay);

    public PdfEditorContext? Context { get => GetValue(ContextProperty); set => SetValue(ContextProperty, value); }
    public IPdfWorkspaceDialogs? Dialogs { get => GetValue(DialogsProperty); set => SetValue(DialogsProperty, value); }
    public IPdfViewerPrintService? PrintService { get => GetValue(PrintServiceProperty); set => SetValue(PrintServiceProperty, value); }
    public bool IsReadOnly { get => GetValue(IsReadOnlyProperty); set => SetValue(IsReadOnlyProperty, value); }
    public bool IsToolbarVisible { get => GetValue(IsToolbarVisibleProperty); set => SetValue(IsToolbarVisibleProperty, value); }
    public bool IsSidebarVisible { get => GetValue(IsSidebarVisibleProperty); set => SetValue(IsSidebarVisibleProperty, value); }
    public bool IsSearchBarVisible { get => GetValue(IsSearchBarVisibleProperty); set => SetValue(IsSearchBarVisibleProperty, value); }
    public bool IsStatusBarVisible { get => GetValue(IsStatusBarVisibleProperty); set => SetValue(IsStatusBarVisibleProperty, value); }
    public double SidebarWidth { get => GetValue(SidebarWidthProperty); set => SetValue(SidebarWidthProperty, value); }
    public int SidebarIndex { get => GetValue(SidebarIndexProperty); set => SetValue(SidebarIndexProperty, value); }
    public int PageNumber { get => GetValue(PageNumberProperty); set => SetValue(PageNumberProperty, value); }
    public double Zoom { get => GetValue(ZoomProperty); set => SetValue(ZoomProperty, value); }
    public PdfViewerZoomMode ZoomMode { get => GetValue(ZoomModeProperty); set => SetValue(ZoomModeProperty, value); }
    public PdfLayoutMode LayoutMode { get => GetValue(LayoutModeProperty); set => SetValue(LayoutModeProperty, value); }
    public PdfTool Tool { get => GetValue(ToolProperty); set => SetValue(ToolProperty, value); }
    public string PageText { get => GetValue(PageTextProperty); set => SetValue(PageTextProperty, value); }
    public string ZoomText { get => GetValue(ZoomTextProperty); set => SetValue(ZoomTextProperty, value); }
    public string SearchText { get => GetValue(SearchTextProperty); set => SetValue(SearchTextProperty, value); }
    public bool MatchCase { get => GetValue(MatchCaseProperty); set => SetValue(MatchCaseProperty, value); }
    public bool WholeWord { get => GetValue(WholeWordProperty); set => SetValue(WholeWordProperty, value); }
    public bool UseRegularExpression { get => GetValue(UseRegularExpressionProperty); set => SetValue(UseRegularExpressionProperty, value); }
    public int SearchResultIndex { get => GetValue(SearchResultIndexProperty); set => SetValue(SearchResultIndexProperty, value); }
    public string DialogValue { get => GetValue(DialogValueProperty); set => SetValue(DialogValueProperty, value); }

    private PdfWorkspace? _workspace;
    private int _pageCount;
    private bool _hasDocument, _canEdit, _canAnnotate, _isBusy, _isSearching, _isCompact, _hasError, _isDialogOpen, _isPasswordPrompt;
    private Thickness _viewportMargin;
    private string _documentTitle = "Open a PDF", _statusText = "No document", _errorMessage = "", _searchResultLabel = "No matches", _dialogTitle = "", _dialogMessage = "";
    private IReadOnlyList<PdfSearchHit> _searchResults = Array.Empty<PdfSearchHit>();
    public static readonly DirectProperty<PdfViewer, PdfWorkspace?> WorkspaceProperty = AvaloniaProperty.RegisterDirect<PdfViewer, PdfWorkspace?>(nameof(Workspace), o => o.Workspace);
    public static readonly DirectProperty<PdfViewer, int> PageCountProperty = AvaloniaProperty.RegisterDirect<PdfViewer, int>(nameof(PageCount), o => o.PageCount);
    public static readonly DirectProperty<PdfViewer, bool> HasDocumentProperty = AvaloniaProperty.RegisterDirect<PdfViewer, bool>(nameof(HasDocument), o => o.HasDocument);
    public static readonly DirectProperty<PdfViewer, bool> CanEditProperty = AvaloniaProperty.RegisterDirect<PdfViewer, bool>(nameof(CanEdit), o => o.CanEdit);
    public static readonly DirectProperty<PdfViewer, bool> CanAnnotateProperty = AvaloniaProperty.RegisterDirect<PdfViewer, bool>(nameof(CanAnnotate), o => o.CanAnnotate);
    public static readonly DirectProperty<PdfViewer, bool> IsBusyProperty = AvaloniaProperty.RegisterDirect<PdfViewer, bool>(nameof(IsBusy), o => o.IsBusy);
    public static readonly DirectProperty<PdfViewer, bool> IsSearchingProperty = AvaloniaProperty.RegisterDirect<PdfViewer, bool>(nameof(IsSearching), o => o.IsSearching);
    public static readonly DirectProperty<PdfViewer, bool> IsCompactProperty = AvaloniaProperty.RegisterDirect<PdfViewer, bool>(nameof(IsCompact), o => o.IsCompact);
    public static readonly DirectProperty<PdfViewer, Thickness> ViewportMarginProperty = AvaloniaProperty.RegisterDirect<PdfViewer, Thickness>(nameof(ViewportMargin), o => o.ViewportMargin);
    public static readonly DirectProperty<PdfViewer, string> DocumentTitleProperty = AvaloniaProperty.RegisterDirect<PdfViewer, string>(nameof(DocumentTitle), o => o.DocumentTitle);
    public static readonly DirectProperty<PdfViewer, string> StatusTextProperty = AvaloniaProperty.RegisterDirect<PdfViewer, string>(nameof(StatusText), o => o.StatusText);
    public static readonly DirectProperty<PdfViewer, string> ErrorMessageProperty = AvaloniaProperty.RegisterDirect<PdfViewer, string>(nameof(ErrorMessage), o => o.ErrorMessage);
    public static readonly DirectProperty<PdfViewer, bool> HasErrorProperty = AvaloniaProperty.RegisterDirect<PdfViewer, bool>(nameof(HasError), o => o.HasError);
    public static readonly DirectProperty<PdfViewer, string> SearchResultLabelProperty = AvaloniaProperty.RegisterDirect<PdfViewer, string>(nameof(SearchResultLabel), o => o.SearchResultLabel);
    public static readonly DirectProperty<PdfViewer, IReadOnlyList<PdfSearchHit>> SearchResultsProperty = AvaloniaProperty.RegisterDirect<PdfViewer, IReadOnlyList<PdfSearchHit>>(nameof(SearchResults), o => o.SearchResults);
    public static readonly DirectProperty<PdfViewer, bool> IsDialogOpenProperty = AvaloniaProperty.RegisterDirect<PdfViewer, bool>(nameof(IsDialogOpen), o => o.IsDialogOpen);
    public static readonly DirectProperty<PdfViewer, bool> IsPasswordPromptProperty = AvaloniaProperty.RegisterDirect<PdfViewer, bool>(nameof(IsPasswordPrompt), o => o.IsPasswordPrompt);
    public static readonly DirectProperty<PdfViewer, string> DialogTitleProperty = AvaloniaProperty.RegisterDirect<PdfViewer, string>(nameof(DialogTitle), o => o.DialogTitle);
    public static readonly DirectProperty<PdfViewer, string> DialogMessageProperty = AvaloniaProperty.RegisterDirect<PdfViewer, string>(nameof(DialogMessage), o => o.DialogMessage);
    public PdfWorkspace? Workspace => _workspace;
    public int PageCount => _pageCount;
    public bool HasDocument => _hasDocument;
    public bool CanEdit => _canEdit;
    public bool CanAnnotate => _canAnnotate;
    public bool IsBusy => _isBusy;
    public bool IsSearching => _isSearching;
    public bool IsCompact => _isCompact;
    public Thickness ViewportMargin => _viewportMargin;
    public string DocumentTitle => _documentTitle;
    public string StatusText => _statusText;
    public string ErrorMessage => _errorMessage;
    public bool HasError => _hasError;
    public string SearchResultLabel => _searchResultLabel;
    public IReadOnlyList<PdfSearchHit> SearchResults => _searchResults;
    public bool IsDialogOpen => _isDialogOpen;
    public bool IsPasswordPrompt => _isPasswordPrompt;
    public string DialogTitle => _dialogTitle;
    public string DialogMessage => _dialogMessage;
    public PdfLayoutMode[] LayoutModes { get; } = Enum.GetValues<PdfLayoutMode>();
    public PdfViewerSidebarSection[] SidebarSections { get; } = Enum.GetValues<PdfViewerSidebarSection>();
    public static readonly RoutedEvent<RoutedEventArgs> DocumentChangedEvent = RoutedEvent.Register<PdfViewer, RoutedEventArgs>(nameof(DocumentChanged), RoutingStrategies.Bubble);
    public static readonly RoutedEvent<PdfViewerPageChangedEventArgs> PageChangedEvent = RoutedEvent.Register<PdfViewer, PdfViewerPageChangedEventArgs>(nameof(PageChanged), RoutingStrategies.Bubble);
    public static readonly RoutedEvent<PdfViewerErrorEventArgs> ErrorOccurredEvent = RoutedEvent.Register<PdfViewer, PdfViewerErrorEventArgs>(nameof(ErrorOccurred), RoutingStrategies.Bubble);
    public static readonly RoutedEvent<RoutedEventArgs> SearchCompletedEvent = RoutedEvent.Register<PdfViewer, RoutedEventArgs>(nameof(SearchCompleted), RoutingStrategies.Bubble);
    public event EventHandler<RoutedEventArgs> DocumentChanged { add => AddHandler(DocumentChangedEvent, value); remove => RemoveHandler(DocumentChangedEvent, value); }
    public event EventHandler<PdfViewerPageChangedEventArgs> PageChanged { add => AddHandler(PageChangedEvent, value); remove => RemoveHandler(PageChangedEvent, value); }
    public event EventHandler<PdfViewerErrorEventArgs> ErrorOccurred { add => AddHandler(ErrorOccurredEvent, value); remove => RemoveHandler(ErrorOccurredEvent, value); }
    public event EventHandler<RoutedEventArgs> SearchCompleted { add => AddHandler(SearchCompletedEvent, value); remove => RemoveHandler(SearchCompletedEvent, value); }
}
