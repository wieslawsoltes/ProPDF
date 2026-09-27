namespace ProPDF.Uno;

/// <summary>Lazy thumbnail for virtualized Uno page lists; releases scenes on unload and cancels stale revisions.</summary>
public sealed class PdfThumbnail : UserControl
{
    public static readonly DependencyProperty ControllerProperty = DependencyProperty.Register(nameof(Controller), typeof(PdfViewportController), typeof(PdfThumbnail), new PropertyMetadata(null, Changed));
    public static readonly DependencyProperty PageNumberProperty = DependencyProperty.Register(nameof(PageNumber), typeof(int), typeof(PdfThumbnail), new PropertyMetadata(1, Changed));
    private readonly SceneSurface _surface = new();
    private CancellationTokenSource? _request;
    private int _generation;
    public PdfViewportController? Controller { get => (PdfViewportController?)GetValue(ControllerProperty); set => SetValue(ControllerProperty, value); }
    public int PageNumber { get => (int)GetValue(PageNumberProperty); set => SetValue(PageNumberProperty, value); }
    public PdfThumbnail()
    {
        Width = 144; Height = 184; Content = _surface;
        Loaded += (_, _) => { Subscribe(); Reload(); };
        Unloaded += (_, _) => { Unsubscribe(); Cancel(); _surface.SetScene(null); };
    }
    private static void Changed(DependencyObject sender, DependencyPropertyChangedEventArgs e)
    {
        var view = (PdfThumbnail)sender;
        if (e.OldValue is PdfViewportController old) old.Session.Changed -= view.DocumentChanged;
        if (view.IsLoaded) { view.Subscribe(); view.Reload(); }
    }
    private void Subscribe() { if (Controller is { } c) { c.Session.Changed -= DocumentChanged; c.Session.Changed += DocumentChanged; } }
    private void Unsubscribe() { if (Controller is { } c) c.Session.Changed -= DocumentChanged; }
    private void DocumentChanged(object? sender, PdfSessionChangedEventArgs e)
    { if (e.Kind != PdfChangeKind.Saved) DispatcherQueue.TryEnqueue(Reload); }
    private void Cancel() { _generation++; try { _request?.Cancel(); } catch (ObjectDisposedException) { } _request = null; }
    private async void Reload()
    {
        Cancel(); _surface.SetScene(null);
        if (!IsLoaded || Controller is not { Document: { } document } c || PageNumber < 1 || PageNumber > document.Pages.Count) return;
        var generation = _generation; using var request = new CancellationTokenSource(); _request = request;
        try
        {
            var scene = await c.CreateThumbnailAsync(PageNumber, 144, 184, request.Token);
            if (!IsLoaded || generation != _generation) scene.Dispose(); else _surface.SetScene(scene);
        }
        catch (OperationCanceledException) { }
        catch (ObjectDisposedException) { }
        catch (Exception error) { c.ReportError(error); }
        finally { if (ReferenceEquals(_request, request)) _request = null; }
    }
}
