using ProPDF.Core;

namespace ProPDF.Presentation;

/// <summary>Optional host service. The workspace asks for explicit confirmation before invoking external navigation.</summary>
public interface IPdfExternalNavigation
{
    Task OpenUriAsync(Uri uri, CancellationToken cancellationToken);
}

public sealed partial class PdfViewportController
{
    private sealed record NavigationPosition(Guid Revision, int Page, double Zoom, PdfPoint Offset, PdfLayoutMode Mode);
    private readonly List<NavigationPosition> _navigationBack = [];
    private readonly List<NavigationPosition> _navigationForward = [];
    public bool CanNavigateBack { get { lock (_gate) return _navigationBack.Any(position => position.Revision == _snapshot?.Id); } }
    public bool CanNavigateForward { get { lock (_gate) return _navigationForward.Any(position => position.Revision == _snapshot?.Id); } }

    /// <summary>Navigates only in-document destinations. URI activation is an explicit, separate host operation.</summary>
    public void NavigateTo(PdfNavigationTarget target, Guid expectedRevision)
    {
        ArgumentNullException.ThrowIfNull(target);
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_snapshot is null || _snapshot.Id != expectedRevision) throw new PdfRevisionConflictException();
            if (!target.IsSupported || target.ExternalUri is not null) throw new NotSupportedException(target.UnsupportedReason ?? "External links require host confirmation.");
            if (target.X is { } tx && !double.IsFinite(tx) || target.Y is { } ty && !double.IsFinite(ty) ||
                target.Zoom is { } zoom && (!double.IsFinite(zoom) || zoom <= 0) || !Enum.IsDefined(target.Fit))
                throw new ArgumentOutOfRangeException(nameof(target));
            var number = target.NamedAction switch
            {
                "FirstPage" => 1,
                "LastPage" => _snapshot.Pages.Count,
                "NextPage" => Math.Min(_snapshot.Pages.Count, _currentPage + 1),
                "PrevPage" => Math.Max(1, _currentPage - 1),
                null => target.PageNumber,
                _ => throw new NotSupportedException("Unsupported named navigation action.")
            };
            var page = _snapshot.GetPage(number);
            _navigationBack.RemoveAll(position => position.Revision != _snapshot.Id);
            _navigationForward.Clear();
            _navigationBack.Add(new NavigationPosition(_snapshot.Id, _currentPage, _zoom, _offset, _mode));
            if (_navigationBack.Count > 64) _navigationBack.RemoveAt(0);
            _currentPage = number;
            var factor = 96d / 72;
            var region = target.Region;
            _zoom = target.Fit switch
            {
                PdfDestinationFit.Page => Math.Min((_viewport.Width - 40) / (page.Size.Width * factor), (_viewport.Height - 40) / (page.Size.Height * factor)),
                PdfDestinationFit.Width => (_viewport.Width - 40) / (page.Size.Width * factor),
                PdfDestinationFit.Height => (_viewport.Height - 40) / (page.Size.Height * factor),
                PdfDestinationFit.Rectangle when region is { IsEmpty: false } rectangle =>
                    Math.Min((_viewport.Width - 40) / (rectangle.Width * factor), (_viewport.Height - 40) / (rectangle.Height * factor)),
                _ => target.Zoom ?? _zoom
            };
            _zoom = Math.Clamp(_zoom, 0.05, 16);
            RebuildLayout();
            if (_placements.TryGetValue(number, out var placement))
            {
                var x = region?.X ?? target.X ?? 0;
                var y = region?.Y ?? target.Y ?? 0;
                _offset = new PdfPoint(placement.Bounds.X + Math.Clamp(x, 0, page.Size.Width) * _layout!.Scale - 20,
                    placement.Bounds.Y + Math.Clamp(y, 0, page.Size.Height) * _layout.Scale - 20);
                ClampOffset();
            }
            _currentPage = number;
        }
        ScheduleRender();
    }

    public void NavigateBack() => NavigateHistory(true);
    public void NavigateForward() => NavigateHistory(false);
    private void NavigateHistory(bool back)
    {
        lock (_gate)
        {
            if (_disposed || _snapshot is null) return;
            var from = back ? _navigationBack : _navigationForward;
            var to = back ? _navigationForward : _navigationBack;
            from.RemoveAll(position => position.Revision != _snapshot.Id);
            if (from.Count == 0) return;
            var target = from[^1];
            from.RemoveAt(from.Count - 1);
            to.Add(new NavigationPosition(_snapshot.Id, _currentPage, _zoom, _offset, _mode));
            if (to.Count > 64) to.RemoveAt(0);
            _currentPage = target.Page;
            _zoom = target.Zoom;
            _mode = target.Mode;
            RebuildLayout();
            _offset = target.Offset;
            ClampOffset();
            _currentPage = target.Page;
        }
        ScheduleRender();
    }
}

