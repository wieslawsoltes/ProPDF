using System.ComponentModel;
using System.Globalization;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Automation.Peers;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Threading;
using ProPDF.Core;
using ProPDF.Presentation;

namespace ProPDF.Avalonia;

public partial class PdfViewer
{
    private readonly List<PdfViewerCommand> _commands = [];
    private PdfViewerViewport? _viewport;
    private TextBox? _searchBox, _dialogInput;
    private Button? _dialogAccept;
    private PdfViewportController? _observed;
    private PdfViewerStorageDialogs? _ownedDialogs;
    private CancellationTokenSource? _lifetime, _searchCancellation;
    private bool _attached, _syncing, _loading;
    private int _notificationQueued, _fitQueued;
    private double _fitZoom = double.NaN;
    private Guid? _revision;
    private int _reportedPage;
    private string _ownError = "";

    public PdfViewer()
    {
        Focusable = true;
        AutomationProperties.SetName(this, "PDF viewer");
        FirstPageCommand = Action(() => Navigate(1), () => HasDocument && PageNumber > 1);
        PreviousPageCommand = Action(() => Navigate(PageNumber - 1), () => HasDocument && PageNumber > 1);
        NextPageCommand = Action(() => Navigate(PageNumber + 1), () => HasDocument && PageNumber < PageCount);
        LastPageCommand = Action(() => Navigate(PageCount), () => HasDocument && PageNumber < PageCount);
        GoToPageCommand = Action(() => { if (!int.TryParse(PageText, out var n) || n < 1 || n > PageCount) throw new ArgumentException("Enter a page number within the document."); Navigate(n); }, () => HasDocument);
        ZoomInCommand = Action(() => SetManualZoom(Zoom * 1.15), () => HasDocument && Zoom < 16);
        ZoomOutCommand = Action(() => SetManualZoom(Zoom / 1.15), () => HasDocument && Zoom > .05);
        SetZoomCommand = Action(() => { var value = (ZoomText ?? "").Trim().TrimEnd('%'); if (!double.TryParse(value, NumberStyles.Float, CultureInfo.CurrentCulture, out var n) || !double.IsFinite(n) || n < 5 || n > 1600) throw new ArgumentException("Enter a zoom percentage from 5 to 1600."); SetManualZoom(n / 100); }, () => HasDocument);
        FitWidthCommand = Action(() => { SetCurrentValue(ZoomModeProperty, PdfViewerZoomMode.FitWidth); QueueFit(); }, () => HasDocument);
        FitPageCommand = Action(() => { SetCurrentValue(ZoomModeProperty, PdfViewerZoomMode.FitPage); QueueFit(); }, () => HasDocument);
        ToggleSidebarCommand = Action(() => SetCurrentValue(IsSidebarVisibleProperty, !IsSidebarVisible));
        ToggleSearchCommand = Action(() => { SetCurrentValue(IsSearchBarVisibleProperty, !IsSearchBarVisible); if (IsSearchBarVisible) Dispatcher.UIThread.Post(() => _searchBox?.Focus()); });
        SearchCommand = Command(_ => SearchAsync(), _ => HasDocument);
        CancelSearchCommand = Action(CancelSearch, () => IsSearching);
        NextMatchCommand = Action(() => Context?.Viewport.NextSearchResult(), () => SearchResults.Count > 0);
        PreviousMatchCommand = Action(() => Context?.Viewport.NextSearchResult(true), () => SearchResults.Count > 0);
        SetToolCommand = Command(p => { if (Enum.TryParse<PdfTool>(p?.ToString(), out var tool) && ToolAllowed(tool)) SetCurrentValue(ToolProperty, tool); return Task.CompletedTask; }, p => Enum.TryParse<PdfTool>(p?.ToString(), out var tool) && ToolAllowed(tool));
        UndoCommand = Command(_ => Context!.Session.UndoAsync(), _ => CanEdit && Context!.Session.CanUndo);
        RedoCommand = Command(_ => Context!.Session.RedoAsync(), _ => CanEdit && Context!.Session.CanRedo);
        PrintCommand = Command(_ => PrintService!.PrintAsync(Context!.Session.Current!, _lifetime!.Token), _ => HasDocument && PrintService is not null);
        ClearErrorCommand = Action(() => { _ownError = ""; Context?.Viewport.ClearError(); Synchronize(); });
        AcceptDialogCommand = new PdfViewerCommand(_ => { CompletePrompt(true); return Task.CompletedTask; }, _ => IsDialogOpen, ReportError);
        CancelDialogCommand = new PdfViewerCommand(_ => { CompletePrompt(false); return Task.CompletedTask; }, _ => IsDialogOpen, ReportError);
        AddHandler(KeyDownEvent, HandleKey, RoutingStrategies.Tunnel);
    }
    public PdfViewerCommand FirstPageCommand { get; }
    public PdfViewerCommand PreviousPageCommand { get; }
    public PdfViewerCommand NextPageCommand { get; }
    public PdfViewerCommand LastPageCommand { get; }
    public PdfViewerCommand GoToPageCommand { get; }
    public PdfViewerCommand ZoomInCommand { get; }
    public PdfViewerCommand ZoomOutCommand { get; }
    public PdfViewerCommand SetZoomCommand { get; }
    public PdfViewerCommand FitWidthCommand { get; }
    public PdfViewerCommand FitPageCommand { get; }
    public PdfViewerCommand ToggleSidebarCommand { get; }
    public PdfViewerCommand ToggleSearchCommand { get; }
    public PdfViewerCommand SearchCommand { get; }
    public PdfViewerCommand CancelSearchCommand { get; }
    public PdfViewerCommand NextMatchCommand { get; }
    public PdfViewerCommand PreviousMatchCommand { get; }
    public PdfViewerCommand SetToolCommand { get; }
    public PdfViewerCommand UndoCommand { get; }
    public PdfViewerCommand RedoCommand { get; }
    public PdfViewerCommand PrintCommand { get; }
    public PdfViewerCommand ClearErrorCommand { get; }
    public PdfViewerCommand AcceptDialogCommand { get; }
    public PdfViewerCommand CancelDialogCommand { get; }
    private PdfViewerCommand Command(Func<object?, Task> execute, Func<object?, bool>? can = null)
    {
        var command = new PdfViewerCommand(execute, p => _attached && !IsBusy && !IsDialogOpen && (can?.Invoke(p) ?? true), ReportError);
        _commands.Add(command); return command;
    }
    private PdfViewerCommand Action(Action action, Func<bool>? can = null) => Command(_ => { action(); return Task.CompletedTask; }, _ => can?.Invoke() ?? true);
    private void RefreshCommands() { foreach (var c in _commands) c.Refresh(); AcceptDialogCommand?.Refresh(); CancelDialogCommand?.Refresh(); }
    protected override void OnApplyTemplate(TemplateAppliedEventArgs e)
    {
        if (_viewport is not null) _viewport.PageTapped -= PageTapped;
        base.OnApplyTemplate(e);
        _viewport = e.NameScope.Find<PdfViewerViewport>("PART_Viewport");
        _searchBox = e.NameScope.Find<TextBox>("PART_SearchBox");
        _dialogInput = e.NameScope.Find<TextBox>("PART_DialogInput");
        _dialogAccept = e.NameScope.Find<Button>("DialogAcceptButton");
        if (_viewport is not null) _viewport.PageTapped += PageTapped;
        Synchronize(); QueueFit();
    }
    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e) { base.OnAttachedToVisualTree(e); _attached = true; Connect(); }
    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e) { _attached = false; Disconnect(); base.OnDetachedFromVisualTree(e); }
    private void Connect()
    {
        Disconnect();
        if (!_attached || Context is not { } context) { Synchronize(); return; }
        _lifetime = new CancellationTokenSource();
        var dialogs = Dialogs ?? (_ownedDialogs = new PdfViewerStorageDialogs(this));
        SetAndRaise(WorkspaceProperty, ref _workspace, new PdfWorkspace(context, dialogs, action => Dispatcher.UIThread.Post(action)));
        _workspace!.PropertyChanged += WorkspaceChanged;
        _observed = context.Viewport;
        _observed.Invalidated += ViewportInvalidated;
        if (_observed.LayoutMode != LayoutMode) _observed.SetLayoutMode(LayoutMode);
        _observed.Tool = ToolAllowed(Tool) ? Tool : PdfTool.Pan;
        if (ZoomMode == PdfViewerZoomMode.Custom) _observed.SetZoom(Zoom);
        Synchronize(); QueueFit();
    }
    private void Disconnect()
    {
        CancelSearch(); CompletePrompt(false);
        _lifetime?.Cancel(); _lifetime?.Dispose(); _lifetime = null;
        if (_observed is not null) { _observed.Invalidated -= ViewportInvalidated; _observed.CancelInteraction(); }
        _observed = null;
        if (_workspace is not null) { _workspace.PropertyChanged -= WorkspaceChanged; _workspace.Dispose(); }
        SetAndRaise(WorkspaceProperty, ref _workspace, null);
        _ownedDialogs?.Dispose(); _ownedDialogs = null;
        if (_viewport is not null) _viewport.Controller = null;
        _revision = null; _reportedPage = 0;
        RefreshCommands();
    }
    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == ContextProperty || change.Property == DialogsProperty) { if (_attached) Connect(); return; }
        if (_syncing) return;
        var c = Context?.Viewport;
        if (change.Property == IsReadOnlyProperty)
        {
            if (IsReadOnly && c is not null) { c.CancelInteraction(); if (!ToolAllowed(c.Tool)) c.Tool = PdfTool.Pan; }
            Synchronize();
        }
        else if (change.Property == PageNumberProperty && c?.Document is not null) { Navigate(Math.Clamp(PageNumber, 1, c.PageCount)); Synchronize(); }
        else if (change.Property == ZoomProperty && c is not null) SetManualZoom(Zoom);
        else if (change.Property == ZoomModeProperty) QueueFit();
        else if (change.Property == LayoutModeProperty && c is not null) { c.SetLayoutMode(LayoutMode); QueueFit(); }
        else if (change.Property == ToolProperty && c is not null) { c.Tool = ToolAllowed(Tool) ? Tool : PdfTool.Pan; Synchronize(); }
        else if (change.Property == SearchTextProperty || change.Property == MatchCaseProperty || change.Property == WholeWordProperty || change.Property == UseRegularExpressionProperty) QueueSearch();
        else if (change.Property == SearchResultIndexProperty && c is not null && SearchResultIndex >= 0 && SearchResultIndex < c.SearchHits.Count) c.GoToSearchResult(SearchResultIndex);
        else if (change.Property == IsSidebarVisibleProperty || change.Property == SidebarWidthProperty) { UpdateResponsive(); QueueFit(); }
        else if (change.Property == PrintServiceProperty) RefreshCommands();
    }
    protected override Size ArrangeOverride(Size finalSize)
    {
        var result = base.ArrangeOverride(finalSize);
        UpdateResponsive(finalSize.Width); QueueFit(); return result;
    }
    private void UpdateResponsive(double? width = null)
    {
        SetAndRaise(IsCompactProperty, ref _isCompact, (width ?? Bounds.Width) < 760);
        SetAndRaise(ViewportMarginProperty, ref _viewportMargin, new Thickness(IsSidebarVisible && !IsCompact ? SidebarWidth : 0, 0, 0, 0));
    }
    private void WorkspaceChanged(object? sender, PropertyChangedEventArgs e) => QueueSynchronize();
    private void ViewportInvalidated(object? sender, EventArgs e) => QueueSynchronize();
    private void QueueSynchronize()
    {
        if (Interlocked.Exchange(ref _notificationQueued, 1) != 0) return;
        Dispatcher.UIThread.Post(() => { Interlocked.Exchange(ref _notificationQueued, 0); if (_attached) Synchronize(); });
    }
    private void Synchronize()
    {
        if (_syncing) return;
        _syncing = true;
        var c = _attached ? Context?.Viewport : null;
        var oldPage = _reportedPage;
        var revision = c?.Document?.Id;
        var documentChanged = revision != _revision;
        try
        {
            SetAndRaise(HasDocumentProperty, ref _hasDocument, c?.Document is not null);
            SetAndRaise(PageCountProperty, ref _pageCount, c?.PageCount ?? 0);
            SetAndRaise(CanEditProperty, ref _canEdit, HasDocument && !IsReadOnly);
            SetAndRaise(CanAnnotateProperty, ref _canAnnotate, CanEdit && Context!.Session.Capabilities.Contains(PdfCapability.Annotations));
            SetAndRaise(IsBusyProperty, ref _isBusy, _loading || (Workspace?.IsBusy ?? false));
            var page = c?.CurrentPage ?? 1;
            if (page != _reportedPage || documentChanged) SetCurrentValue(PageTextProperty, page.ToString(CultureInfo.CurrentCulture));
            var zoom = c?.Zoom ?? 1;
            if (Math.Abs(zoom - Zoom) > .00001)
            {
                if (!documentChanged && Math.Abs(zoom - _fitZoom) > .00001) SetCurrentValue(ZoomModeProperty, PdfViewerZoomMode.Custom);
                SetCurrentValue(ZoomTextProperty, (zoom * 100).ToString("0.#", CultureInfo.CurrentCulture) + "%");
            }
            SetCurrentValue(PageNumberProperty, page); SetCurrentValue(ZoomProperty, zoom);
            if (c is not null) { SetCurrentValue(LayoutModeProperty, c.LayoutMode); SetCurrentValue(ToolProperty, ToolAllowed(c.Tool) ? c.Tool : PdfTool.Pan); }
            SetAndRaise(SearchResultsProperty, ref _searchResults, c?.SearchHits ?? Array.Empty<PdfSearchHit>());
            SetCurrentValue(SearchResultIndexProperty, c?.SearchIndex ?? -1);
            SetAndRaise(SearchResultLabelProperty, ref _searchResultLabel, SearchResults.Count == 0 ? "No matches" : $"{(c?.SearchIndex ?? -1) + 1} / {SearchResults.Count}");
            SetAndRaise(DocumentTitleProperty, ref _documentTitle, Workspace?.DisplayName ?? "Open a PDF");
            SetAndRaise(ErrorMessageProperty, ref _errorMessage, _ownError.Length != 0 ? _ownError : c?.LastError ?? "");
            SetAndRaise(HasErrorProperty, ref _hasError, ErrorMessage.Length > 0);
            SetAndRaise(StatusTextProperty, ref _statusText, IsSearching ? "Searching document…" : Workspace?.Status ?? "Open a PDF to begin");
            _reportedPage = page; _revision = revision;
            if (_viewport is not null) { _viewport.Controller = c; _viewport.IsReadOnly = IsReadOnly; }
            PseudoClasses.Set(":empty", !HasDocument); PseudoClasses.Set(":busy", IsBusy); PseudoClasses.Set(":readonly", IsReadOnly); PseudoClasses.Set(":error", HasError);
            AutomationProperties.SetName(this, $"PDF viewer, {DocumentTitle}, page {page} of {PageCount}");
            UpdateResponsive();
        }
        finally { _syncing = false; }
        RefreshCommands();
        if (!IsBusy && !IsDialogOpen) _ownedDialogs?.ReleaseCompletedOperations();
        if (documentChanged) { RaiseEvent(new RoutedEventArgs(DocumentChangedEvent)); QueueFit(); }
        if (oldPage != _reportedPage) { RaiseEvent(new PdfViewerPageChangedEventArgs(PageChangedEvent, oldPage, _reportedPage)); if (ZoomMode == PdfViewerZoomMode.FitPage) QueueFit(); }
    }
    private bool ToolAllowed(PdfTool tool) => tool is PdfTool.Pan or PdfTool.SelectText or PdfTool.SelectRegion ||
        (!IsReadOnly && Context?.Session.Current is not null && Context.Session.Capabilities.Contains(PdfCapability.Annotations) && tool is PdfTool.Highlight or PdfTool.Note or PdfTool.Ink or PdfTool.FreeText);
    private void Navigate(int page)
    {
        if (Context?.Session.Current is { } doc) Context.Viewport.NavigateTo(new PdfNavigationTarget(Math.Clamp(page, 1, doc.Pages.Count), PdfDestinationFit.KeepZoom), doc.Id);
    }
    private void SetManualZoom(double value)
    {
        SetCurrentValue(ZoomModeProperty, PdfViewerZoomMode.Custom);
        Context?.Viewport.SetZoom(value); Synchronize();
    }
    private void QueueFit()
    {
        if (!_attached || ZoomMode == PdfViewerZoomMode.Custom || Interlocked.Exchange(ref _fitQueued, 1) != 0) return;
        Dispatcher.UIThread.Post(() => { Interlocked.Exchange(ref _fitQueued, 0); if (_attached && ZoomMode != PdfViewerZoomMode.Custom && Context?.Viewport.Document is not null) { ApplyFit(Context.Viewport); Synchronize(); } }, DispatcherPriority.Loaded);
    }
    private void ApplyFit(PdfViewportController c)
    {
        var pages = c.Document!.Pages;
        if (pages.Count == 0 || c.Viewport.Width < 60 || c.Viewport.Height < 60) return;
        var current = Math.Clamp(c.CurrentPage - 1, 0, pages.Count - 1);
        var width = pages[current].Size.Width;
        var height = pages[current].Size.Height;
        var gap = 0.0;
        if (c.LayoutMode == PdfLayoutMode.Facing && current > 0)
        {
            var left = current % 2 == 1 ? current : current - 1;
            width = pages[left].Size.Width; height = pages[left].Size.Height;
            if (left + 1 < pages.Count) { width += pages[left + 1].Size.Width; height = Math.Max(height, pages[left + 1].Size.Height); gap = 20; }
        }
        if (ZoomMode == PdfViewerZoomMode.FitWidth && c.LayoutMode != PdfLayoutMode.SinglePage)
        {
            width = pages[0].Size.Width; gap = 0;
            if (c.LayoutMode == PdfLayoutMode.Continuous) width = pages.Max(p => p.Size.Width);
            else for (var i = 1; i < pages.Count; i += 2)
            {
                var row = pages[i].Size.Width + (i + 1 < pages.Count ? pages[i + 1].Size.Width : 0);
                if (row > width) { width = row; gap = i + 1 < pages.Count ? 20 : 0; }
            }
        }
        var zoom = (c.Viewport.Width - 48 - gap) / (width * 96 / 72);
        if (ZoomMode == PdfViewerZoomMode.FitPage) zoom = Math.Min(zoom, (c.Viewport.Height - 48) / (height * 96 / 72));
        _fitZoom = Math.Clamp(zoom, .05, 16);
        // Automatic width fitting follows the top of the reading position, not the
        // viewport center. Center anchoring can reveal the previous page when a
        // sidebar or replacement template narrows the surface and reduces zoom.
        PdfPoint? anchor = null;
        if (c.GetPageViewportBounds(current + 1) is { Width: > 2, Height: > 2 } bounds)
            anchor = new PdfPoint(Math.Clamp(c.Viewport.Width / 2, bounds.X + 1, bounds.Right - 1),
                Math.Clamp(Math.Max(0, bounds.Y) + 1, bounds.Y + 1, bounds.Bottom - 1));
        c.SetZoom(_fitZoom, anchor);
        if (ZoomMode == PdfViewerZoomMode.FitPage) c.GoToPage(current + 1);
    }
    private async void PageTapped(object? sender, PdfViewerPageTappedEventArgs e)
    {
        if (Workspace is not { } workspace || IsBusy || IsDialogOpen) return;
        var revision = workspace.Document?.Id;
        try
        {
            await workspace.LoadNavigationAsync();
            if (workspace != Workspace || revision != workspace.Document?.Id) return;
            var link = workspace.NavigationLinks.FirstOrDefault(l => l.PageNumber == e.PageNumber && l.Bounds.Contains(e.Point));
            if (link is not null) { workspace.SelectedLink = link; await workspace.FollowLinkCommand.ExecuteAsync(); }
        }
        catch (Exception error) { ReportError(error); }
    }
    internal void ReportError(Exception error)
    {
        _ownError = error.Message; Synchronize(); RaiseEvent(new PdfViewerErrorEventArgs(ErrorOccurredEvent, error.Message));
    }
    protected override AutomationPeer OnCreateAutomationPeer() => new PdfViewerAutomationPeer(this);
    private sealed class PdfViewerAutomationPeer(PdfViewer owner) : ControlAutomationPeer(owner)
    {
        protected override string GetClassNameCore() => nameof(PdfViewer);
        protected override AutomationControlType GetAutomationControlTypeCore() => AutomationControlType.Document;
    }
}
