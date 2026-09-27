using System.ComponentModel;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Markup.Xaml;
using Avalonia.Threading;
using ProPDF.Core;
using ProPDF.Presentation;

namespace ProPDF.Avalonia;

/// <summary>Reusable inspector for revision-aware native page-content editing.</summary>
public sealed partial class PdfContentPanel : UserControl
{
    private readonly ListBox _objects;
    private PdfWorkspace? _workspace;
    private bool _attached, _syncing, _queued;
    public PdfContentPanel()
    {
        AvaloniaXamlLoader.Load(this);
        _objects = this.FindControl<ListBox>("ContentObjectsList")!;
        _objects.SelectionChanged += SelectionChanged;
        DataContextChanged += (_, _) => BindWorkspace();
    }
    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    { base.OnAttachedToVisualTree(e); _attached = true; BindWorkspace(); }
    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    { _attached = false; BindWorkspace(); base.OnDetachedFromVisualTree(e); }
    private void BindWorkspace()
    {
        if (_workspace is not null) _workspace.PropertyChanged -= WorkspaceChanged;
        _workspace = _attached ? DataContext as PdfWorkspace : null;
        if (_workspace is not null) _workspace.PropertyChanged += WorkspaceChanged;
        QueueSync();
    }
    private void WorkspaceChanged(object? sender, PropertyChangedEventArgs e) => QueueSync();
    private void QueueSync()
    {
        if (_queued) return;
        _queued = true;
        Dispatcher.UIThread.Post(() =>
        {
            _queued = false;
            if (_workspace is not { } workspace || !_attached) return;
            _syncing = true;
            try
            {
                var selected = _objects.SelectedItems!;
                var desired = workspace.SelectedContentObjects;
                foreach (var item in selected.OfType<PdfContentObject>().ToArray())
                    if (!desired.Contains(item)) selected.Remove(item);
                foreach (var item in desired)
                    if (!selected.Contains(item)) selected.Add(item);
            }
            finally { _syncing = false; }
        }, DispatcherPriority.Background);
    }
    private void SelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (_syncing || _workspace is not { } workspace || !ReferenceEquals(_objects.ItemsSource, workspace.ContentObjects)) return;
        try { workspace.SelectContentObjects(_objects.SelectedItems!.OfType<PdfContentObject>()); }
        catch (Exception error) { workspace.Viewport.ReportError(error); QueueSync(); }
    }
}
