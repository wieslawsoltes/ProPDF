using ProPDF.Core;

namespace ProPDF.Presentation;

public sealed partial class PdfViewportController
{
    private sealed class DragState(Guid revision, int page, PdfPoint start, PdfPoint viewportStart, PdfPoint offset, PdfTool tool)
    {
        public Guid Revision { get; } = revision;
        public int Page { get; } = page;
        public PdfPoint Start { get; } = start;
        public PdfPoint ViewportStart { get; } = viewportStart;
        public PdfPoint Offset { get; } = offset;
        public PdfTool Tool { get; } = tool;
        public List<PdfPoint> Points { get; } = [start];
    }
    private DragState? _drag;
    private long _searchGeneration;
    private long _selectionGeneration;

    public bool BeginInteraction(PdfPoint point, bool forcePan = false)
    {
        if (!double.IsFinite(point.X) || !double.IsFinite(point.Y)) return false;
        lock (_gate)
        {
            if (_disposed || _snapshot is null) return false;
            var tool = forcePan ? PdfTool.Pan : _tool;
            var hit = HitTestLocked(point);
            if (hit is null && tool != PdfTool.Pan) return false;
            _drag = new DragState(_snapshot.Id, hit?.PageNumber ?? _currentPage, hit?.Point ?? new PdfPoint(), point, _offset, tool);
            if (tool != PdfTool.Pan)
            {
                _selection = null;
                _selectedText = "";
                _selectionGeneration++;
            }
        }
        Notify();
        return true;
    }

    public void MoveInteraction(PdfPoint point)
    {
        if (!double.IsFinite(point.X) || !double.IsFinite(point.Y)) return;
        PdfPoint? pan = null;
        lock (_gate)
        {
            if (_disposed || _drag is not { } drag || _snapshot?.Id != drag.Revision) return;
            if (drag.Tool == PdfTool.Pan)
                pan = new PdfPoint(drag.Offset.X - (point.X - drag.ViewportStart.X), drag.Offset.Y - (point.Y - drag.ViewportStart.Y));
            else if (_placements.TryGetValue(drag.Page, out var page))
            {
                var size = _snapshot.GetPage(drag.Page).Size;
                var local = new PdfPoint(Math.Clamp((point.X + _offset.X - page.Bounds.X) / _layout!.Scale, 0, size.Width),
                    Math.Clamp((point.Y + _offset.Y - page.Bounds.Y) / _layout.Scale, 0, size.Height));
                _selection = new PdfSelection(drag.Revision, drag.Page, PdfRect.FromPoints(drag.Start, local));
                if (drag.Points.Count < 100_000 && Math.Abs(drag.Points[^1].X - local.X) + Math.Abs(drag.Points[^1].Y - local.Y) >= 0.5)
                    drag.Points.Add(local);
            }
        }
        if (pan is { } offset) SetOffset(offset.X, offset.Y);
        else Notify();
    }

    public async Task EndInteractionAsync(PdfPoint point, CancellationToken cancellationToken = default)
    {
        MoveInteraction(point);
        DragState? drag;
        PdfSelection? selection;
        PdfSnapshot? document;
        string text;
        long generation;
        lock (_gate)
        {
            if (_disposed) return;
            drag = _drag;
            selection = _selection;
            document = _snapshot;
            text = _toolText;
            generation = _selectionGeneration;
            _drag = null;
        }
        if (drag is null || drag.Tool == PdfTool.Pan || selection is null || document?.Id != selection.Revision) return;
        var bounds = selection.Bounds;
        if (drag.Tool == PdfTool.SelectText)
        {
            var page = await _textService.GetPageTextAsync(document, selection.PageNumber, cancellationToken).ConfigureAwait(false);
            var selected = page.Words.Where(word => word.Bounds.Intersects(bounds)).ToArray();
            lock (_gate)
                if (!_disposed && _snapshot?.Id == document.Id && generation == _selectionGeneration)
                    _selectedText = string.Join(" ", selected.Select(word => word.Text));
            Notify();
            return;
        }
        // A horizontal/vertical ink stroke is valid even when its selection rectangle has zero area.
        if (drag.Tool == PdfTool.Ink)
        {
            if (drag.Points.Count >= 2)
                await Session.ApplyAsync(new AddInkAnnotation(selection.PageNumber, drag.Points), selection.Revision, cancellationToken).ConfigureAwait(false);
            return;
        }
        if (drag.Tool == PdfTool.SelectRegion || bounds.Width < 0.5 || bounds.Height < 0.5) return;
        if (drag.Tool == PdfTool.Redact)
        {
            lock (_gate)
            {
                if (_snapshot?.Id != selection.Revision) throw new PdfRevisionConflictException();
                if (_redactions.Count >= 10_000) throw new InvalidOperationException("The redaction mark limit has been reached.");
                _redactions.Add(selection);
                _selection = null;
            }
            Notify();
            return;
        }
        IPdfEditOperation operation = drag.Tool switch
        {
            PdfTool.Highlight => new AddAnnotation(selection.PageNumber, bounds, PdfAnnotationKind.Highlight),
            PdfTool.Note => new AddAnnotation(selection.PageNumber, bounds, PdfAnnotationKind.Note, text),
            PdfTool.FreeText => new AddAnnotation(selection.PageNumber, bounds, PdfAnnotationKind.FreeText, text),
            PdfTool.Text => new AddText(selection.PageNumber, new PdfPoint(bounds.X, bounds.Y + Math.Min(14, bounds.Height)), text),
            PdfTool.Rectangle => new AddShape(selection.PageNumber, bounds),
            PdfTool.Ellipse => new AddShape(selection.PageNumber, bounds, PdfShapeKind.Ellipse),
            PdfTool.ReplaceText => new ReplaceRegionText(selection.PageNumber, bounds, text),
            PdfTool.TextField => new AddFormField(selection.PageNumber, bounds, text, PdfFormFieldKind.Text),
            PdfTool.CheckBox => new AddFormField(selection.PageNumber, bounds, text, PdfFormFieldKind.CheckBox),
            _ => throw new NotSupportedException("The selected tool has no editing operation.")
        };
        await Session.ApplyAsync(operation, selection.Revision, cancellationToken).ConfigureAwait(false);
    }

