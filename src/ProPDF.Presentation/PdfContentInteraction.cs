using ProPDF.Core;

namespace ProPDF.Presentation;

public sealed partial class PdfViewportController
{
    private IPdfContentService? _contentService;
    private PdfPageContent? _contentPage;
    private PdfContentObject? _selectedObject;
    private IReadOnlyList<PdfContentObject> _selectedObjects = Array.Empty<PdfContentObject>();
    private PdfRect? _selectionBounds;
    public IReadOnlyList<PdfContentObject> SelectedContentObjects { get { lock (_gate) return _selectedObjects; } }
    public PdfRect? ContentSelectionBounds { get { lock (_gate) return _selectionBounds; } }
    private PdfRect? _objectPreview;
    private string? _contentError;
    private (Guid Revision, int Page)? _contentRequest;
    private Task _contentTask = Task.CompletedTask;
    private long _contentGeneration;
    private sealed record ObjectDrag(IReadOnlyList<PdfContentObject> Objects, PdfRect Bounds, PdfPoint Start, int Corner)
    {
        public PdfContentObject Object => Objects[^1];
    }
    private ObjectDrag? _objectDrag;
    public IReadOnlyList<PdfContentObject> ContentObjects { get { lock (_gate) return _contentPage?.Objects ?? Array.Empty<PdfContentObject>(); } }
    public PdfContentObject? SelectedContentObject { get { lock (_gate) return _selectedObject; } }
    public string ContentStatus { get { lock (_gate) return _contentError ?? (!_contentTask.IsCompleted ? "Reading page objects…" : _selectedObject?.ReadOnlyReason ?? $"{_contentPage?.Objects.Count ?? 0} objects · Bounds are approximate"); } }
    public double TextBoxFontSize { get; set; } = 14;
    public string TextBoxStandardFont { get; set; } = "Helvetica";
    public double TextBoxLineSpacing { get; set; } = 1.2;
    public PdfTextAlignment TextBoxAlignment { get; set; }

