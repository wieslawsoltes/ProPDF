using ProPDF.Core;

namespace ProPDF.Presentation;

public sealed partial class PdfViewportController
{
    private IPdfContentService? _contentService;
    private PdfPageContent? _contentPage;
    private PdfContentObject? _selectedObject;
    private PdfRect? _objectPreview;
    private string? _contentError;
    private (Guid Revision, int Page)? _contentRequest;
    private Task _contentTask = Task.CompletedTask;
    private long _contentGeneration;
    private sealed record ObjectDrag(PdfContentObject Object, PdfPoint Start, int Corner);
    private ObjectDrag? _objectDrag;
    public IReadOnlyList<PdfContentObject> ContentObjects { get { lock (_gate) return _contentPage?.Objects ?? Array.Empty<PdfContentObject>(); } }
    public PdfContentObject? SelectedContentObject { get { lock (_gate) return _selectedObject; } }
    public string ContentStatus { get { lock (_gate) return _contentError ?? (!_contentTask.IsCompleted ? "Reading page objects…" : _selectedObject?.ReadOnlyReason ?? $"{_contentPage?.Objects.Count ?? 0} objects · Bounds are approximate"); } }
    public double TextBoxFontSize { get; set; } = 14;
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
        _contentGeneration++; _contentPage = null; _contentRequest = null; _selectedObject = null; _objectPreview = null; _objectDrag = null; _contentError = null;
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
            if (_contentRequest != request) { _contentPage = null; _selectedObject = null; _objectPreview = null; _objectDrag = null; }
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
                        if (_selectedObject?.Reference.PageNumber != number) { _selectedObject = null; _objectPreview = null; }
                    }
                }
                catch (OperationCanceledException) when (linked.IsCancellationRequested)
                {
                    lock (_gate) if (generation == _contentGeneration) _contentRequest = null;
                }
                catch (Exception error)
                {
                    lock (_gate) if (!_disposed && generation == _contentGeneration) { _contentError = error.Message; _contentPage = null; _selectedObject = null; _objectPreview = null; }
                }
                finally { Notify(); }
            });
            _contentTask = task; _pending.Add(task);
            _ = task.ContinueWith(done => { lock (_gate) _pending.Remove(done); Notify(); }, CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
        }
        Notify(); return task;
    }
    public void SelectContentObject(PdfContentObject? value)
    {
        lock (_gate)
        {
            if (_disposed) return;
            if (value is not null && (value.Reference.Revision != _snapshot?.Id || _contentPage?.Objects.Contains(value) != true)) throw new PdfRevisionConflictException();
            _selectedObject = value; _objectPreview = value?.Bounds; _objectDrag = null;
        }
        Notify();
    }
    private bool BeginObjectInteraction(PdfPoint point)
    {
        int? load = null; bool selected = false;
        lock (_gate)
        {
            if (_disposed || _snapshot is null) return false;
            var hit = HitTestLocked(point); if (hit is null) return false;
            if (_contentPage?.Revision != _snapshot.Id || _contentPage.PageNumber != hit.Value.PageNumber) load = hit.Value.PageNumber;
            else
            {
                var local = hit.Value.Point; var corner = -1;
                if (_selectedObject is { CanEdit: true } old && old.Reference.PageNumber == hit.Value.PageNumber)
                {
                    var corners = ObjectCorners(old.Bounds); var radius = 7 / _layout!.Scale;
                    for (var i = 0; i < corners.Length; i++) if (Math.Abs(corners[i].X - local.X) <= radius && Math.Abs(corners[i].Y - local.Y) <= radius) { corner = i; break; }
                }
                if (corner < 0) _selectedObject = _contentPage.Objects.LastOrDefault(item => item.Bounds.Contains(local));
                _objectPreview = _selectedObject?.Bounds;
                if (_selectedObject is { CanEdit: true } item && item.Bounds.Width > .001 && item.Bounds.Height > .001)
                { _objectDrag = new(item, local, corner); selected = true; }
                else _objectDrag = null;
            }
        }
        if (load.HasValue) _ = LoadContentAsync(load);
        Notify(); return selected;
    }
    private static PdfPoint[] ObjectCorners(PdfRect bounds) => [new(bounds.X, bounds.Y), new(bounds.Right, bounds.Y), new(bounds.Right, bounds.Bottom), new(bounds.X, bounds.Bottom)];
    private bool MoveObjectInteraction(PdfPoint point)
    {
        lock (_gate)
        {
            if (_disposed || _objectDrag is not { } drag || drag.Object.Reference.Revision != _snapshot?.Id) return false;
            if (!_placements.TryGetValue(drag.Object.Reference.PageNumber, out var placement)) return false;
            var local = new PdfPoint((point.X + _offset.X - placement.Bounds.X) / _layout!.Scale, (point.Y + _offset.Y - placement.Bounds.Y) / _layout.Scale);
            var b = drag.Object.Bounds;
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
        var source = drag.Object.Bounds;
        if (Math.Abs(source.X - target.X) + Math.Abs(source.Y - target.Y) + Math.Abs(source.Width - target.Width) + Math.Abs(source.Height - target.Height) < .01) return true;
        try
        {
            await Session.ApplyAsync(new TransformContentObject(drag.Object.Reference, BoundsTransform(source, target)), drag.Object.Reference.Revision, cancellationToken).ConfigureAwait(false);
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