    public void CancelInteraction() { lock (_gate) _drag = null; Notify(); }
    public void ClearSelection() { lock (_gate) { _selection = null; _selectedText = ""; _selectionGeneration++; } Notify(); }
    public void ClearRedactions() { lock (_gate) _redactions.Clear(); Notify(); }

    /// <summary>Call only after an explicit application confirmation. Marks themselves are not saved into the PDF.</summary>
    public async Task ApplyRedactionsAsync(CancellationToken cancellationToken = default)
    {
        PdfSelection[] marks;
        lock (_gate) marks = _redactions.ToArray();
        if (marks.Length == 0) return;
        if (marks.Any(mark => mark.Revision != marks[0].Revision)) throw new PdfRevisionConflictException();
        await Session.ApplyAsync(marks.Select(mark => new RedactRegion(mark.PageNumber, mark.Bounds)), marks[0].Revision, cancellationToken).ConfigureAwait(false);
    }

    public async Task SearchAsync(string query, PdfSearchOptions? options = null, CancellationToken cancellationToken = default)
    {
        PdfSnapshot document;
        long generation;
        CancellationToken lifetime;
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            document = _snapshot ?? throw new InvalidOperationException("No PDF is open.");
            generation = ++_searchGeneration;
            lifetime = _lifetime.Token;
        }
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, lifetime);
        var hits = string.IsNullOrWhiteSpace(query) ? Array.Empty<PdfSearchHit>() :
            await _textService.SearchAsync(document, query, options, linked.Token).ConfigureAwait(false);
        linked.Token.ThrowIfCancellationRequested();
        lock (_gate)
        {
            if (_disposed || _snapshot?.Id != document.Id || generation != _searchGeneration) return;
            _searchHits = hits;
            _searchIndex = hits.Count == 0 ? -1 : 0;
            if (hits.Count > 0) RevealHitLocked(hits[0]);
        }
        ScheduleRender();
    }

    public void NextSearchResult(bool backwards = false)
    {
        lock (_gate)
        {
            if (_disposed || _searchHits.Count == 0) return;
            _searchIndex = (_searchIndex + (backwards ? -1 : 1) + _searchHits.Count) % _searchHits.Count;
            RevealHitLocked(_searchHits[_searchIndex]);
        }
        ScheduleRender();
    }

    // Called under _gate: changing document, search generation or layout cannot interleave navigation.
    private void RevealHitLocked(PdfSearchHit hit)
    {
        _currentPage = hit.PageNumber;
        if (_mode == PdfLayoutMode.SinglePage) RebuildLayout();
        if (_placements.TryGetValue(hit.PageNumber, out var page))
        {
            var target = hit.Bounds.Count > 0 ? hit.Bounds[0] : new PdfRect(0, 0, 1, 1);
            _offset = new PdfPoint(Math.Max(0, page.Bounds.X + target.X * _layout!.Scale - _viewport.Width / 4),
                Math.Max(0, page.Bounds.Y + target.Y * _layout.Scale - _viewport.Height / 3));
            ClampOffset();
        }
        // Clamping normally chooses the first visible page. Explicit navigation must retain its target
        // even when a strip of the preceding page remains visible above the highlighted result.
        _currentPage = hit.PageNumber;
    }
}