public sealed partial class PdfWorkspace
{
    private Guid? _navigationRequested;
    private PdfDocumentNavigation? _navigation;
    private Task _navigationTask = Task.CompletedTask;
    private PdfNavigationBookmark? _selectedBookmark;
    private PdfNavigationLink? _selectedLink;
    private PdfUiCommand? _reloadNavigation, _followBookmark, _followLink, _copyLink, _back, _forward;
    private PdfUiCommand? _insertChild, _renameBookmark, _deleteBookmark, _linkSelection;
    public bool HasNavigationService => _context.Inspector is IPdfNavigationService;

    // These getters start one asynchronous, revision-keyed load; parsing never occurs on the UI thread.
    // The existing workspace document notifications invalidate them without adding event subscriptions.
    public IReadOnlyList<PdfNavigationBookmark> NavigationBookmarks { get { EnsureNavigationLoaded(); return _navigation?.Bookmarks ?? Array.Empty<PdfNavigationBookmark>(); } }
    public IReadOnlyList<PdfNavigationLink> NavigationLinks { get { EnsureNavigationLoaded(); return _navigation?.Links ?? Array.Empty<PdfNavigationLink>(); } }
    public PdfNavigationBookmark? SelectedBookmark
    {
        get => _selectedBookmark;
        set { if (Set(ref _selectedBookmark, value)) RefreshCommands(); }
    }
    public PdfNavigationLink? SelectedLink
    {
        get => _selectedLink;
        set { if (Set(ref _selectedLink, value)) RefreshCommands(); }
    }
    private bool NavigationIsCurrent => _navigation?.Revision == Document?.Id && _navigation is not null;
    public PdfUiCommand ReloadNavigationCommand => _reloadNavigation ??= Command(async _ => { _navigationRequested = null; EnsureNavigationLoaded(); await _navigationTask; }, () => HasDocument() && HasNavigationService);
    public PdfUiCommand FollowBookmarkCommand => _followBookmark ??= Command(token => FollowAsync(SelectedBookmark!.Target, _navigation!.Revision, token),
        () => NavigationIsCurrent && SelectedBookmark?.Target.IsSupported == true);
    public PdfUiCommand FollowLinkCommand => _followLink ??= Command(token => FollowAsync(SelectedLink!.Target, _navigation!.Revision, token),
        () => NavigationIsCurrent && SelectedLink?.Target.IsSupported == true);
    public PdfUiCommand CopyLinkCommand => _copyLink ??= Command(token => _dialogs.CopyTextAsync(SelectedLink!.Target.ExternalUri!, token),
        () => NavigationIsCurrent && SelectedLink?.Target.ExternalUri is not null);
    public PdfUiCommand BackCommand => _back ??= Action(Viewport.NavigateBack, () => Viewport.CanNavigateBack);
    public PdfUiCommand ForwardCommand => _forward ??= Action(Viewport.NavigateForward, () => Viewport.CanNavigateForward);
    public PdfUiCommand InsertChildBookmarkCommand => _insertChild ??= Command(token =>
        Session.ApplyAsync(new InsertOutline(ToolText, Viewport.CurrentPage, SelectedBookmark!.Path), _navigation!.Revision, token),
        () => Can(PdfCapability.Bookmarks) && NavigationIsCurrent && SelectedBookmark is not null);
    public PdfUiCommand RenameBookmarkCommand => _renameBookmark ??= Command(token =>
        Session.ApplyAsync(new UpdateOutline(SelectedBookmark!.Path, ToolText), _navigation!.Revision, token),
        () => Can(PdfCapability.Bookmarks) && NavigationIsCurrent && SelectedBookmark is not null);
    public PdfUiCommand DeleteBookmarkCommand => _deleteBookmark ??= Command(async token =>
    {
        var path = SelectedBookmark!.Path;
        var revision = _navigation!.Revision;
        if (await _dialogs.ConfirmAsync("Delete bookmark", "Delete this bookmark and all its children?", token))
            await Session.ApplyAsync(new DeleteOutline(path), revision, token);
    }, () => Can(PdfCapability.Bookmarks) && NavigationIsCurrent && SelectedBookmark is not null);
    public PdfUiCommand LinkSelectionCommand => _linkSelection ??= Command(token =>
    {
        var selection = Viewport.Selection ?? throw new InvalidOperationException("Select a link region first.");
        if (!int.TryParse(ToolText, out var page) || page < 1 || page > Viewport.PageCount)
            throw new ArgumentException("Enter the destination page number in the toolbar text box.");
        return Session.ApplyAsync(new AddInternalLink(selection.PageNumber, selection.Bounds, page), selection.Revision, token);
    }, () => Can(PdfCapability.Annotations) && HasSelection());

