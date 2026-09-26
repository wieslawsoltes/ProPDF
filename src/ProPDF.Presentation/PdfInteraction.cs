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

    public bool BeginInteraction(PdfPoint point, bool forcePan = false)
    {
        lock (_gate)
        {
            if (_snapshot is null) return false;
            var tool = forcePan ? PdfTool.Pan : _tool;
            var hit = HitTestLocked(point);
            if (hit is null && tool != PdfTool.Pan) return false;
            _drag = new DragState(_snapshot.Id, hit?.PageNumber ?? _currentPage, hit?.Point ?? new PdfPoint(), point, _offset, tool);
            if (tool != PdfTool.Pan) _selection = null;
        }
        Notify();
        return true;
    }

    public void MoveInteraction(PdfPoint point)
    {
        PdfPoint? pan = null;
        lock (_gate)
        {
            if (_drag is not { } drag || _snapshot?.Id != drag.Revision) return;
            if (drag.Tool == PdfTool.Pan)
                pan = new PdfPoint(drag.Offset.X - (point.X - drag.ViewportStart.X), drag.Offset.Y - (point.Y - drag.ViewportStart.Y));
            else if (_placements.TryGetValue(drag.Page, out var page))
            {
                var size = _snapshot.GetPage(drag.Page).Size;
                var local = new PdfPoint(Math.Clamp((point.X + _offset.X - page.Bounds.X) / _layout!.Scale, 0, size.Width),
                    Math.Clamp((point.Y + _offset.Y - page.Bounds.Y) / _layout.Scale, 0, size.Height));
                _selection = new PdfSelection(drag.Revision, drag.Page, PdfRect.FromPoints(drag.Start, local));
                if (drag.Points.Count < 100_000 && (Math.Abs(drag.Points[^1].X - local.X) + Math.Abs(drag.Points[^1].Y - local.Y) >= 0.5)) drag.Points.Add(local);
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
        string text;
        lock (_gate)
        {
            drag = _drag;
            selection = _selection;
            text = _toolText;
            _drag = null;
        }
        if (drag is null || drag.Tool == PdfTool.Pan || selection is null || selection.Revision != Document?.Id) return;
        var bounds = selection.Bounds;
        if (drag.Tool == PdfTool.SelectText)
        {
            var document = Document!;
            var page = await _textService.GetPageTextAsync(document, selection.PageNumber, cancellationToken).ConfigureAwait(false);
            var selected = page.Words.Where(word => word.Bounds.Intersects(bounds)).ToArray();
            lock (_gate) if (_snapshot?.Id == document.Id) _selectedText = string.Join(" ", selected.Select(word => word.Text));
            Notify();
            return;
        }
        if (drag.Tool == PdfTool.SelectRegion || bounds.Width < 0.5 || bounds.Height < 0.5) return;
        if (drag.Tool == PdfTool.Redact)
        {
            lock (_gate)
            {
                if (_snapshot?.Id != selection.Revision) throw new PdfRevisionConflictException();
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
            PdfTool.Ink => new AddInkAnnotation(selection.PageNumber, drag.Points),
            PdfTool.ReplaceText => new ReplaceRegionText(selection.PageNumber, bounds, text),
            PdfTool.TextField => new AddFormField(selection.PageNumber, bounds, text, PdfFormFieldKind.Text),
            PdfTool.CheckBox => new AddFormField(selection.PageNumber, bounds, text, PdfFormFieldKind.CheckBox),
            _ => throw new NotSupportedException("The selected tool has no editing operation.")
        };
        await Session.ApplyAsync(operation, selection.Revision, cancellationToken).ConfigureAwait(false);
    }

    public void CancelInteraction() { lock (_gate) _drag = null; Notify(); }
    public void ClearSelection() { lock (_gate) { _selection = null; _selectedText = ""; } Notify(); }
    public void ClearRedactions() { lock (_gate) _redactions.Clear(); Notify(); }

    /// <summary>Call only after an explicit application confirmation. The marks themselves are not saved into the PDF.</summary>
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
        var document = Document ?? throw new InvalidOperationException("No PDF is open.");
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _lifetime.Token);
        var hits = string.IsNullOrWhiteSpace(query) ? Array.Empty<PdfSearchHit>() :
            await _textService.SearchAsync(document, query, options, linked.Token).ConfigureAwait(false);
        lock (_gate)
        {
            if (_disposed || _snapshot?.Id != document.Id) return;
            _searchHits = hits;
            _searchIndex = hits.Count == 0 ? -1 : 0;
        }
        if (hits.Count > 0) RevealHit(hits[0]);
        Notify();
    }

    public void NextSearchResult(bool backwards = false)
    {
        PdfSearchHit hit;
        lock (_gate)
        {
            if (_searchHits.Count == 0) return;
            _searchIndex = (_searchIndex + (backwards ? -1 : 1) + _searchHits.Count) % _searchHits.Count;
            hit = _searchHits[_searchIndex];
        }
        RevealHit(hit);
        Notify();
    }

    private void RevealHit(PdfSearchHit hit)
    {
        GoToPage(hit.PageNumber);
        lock (_gate)
        {
            if (hit.Bounds.Count > 0 && _placements.TryGetValue(hit.PageNumber, out var page))
            {
                _offset = new PdfPoint(_offset.X, Math.Max(0, page.Bounds.Y + hit.Bounds[0].Y * _layout!.Scale - _viewport.Height / 3));
                ClampOffset();
            }
        }
        ScheduleRender();
    }
}
