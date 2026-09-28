using System.ComponentModel;
using ProPDF.Core;
using ProPDF.Rendering.Skia;

namespace ProPDF.Presentation;

public enum PdfTool { Pan, SelectText, SelectRegion, Highlight, Note, FreeText, Text, Rectangle, Ellipse, Ink, Redact, ReplaceText, TextField, CheckBox, EditObject, TextBox }
public sealed record PdfSelection(Guid Revision, int PageNumber, PdfRect Bounds);

/// <summary>UI-independent, thread-safe viewport and editor interaction model. The host owns the session and renderer.</summary>
public sealed partial class PdfViewportController : INotifyPropertyChanged, IAsyncDisposable
{
    private readonly object _gate = new();
    private readonly SkiaPdfRenderer _renderer;
    private readonly IPdfTextService _textService;
    private readonly Action<Action> _dispatch;
    private readonly CancellationTokenSource _lifetime = new();
    private readonly HashSet<Task> _pending = [];
    private CancellationTokenSource? _renderCancellation;
    private Task _lastRender = Task.CompletedTask;
    private long _generation;
    private Guid? _plannedRevision;
    private SkiaTileRequest[] _plannedRequests = [];
    private int _notificationQueued;
    private PdfSnapshot? _snapshot;
    private PdfPageLayout? _layout;
    private Dictionary<int, PdfPagePlacement> _placements = [];
    private List<SkiaTileLease> _tiles = [];
    private Guid? _tileRevision;
    private PdfSize _viewport = new(900, 700);
    private PdfPoint _offset;
    private double _zoom = 1;
    private double _pixelsPerDip = 1;
    private int _currentPage = 1;
    private PdfLayoutMode _mode;
    private bool _disposed;
    private bool _isRendering;
    private string? _lastError;
    private string? _renderError;
    private PdfSelection? _selection;
    private IReadOnlyList<PdfSearchHit> _searchHits = Array.Empty<PdfSearchHit>();
    private int _searchIndex = -1;
    private string _selectedText = "";
    private readonly List<PdfSelection> _redactions = [];
    private PdfTool _tool = PdfTool.Pan;
    private string _toolText = "Review note";

    public PdfViewportController(PdfSession session, SkiaPdfRenderer renderer, IPdfTextService textService, Action<Action>? dispatch = null)
    {
        Session = session ?? throw new ArgumentNullException(nameof(session));
        _renderer = renderer ?? throw new ArgumentNullException(nameof(renderer));
        _textService = textService ?? throw new ArgumentNullException(nameof(textService));
        var context = SynchronizationContext.Current;
        _dispatch = dispatch ?? (context is null ? action => action() : action => context.Post(_ => action(), null));
        Session.Changed += SessionChanged;
        ReloadDocument();
    }

    public PdfSession Session { get; }
    public PdfSnapshot? Document { get { lock (_gate) return _snapshot; } }
    public double PixelsPerDip { get { lock (_gate) return _pixelsPerDip; } }
    public double Zoom { get { lock (_gate) return _zoom; } }
    public PdfPoint Offset { get { lock (_gate) return _offset; } }
    public PdfSize Viewport { get { lock (_gate) return _viewport; } }
    public PdfSize Extent { get { lock (_gate) return _layout?.Extent ?? _viewport; } }
    public int CurrentPage { get { lock (_gate) return _currentPage; } }
    public int PageCount { get { lock (_gate) return _snapshot?.Pages.Count ?? 0; } }
    public PdfLayoutMode LayoutMode { get { lock (_gate) return _mode; } }
    public bool IsRendering { get { lock (_gate) return _isRendering; } }
    public string? LastError { get { lock (_gate) return _lastError ?? _renderError; } }
    public PdfSelection? Selection { get { lock (_gate) return _selection; } }
    public string SelectedText { get { lock (_gate) return _selectedText; } }
    public IReadOnlyList<PdfSearchHit> SearchHits { get { lock (_gate) return _searchHits; } }
    public int SearchIndex { get { lock (_gate) return _searchIndex; } }
    public int PendingRedactions { get { lock (_gate) return _redactions.Count; } }
    public PdfTool Tool { get { lock (_gate) return _tool; } set { lock (_gate) _tool = value; CancelInteraction(); Notify(); } }
    public string ToolText { get { lock (_gate) return _toolText; } set { lock (_gate) _toolText = value ?? ""; Notify(); } }
    public event PropertyChangedEventHandler? PropertyChanged;
    public event EventHandler? Invalidated;

