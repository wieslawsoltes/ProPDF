using System.ComponentModel;
using System.Windows.Controls;
using System.Windows.Threading;
using ProPDF.Core;
using ProPDF.Presentation;

namespace ProPDF.Wpf;

/// <summary>Reusable inspector for revision-aware native page-content editing.</summary>
public sealed partial class PdfContentPanel : UserControl
{
    private PdfWorkspace? _workspace;
    private bool _syncing, _queued;
    public PdfContentPanel()
    {
        InitializeComponent();
        ContentObjectsList.SelectionChanged += SelectionChanged;
        DataContextChanged += (_, _) => BindWorkspace();
        Loaded += (_, _) => BindWorkspace();
        Unloaded += (_, _) => UnbindWorkspace();
    }
    private void UnbindWorkspace()
    {
        if (_workspace is not null) _workspace.PropertyChanged -= WorkspaceChanged;
        _workspace = null;
    }
    private void BindWorkspace()
    {
        UnbindWorkspace();
        _workspace = IsLoaded ? DataContext as PdfWorkspace : null;
        if (_workspace is not null) _workspace.PropertyChanged += WorkspaceChanged;
        QueueSync();
    }
    private void WorkspaceChanged(object? sender, PropertyChangedEventArgs e) => QueueSync();
    private void QueueSync()
    {
        if (_queued) return;
        _queued = true;
        Dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(() =>
        {
            _queued = false;
            if (_workspace is not { } workspace || !IsLoaded) return;
            _syncing = true;
            try
            {
                var selected = ContentObjectsList.SelectedItems;
                var desired = workspace.SelectedContentObjects;
                foreach (var item in selected.OfType<PdfContentObject>().ToArray())
                    if (!desired.Contains(item)) selected.Remove(item);
                foreach (var item in desired)
                    if (!selected.Contains(item)) selected.Add(item);
            }
            finally { _syncing = false; }
        }));
    }
    private void SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_syncing || _workspace is not { } workspace || !ReferenceEquals(ContentObjectsList.ItemsSource, workspace.ContentObjects)) return;
        try { workspace.SelectContentObjects(ContentObjectsList.SelectedItems.OfType<PdfContentObject>()); }
        catch (Exception error) { workspace.Viewport.ReportError(error); QueueSync(); }
    }
}