    public Task LoadNavigationAsync()
    {
        EnsureNavigationLoaded();
        return _navigationTask;
    }
    private void EnsureNavigationLoaded()
    {
        if (_disposed) return;
        var document = Document;
        if (_navigationRequested == document?.Id) return;
        _navigationRequested = document?.Id;
        _navigation = null;
        _selectedBookmark = null;
        _selectedLink = null;
        if (document is not null && _context.Inspector is IPdfNavigationService service)
            _navigationTask = LoadNavigationCoreAsync(service, document, _lifetime.Token);
    }
    private async Task LoadNavigationCoreAsync(IPdfNavigationService service, PdfSnapshot document, CancellationToken token)
    {
        try
        {
            var data = await service.ReadNavigationAsync(document, cancellationToken: token).ConfigureAwait(false);
            _dispatch(() =>
            {
                if (_disposed || Document?.Id != document.Id || _navigationRequested != document.Id) return;
                _navigation = data;
                Changed(nameof(NavigationBookmarks));
                Changed(nameof(NavigationLinks));
                RefreshCommands();
            });
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        catch (Exception error) { if (!_disposed) Viewport.ReportError(error); }
    }
    private async Task FollowAsync(PdfNavigationTarget target, Guid revision, CancellationToken token)
    {
        if (Document?.Id != revision) throw new PdfRevisionConflictException();
        if (!target.IsSupported) throw new NotSupportedException(target.UnsupportedReason);
        if (target.ExternalUri is { } value)
        {
            if (!PdfUriPolicy.TryNormalize(value, out var uri)) throw new InvalidOperationException("The URI is not allowed.");
            if (_dialogs is not IPdfExternalNavigation host) throw new NotSupportedException("This host has not opted into external-link activation. Copy the URI instead.");
            if (await _dialogs.ConfirmAsync("Open external link", "Open this address in the system application?\n\n" + uri!.AbsoluteUri, token))
            {
                if (Document?.Id != revision) throw new PdfRevisionConflictException();
                await host.OpenUriAsync(uri!, token);
            }
        }
        else Viewport.NavigateTo(target, revision);
    }
}