    private void SessionChanged(object? sender, PdfSessionChangedEventArgs args)
    {
        if (args.Kind != PdfChangeKind.Saved) ReloadDocument();
        else Notify();
    }

    private void ReloadDocument()
    {
        lock (_gate)
        {
            if (_disposed) return;
            var current = Session.Current;
            if (current?.Id == _snapshot?.Id) return;
            _snapshot = current;
            ResetContentLocked();
            _currentPage = Math.Clamp(_currentPage, 1, Math.Max(1, current?.Pages.Count ?? 1));
            _selection = null;
            _searchHits = Array.Empty<PdfSearchHit>();
            _searchIndex = -1;
            _selectedText = "";
            _redactions.Clear();
            _drag = null;
            foreach (var tile in _tiles) tile.Dispose();
            _tiles.Clear();
            _tileRevision = null;
            _lastError = null; _renderError = null;
            RebuildLayout();
        }
        ScheduleRender();
    }

    private void RebuildLayout()
    {
        _layout = _snapshot is null ? null : PdfPageLayout.Create(_snapshot.Pages, _viewport.Width, _zoom, _mode, _currentPage);
        _placements = _layout?.Pages.ToDictionary(page => page.PageNumber) ?? [];
        ClampOffset();
    }

    private void ClampOffset()
    {
        var extent = _layout?.Extent ?? _viewport;
        _offset = new PdfPoint(Math.Clamp(_offset.X, 0, Math.Max(0, extent.Width - _viewport.Width)),
            Math.Clamp(_offset.Y, 0, Math.Max(0, extent.Height - _viewport.Height)));
        if (_layout is not null && _mode != PdfLayoutMode.SinglePage)
        {
            var visible = _layout.GetVisible(new PdfRect(_offset.X, _offset.Y, _viewport.Width, _viewport.Height)).FirstOrDefault();
            if (visible is not null) _currentPage = visible.PageNumber;
        }
    }