    public void ConfigureContentService(IPdfContentService? service)
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (ReferenceEquals(_contentService, service)) return;
            _contentService = service; ResetContentLocked();
        }
        Notify();
    }
    private void ResetContentLocked()
    {
        _contentGeneration++; _contentPage = null; _contentRequest = null; SetSelectedObjectsLocked([]); _objectDrag = null; _contentError = null;
    }
    /// <summary>Loads one page's content lazily. Stale or cancelled reads never replace the current inspection.</summary>
    public Task LoadContentAsync(int? pageNumber = null, bool force = false, CancellationToken cancellationToken = default)
    {
        Task task;
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_snapshot is not { } document || _contentService is not { } service) return Task.CompletedTask;
            var number = pageNumber ?? _currentPage; document.GetPage(number); var request = (document.Id, number);
            if (!force && _contentRequest == request) return _contentTask.WaitAsync(cancellationToken);
            if (_contentRequest != request) { _contentPage = null; SetSelectedObjectsLocked([]); _objectDrag = null; }
            _contentRequest = request; var generation = ++_contentGeneration; var lifetime = _lifetime.Token; _contentError = null;
            task = Task.Run(async () =>
            {
                using var linked = CancellationTokenSource.CreateLinkedTokenSource(lifetime, cancellationToken);
                try
                {
                    var content = await service.ReadPageContentAsync(document, number, cancellationToken: linked.Token).ConfigureAwait(false);
                    linked.Token.ThrowIfCancellationRequested();
                    lock (_gate)
                    {
                        if (_disposed || generation != _contentGeneration || _snapshot?.Id != document.Id) return;
                        _contentPage = content;
                        if (_selectedObject?.Reference.PageNumber != number) { SetSelectedObjectsLocked([]); }
                    }
                }
                catch (OperationCanceledException) when (linked.IsCancellationRequested)
                {
                    lock (_gate) if (generation == _contentGeneration) _contentRequest = null;
                }
                catch (Exception error)
                {
                    lock (_gate) if (!_disposed && generation == _contentGeneration) { _contentError = error.Message; _contentPage = null; SetSelectedObjectsLocked([]); }
                }
                finally { Notify(); }
            });
            _contentTask = task; _pending.Add(task);
            _ = task.ContinueWith(done => { lock (_gate) _pending.Remove(done); Notify(); }, CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
        }
        Notify(); return task;
    }
    public void SelectContentObject(PdfContentObject? value) => SelectContentObjects(value is null ? [] : [value]);

    /// <summary>Selects up to 1000 inspected objects on one page. An invalid selection leaves the existing selection intact.</summary>
    public void SelectContentObjects(IEnumerable<PdfContentObject> values)
    {
        ArgumentNullException.ThrowIfNull(values);
        var items = values.Take(EditContentObjects.MaximumSelection + 1).ToArray();
        if (items.Length > EditContentObjects.MaximumSelection) throw new ArgumentException("The selection exceeds 1000 objects.", nameof(values));
        lock (_gate)
        {
            if (_disposed) return;
            if (items.Length > 0)
            {
                _ = PdfContentSelection.Bounds(items);
                var available = _contentPage?.Objects.ToDictionary(o => o.Reference);
                if (available is null || items.Any(o => o.Reference.Revision != _snapshot?.Id ||
                    !available.TryGetValue(o.Reference, out var inspected) || inspected != o)) throw new PdfRevisionConflictException();
            }
            if (_selectedObjects.SequenceEqual(items)) return;
            SetSelectedObjectsLocked(items);
        }
        Notify();
    }
    private void SetSelectedObjectsLocked(PdfContentObject[] items)
    {
        _selectionBounds = items.Length == 0 ? null : PdfContentSelection.Bounds(items);
        _selectedObjects = Array.AsReadOnly(items);
        _selectedObject = items.LastOrDefault();
        _objectPreview = _selectionBounds;
        _objectDrag = null;
    }
    private bool BeginObjectInteraction(PdfPoint point, bool toggleSelection = false)
    {
        int? load = null; bool handled = false;
        lock (_gate)
        {
            if (_disposed || _snapshot is null) return false;
            var hit = HitTestLocked(point); if (hit is null) return false;
            if (_contentPage?.Revision != _snapshot.Id || _contentPage.PageNumber != hit.Value.PageNumber) load = hit.Value.PageNumber;
            else
            {
                var local = hit.Value.Point; var corner = -1;
                if (!toggleSelection && _selectionBounds is { } selectedBounds && _selectedObjects.All(o => o.CanEdit))
                {
                    var corners = ObjectCorners(selectedBounds); var radius = 7 / _layout!.Scale;
                    for (var i = 0; i < corners.Length; i++)
                        if (Math.Abs(corners[i].X - local.X) <= radius && Math.Abs(corners[i].Y - local.Y) <= radius) { corner = i; break; }
                }
                var target = _contentPage.Objects.LastOrDefault(item => item.Bounds.Contains(local));
                if (toggleSelection)
                {
                    if (target is not null)
                    {
                        var items = _selectedObjects.ToList();
                        if (!items.Remove(target))
                        {
                            if (items.Count >= EditContentObjects.MaximumSelection) { _contentError = "The selection exceeds 1000 objects."; return false; }
                            items.Add(target);
                        }
                        SetSelectedObjectsLocked(items.ToArray());
                    }
                    handled = true; // Toggle only; never start a drag on the same press.
                }
                else
                {
                    if (corner < 0 && (target is null || !_selectedObjects.Contains(target)))
                        SetSelectedObjectsLocked(target is null ? [] : [target]);
                    _objectPreview = _selectionBounds;
                    if (_selectionBounds is { Width: > .001, Height: > .001 } bounds && _selectedObjects.All(o => o.CanEdit))
                    { _objectDrag = new(_selectedObjects, bounds, local, corner); handled = true; }
                    else _objectDrag = null;
                }
            }
        }
        if (load.HasValue) _ = LoadContentAsync(load);
        Notify(); return handled;
    }
    private static PdfPoint[] ObjectCorners(PdfRect bounds) => [new(bounds.X, bounds.Y), new(bounds.Right, bounds.Y), new(bounds.Right, bounds.Bottom), new(bounds.X, bounds.Bottom)];
    private bool MoveObjectInteraction(PdfPoint point)
    {
        lock (_gate)
        {
            if (_disposed || _objectDrag is not { } drag || drag.Object.Reference.Revision != _snapshot?.Id) return false;
            if (!_placements.TryGetValue(drag.Object.Reference.PageNumber, out var placement)) return false;
            var local = new PdfPoint((point.X + _offset.X - placement.Bounds.X) / _layout!.Scale, (point.Y + _offset.Y - placement.Bounds.Y) / _layout.Scale);
            var b = drag.Bounds;
            if (drag.Corner < 0) _objectPreview = new PdfRect(b.X + local.X - drag.Start.X, b.Y + local.Y - drag.Start.Y, b.Width, b.Height);
            else
            {
                var left = drag.Corner is 0 or 3 ? Math.Min(local.X, b.Right - 2) : b.X;
                var right = drag.Corner is 1 or 2 ? Math.Max(local.X, b.X + 2) : b.Right;
                var top = drag.Corner is 0 or 1 ? Math.Min(local.Y, b.Bottom - 2) : b.Y;
                var bottom = drag.Corner is 2 or 3 ? Math.Max(local.Y, b.Y + 2) : b.Bottom;
                _objectPreview = new PdfRect(left, top, right - left, bottom - top);
            }
        }
        Notify(); return true;
    }
    private async Task<bool> EndObjectInteractionAsync(PdfPoint point, CancellationToken cancellationToken)
    {
        ObjectDrag? drag; PdfRect? bounds;
        lock (_gate)
        {
            drag = _objectDrag; bounds = _objectPreview; _objectDrag = null;
        }
        if (drag is null) return false;
        if (bounds is not { } target || drag.Object.Reference.Revision != Document?.Id) return true;
        var source = drag.Bounds;
        if (Math.Abs(source.X - target.X) + Math.Abs(source.Y - target.Y) + Math.Abs(source.Width - target.Width) + Math.Abs(source.Height - target.Height) < .01) return true;
        try
        {
            await Session.ApplyAsync(PdfContentSelection.Transform(drag.Objects, BoundsTransform(source, target)), drag.Object.Reference.Revision, cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            lock (_gate) if (_selectedObject?.Reference == drag.Object.Reference) _objectPreview = source;
            Notify(); throw;
        }
        return true;
    }
    public static PdfAffineTransform BoundsTransform(PdfRect source, PdfRect target)
    {
        if (source.IsEmpty || target.IsEmpty) throw new ArgumentException("Object dimensions must be positive.");
        var sx = target.Width / source.Width; var sy = target.Height / source.Height;
        var mapping = new PdfAffineTransform(sx, 0, 0, sy, target.X - sx * source.X, target.Y - sy * source.Y); mapping.Validate(); return mapping;
    }
}