    public void SetViewport(double width, double height, double pixelsPerDip = 1)
    {
        if (!double.IsFinite(width) || !double.IsFinite(height) || width < 1 || height < 1) return;
        if (width > 16_384 || height > 16_384 || !double.IsFinite(pixelsPerDip) || pixelsPerDip is < 0.5 or > 8)
            throw new ArgumentOutOfRangeException(nameof(width));
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (Math.Abs(_viewport.Width - width) < 0.01 && Math.Abs(_viewport.Height - height) < 0.01 && _pixelsPerDip == pixelsPerDip) return;
            _viewport = new PdfSize(width, height);
            _pixelsPerDip = pixelsPerDip;
            RebuildLayout();
        }
        ScheduleRender();
    }

    public void SetZoom(double value, PdfPoint? anchor = null)
    {
        if (!double.IsFinite(value)) throw new ArgumentOutOfRangeException(nameof(value));
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            value = Math.Clamp(value, 0.05, 16);
            if (Math.Abs(value - _zoom) < 0.00001) return;
            var point = anchor ?? new PdfPoint(_viewport.Width / 2, _viewport.Height / 2);
            var hit = HitTestLocked(point);
            var ratio = value / _zoom;
            _zoom = value;
            _drag = null; _objectDrag = null; _objectPreview = _selectionBounds;
            RebuildLayout();
            if (hit is { } target && _placements.TryGetValue(target.PageNumber, out var placement))
                _offset = new PdfPoint(placement.Bounds.X + target.Point.X * _layout!.Scale - point.X,
                    placement.Bounds.Y + target.Point.Y * _layout.Scale - point.Y);
            else _offset = new PdfPoint((_offset.X + point.X) * ratio - point.X, (_offset.Y + point.Y) * ratio - point.Y);
            ClampOffset();
        }
        ScheduleRender();
    }

    public void SetOffset(double x, double y)
    {
        if (!double.IsFinite(x) || !double.IsFinite(y)) throw new ArgumentOutOfRangeException(nameof(x));
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            var previous = _offset;
            _offset = new PdfPoint(x, y);
            ClampOffset();
            if (previous == _offset) return;
        }
        ScheduleRender();
    }

    public void ScrollBy(double x, double y) { var offset = Offset; SetOffset(offset.X + x, offset.Y + y); }
    public void SetLayoutMode(PdfLayoutMode mode)
    {
        if (!Enum.IsDefined(mode)) throw new ArgumentOutOfRangeException(nameof(mode));
        lock (_gate) { _mode = mode; RebuildLayout(); }
        ScheduleRender();
    }
    public void FitWidth()
    {
        var document = Document;
        if (document is null) return;
        var width = document.GetPage(CurrentPage).Size.Width;
        if (LayoutMode == PdfLayoutMode.Facing && CurrentPage > 1) width *= 2;
        SetZoom((Viewport.Width - 48) / Math.Max(1, width * 96 / 72));
    }
    public void FitPage()
    {
        var document = Document;
        if (document is null) return;
        var size = document.GetPage(CurrentPage).Size;
        SetZoom(Math.Min((Viewport.Width - 48) / (size.Width * 96 / 72), (Viewport.Height - 48) / (size.Height * 96 / 72)));
        GoToPage(CurrentPage);
    }
    public void GoToPage(int number)
    {
        lock (_gate)
        {
            if (_snapshot is null) return;
            _snapshot.GetPage(number);
            _currentPage = number;
            if (_mode == PdfLayoutMode.SinglePage) RebuildLayout();
            if (_placements.TryGetValue(number, out var placement))
                _offset = new PdfPoint(Math.Max(0, placement.Bounds.X - 20), Math.Max(0, placement.Bounds.Y - 20));
            ClampOffset();
            _currentPage = number;
        }
        ScheduleRender();
    }

    public (int PageNumber, PdfPoint Point)? HitTest(PdfPoint viewportPoint) { lock (_gate) return HitTestLocked(viewportPoint); }
    private (int PageNumber, PdfPoint Point)? HitTestLocked(PdfPoint point) => _layout?.HitTest(new PdfPoint(point.X + _offset.X, point.Y + _offset.Y));

    private sealed record RenderState(long Generation, PdfSnapshot Document, SkiaTileRequest[] Requests);
    private void ScheduleRender()
    {
        CancellationTokenSource? previous = null;
        lock (_gate)
        {
            if (_disposed) return;
            SkiaTileRequest[] requests = [];
            var planFailed = false;
            try
            {
                if (_snapshot is not null && _layout is not null)
                    requests = PlanTiles(_snapshot, _layout,
                        new PdfRect(_offset.X, _offset.Y, _viewport.Width, _viewport.Height),
                        Math.Min(64, _layout.Scale * _pixelsPerDip));
            }
            catch (Exception error)
            {
                previous = _renderCancellation;
                _renderCancellation = null;
                _plannedRevision = null;
                _plannedRequests = [];
                _lastRender = Task.CompletedTask;
                _generation++;
                _isRendering = false;
                _renderError = error.Message;
                planFailed = true;
            }

            // Scrolling within the same device-space tiles only changes composition.
            // Reuse an in-flight plan as well as a completed one; do not cancel its work.
            if (!planFailed && (_plannedRevision != _snapshot?.Id || _renderError is not null ||
                !_plannedRequests.AsSpan().SequenceEqual(requests)))
            {
                previous = _renderCancellation;
                _renderCancellation = null;
                _plannedRevision = _snapshot?.Id;
                _plannedRequests = requests;
                var generation = ++_generation;
                _isRendering = _snapshot is not null;
                if (_snapshot is null)
                {
                    _lastRender = Task.CompletedTask;
                }
                else
                {
                    var cancellation = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
                    _renderCancellation = cancellation;
                    var state = new RenderState(generation, _snapshot, requests);
                    var task = Task.Run(() => RenderAsync(state, cancellation));
                    _lastRender = task;
                    _pending.Add(task);
                    _ = task.ContinueWith(completed => { lock (_gate) _pending.Remove(completed); }, CancellationToken.None,
                        TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
                }
            }
        }
        Cancel(previous);
        Notify();
    }

    private static SkiaTileRequest[] PlanTiles(PdfSnapshot document, PdfPageLayout layout, PdfRect viewport, double scale)
    {
        var requests = new List<SkiaTileRequest>();
        const int edge = 512;
        foreach (var placement in layout.GetVisible(viewport))
        {
            var visible = placement.Bounds.Intersect(viewport);
            var page = document.GetPage(placement.PageNumber);
            var left = (visible.X - placement.Bounds.X) / layout.Scale * scale;
            var top = (visible.Y - placement.Bounds.Y) / layout.Scale * scale;
            var right = (visible.Right - placement.Bounds.X) / layout.Scale * scale;
            var bottom = (visible.Bottom - placement.Bounds.Y) / layout.Scale * scale;
            for (var y = (int)Math.Floor(top / edge) * edge; y < bottom; y += edge)
                for (var x = (int)Math.Floor(left / edge) * edge; x < right; x += edge)
                {
                    var clip = new PdfRect(Math.Max(0, x / scale), Math.Max(0, y / scale),
                        Math.Min(edge / scale, page.Size.Width - x / scale), Math.Min(edge / scale, page.Size.Height - y / scale));
                    if (!clip.IsEmpty) requests.Add(new SkiaTileRequest(placement.PageNumber, clip, scale));
                    if (requests.Count > 256) throw new InvalidOperationException("The viewport exceeds the 256-tile rendering budget. Reduce display scaling or window size.");
                }
        }
        return requests.ToArray();
    }

    private async Task RenderAsync(RenderState state, CancellationTokenSource cancellation)
    {
        var rendered = new List<SkiaTileLease>();
        var token = cancellation.Token;
        try
        {
            var requests = state.Requests;
            for (var i = 0; i < requests.Length; i++)
            {
                token.ThrowIfCancellationRequested();
                rendered.Add(await _renderer.RenderTileAsync(state.Document, requests[i], token).ConfigureAwait(false));
                if (i == 0 || i % 4 == 3 || i == requests.Length - 1) PublishTiles(state, rendered);
            }
            if (requests.Length == 0) PublishTiles(state, rendered);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        catch (Exception error)
        {
            lock (_gate) if (state.Generation == _generation && !_disposed) _renderError = error.Message;
        }
        finally
        {
            foreach (var tile in rendered) tile.Dispose();
            lock (_gate) if (state.Generation == _generation) _isRendering = false;
            cancellation.Dispose();
            Notify();
        }
    }

    private void PublishTiles(RenderState state, List<SkiaTileLease> rendered)
    {
        lock (_gate)
        {
            if (_disposed || state.Generation != _generation || _snapshot?.Id != state.Document.Id) return;
            var leases = rendered.Select(tile => tile.Retain()).ToList();
            // Progressive publication must not erase already visible tiles that the
            // current plan still needs. Keep only exact-revision, exact-scale matches.
            if (_tileRevision == state.Document.Id)
            {
                var missing = state.Requests.ToHashSet();
                foreach (var tile in rendered) missing.Remove(tile.Request);
                foreach (var tile in _tiles)
                    if (missing.Remove(tile.Request)) leases.Add(tile.Retain());
            }
            foreach (var tile in _tiles) tile.Dispose();
            _tiles = leases;
            _tileRevision = state.Document.Id;
            _renderError = null;
        }
        Notify();
    }

    public PdfScene CaptureScene()
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            var visible = _layout?.GetVisible(new PdfRect(_offset.X, _offset.Y, _viewport.Width, _viewport.Height)).ToArray() ?? [];
            var map = visible.ToDictionary(page => page.PageNumber);
            var pages = visible.Select(page => new PdfPageVisual(page.PageNumber,
                new PdfRect(page.Bounds.X - _offset.X, page.Bounds.Y - _offset.Y, page.Bounds.Width, page.Bounds.Height))).ToArray();
            var tiles = new List<PdfTileVisual>();
            if (_tileRevision == _snapshot?.Id && _layout is not null)
                foreach (var tile in _tiles)
                    if (map.TryGetValue(tile.Request.PageNumber, out var page))
                        tiles.Add(new PdfTileVisual(tile.Retain(), ScreenBounds(page, tile.Request.Clip)));
            var overlays = new List<PdfOverlay>();
            void Overlay(int number, PdfRect bounds, PdfOverlayKind kind)
            {
                if (map.TryGetValue(number, out var page)) overlays.Add(new PdfOverlay(ScreenBounds(page, bounds), kind));
            }
            foreach (var hit in _searchHits)
                if (map.ContainsKey(hit.PageNumber)) foreach (var bounds in hit.Bounds) Overlay(hit.PageNumber, bounds, PdfOverlayKind.Search);
            if (_selectedObject is { } selectedObject && _objectPreview is { } preview)
            {
                if (_selectedObjects.Count > 1)
                {
                    var transform = _objectDrag is { } drag ? BoundsTransform(drag.Bounds, preview) : PdfAffineTransform.Identity;
                    foreach (var member in _selectedObjects) Overlay(member.Reference.PageNumber, transform.Map(member.Bounds), PdfOverlayKind.ContentMember);
                }
                Overlay(selectedObject.Reference.PageNumber, preview, PdfOverlayKind.Content);
            }
            if (_selection is { } selection) Overlay(selection.PageNumber, selection.Bounds, PdfOverlayKind.Selection);
            foreach (var redaction in _redactions) Overlay(redaction.PageNumber, redaction.Bounds, PdfOverlayKind.Redaction);
            return new PdfScene(_viewport, pages, tiles.ToArray(), overlays.ToArray());
        }
    }

    private PdfRect ScreenBounds(PdfPagePlacement page, PdfRect bounds) => new(page.Bounds.X - _offset.X + bounds.X * _layout!.Scale,
        page.Bounds.Y - _offset.Y + bounds.Y * _layout.Scale, bounds.Width * _layout.Scale, bounds.Height * _layout.Scale);

    public Task<PdfScene> CreateThumbnailAsync(int pageNumber, double width = 160, double height = 200, CancellationToken cancellationToken = default)
        => CreateThumbnailAsync(pageNumber, width, height, 1, cancellationToken);

    /// <summary>Rasterizes at the display density while preserving thumbnail dimensions in DIPs.
    /// Large/high-density previews are tiled rather than allocated as unbounded page images.</summary>
    public async Task<PdfScene> CreateThumbnailAsync(int pageNumber, double width, double height,
        double pixelsPerDip, CancellationToken cancellationToken = default)
    {
        if (!double.IsFinite(width) || !double.IsFinite(height) || width is < 16 or > 512 || height is < 16 or > 512 ||
            !double.IsFinite(pixelsPerDip) || pixelsPerDip is < 0.5 or > 8)
            throw new ArgumentOutOfRangeException(nameof(width));
        var document = Document ?? throw new InvalidOperationException("No PDF is open.");
        var page = document.GetPage(pageNumber);
        var scale = Math.Min((width - 12) / page.Size.Width, (height - 12) / page.Size.Height);
        var rasterScale = scale * pixelsPerDip;
        var bounds = new PdfRect((width - page.Size.Width * scale) / 2, (height - page.Size.Height * scale) / 2,
            page.Size.Width * scale, page.Size.Height * scale);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _lifetime.Token);
        var tiles = new List<PdfTileVisual>();
        try
        {
            for (var y = 0; y < page.Size.Height * rasterScale; y += 512)
                for (var x = 0; x < page.Size.Width * rasterScale; x += 512)
                {
                    var clip = new PdfRect(x / rasterScale, y / rasterScale,
                        Math.Min(512 / rasterScale, page.Size.Width - x / rasterScale),
                        Math.Min(512 / rasterScale, page.Size.Height - y / rasterScale));
                    using var tile = await _renderer.RenderTileAsync(document, new(pageNumber, clip, rasterScale), linked.Token).ConfigureAwait(false);
                    tiles.Add(new PdfTileVisual(tile.Retain(), new(bounds.X + clip.X * scale, bounds.Y + clip.Y * scale,
                        clip.Width * scale, clip.Height * scale)));
                }
            return new PdfScene(new PdfSize(width, height), [new PdfPageVisual(pageNumber, bounds)], tiles.ToArray(), []);
        }
        catch { foreach (var tile in tiles) tile.Tile.Dispose(); throw; }
    }

    public async Task WaitForRenderingAsync(CancellationToken cancellationToken = default)
    {
        while (true)
        {
            Task task;
            lock (_gate) task = _lastRender;
            await task.WaitAsync(cancellationToken).ConfigureAwait(false);
            lock (_gate) if (ReferenceEquals(task, _lastRender)) return;
        }
    }

    private void Notify()
    {
        // Progressive tiles and input can arrive faster than a dispatcher frame.
        // Consumers read current state, so one queued notification is sufficient.
        if (Interlocked.Exchange(ref _notificationQueued, 1) != 0) return;
        try
        {
            _dispatch(() =>
            {
                Interlocked.Exchange(ref _notificationQueued, 0);
                lock (_gate) if (_disposed) return;
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(null));
                Invalidated?.Invoke(this, EventArgs.Empty);
            });
        }
        catch { Interlocked.Exchange(ref _notificationQueued, 0); throw; }
    }
    public void ReportError(Exception error) { lock (_gate) _lastError = error.Message; Notify(); }
    /// <summary>Clears an editing/host diagnostic without discarding active rendering failures.</summary>
    public void ClearError() { lock (_gate) _lastError = null; Notify(); }
    private static void Cancel(CancellationTokenSource? source) { try { source?.Cancel(); } catch (ObjectDisposedException) { } }

    public async ValueTask DisposeAsync()
    {
        Task[] pending;
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
            pending = _pending.ToArray();
        }
        Session.Changed -= SessionChanged;
        _lifetime.Cancel();
        await Task.WhenAll(pending).ConfigureAwait(false);
        lock (_gate)
        {
            foreach (var tile in _tiles) tile.Dispose();
            _tiles.Clear();
        }
        _lifetime.Dispose();
    }
}
