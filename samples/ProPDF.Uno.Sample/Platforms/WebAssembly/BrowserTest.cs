using System.Runtime.InteropServices.JavaScript;
using System.Text.Json;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using ProPDF.Core;
using ProPDF.Presentation;
using ProPDF.SampleSupport;

namespace ProPDF.Uno.Sample;

/// <summary>Opt-in browser regression bridge. Tests resolve real bound controls; no replacement PDF engine or canned results.</summary>
public static partial class BrowserTest
{
    private static PdfEditor? _editor;
    private static DemoWorkspace? _runtime;
    internal static void Initialize(PdfEditor editor, DemoWorkspace runtime) { _editor = editor; _runtime = runtime; }
    private static PdfWorkspace Workspace => _editor?.Workspace ?? throw new InvalidOperationException("Workspace not initialized.");
    [JSExport]
    public static string State()
    {
        var w = Workspace; var c = w.Viewport;
        using var scene = c.CaptureScene();
        return JsonSerializer.Serialize(new { revision = w.Document?.Id, pages = c.PageCount, page = c.CurrentPage, zoom = c.Zoom,
            dirty = w.Session.IsDirty, canUndo = w.Session.CanUndo, canRedo = w.Session.CanRedo, busy = w.IsBusy,
            error = c.LastError, tiles = scene.TileCount, matches = c.SearchHits.Count, selected = w.SelectedContentObjects.Count,
            title = w.Document?.Metadata.Title, fields = w.Fields.Count, annotations = w.Annotations.Count, tool = c.Tool.ToString() });
    }
    [JSExport]
    public static string Controls() => JsonSerializer.Serialize(Elements().Where(e => !string.IsNullOrEmpty(e.Name)).Select(e =>
    {
        var p = e.TransformToVisual(_editor).TransformPoint(new Windows.Foundation.Point(0, 0));
        return new { name = e.Name, x = p.X, y = p.Y, width = e.ActualWidth, height = e.ActualHeight, type = e.GetType().Name };
    }));
    [JSExport]
    public static async Task Click(string name)
    {
        if (Find(name) is not Button b || b.Command is not PdfUiCommand command || !command.CanExecute(null)) throw new InvalidOperationException("Bound command unavailable: " + name);
        await command.ExecuteAsync(); await _runtime!.Viewport.WaitForRenderingAsync();
    }
    [JSExport]
    public static void Text(string name, string value)
    { if (Find(name) is not TextBox box) throw new ArgumentException("Text input not found: " + name); box.Text = value; }
    [JSExport]
    public static void Choose(string name, int index)
    {
        if (name == "InspectorSection")
        {
            var choice = Elements().OfType<ComboBox>().Single(e => Microsoft.UI.Xaml.Automation.AutomationProperties.GetAutomationId(e) == name);
            choice.SelectedIndex = index; return;
        }
        if (Find(name) is not ComboBox box) throw new ArgumentException("Choice not found: " + name); box.SelectedIndex = index;
    }
    [JSExport]
    public static void SelectObject(int index, bool extend)
    {
        var list = (ListView)Find("ContentObjectsList");
        if (!extend) list.SelectedItems.Clear(); list.SelectedItems.Add(list.Items[index]);
    }
    [JSExport]
    public static async Task<string> TextContent()
    { var text = await _runtime!.Backend.GetPageTextAsync(_runtime.Session.Current!, _runtime.Viewport.CurrentPage); return text.Text; }
    [JSExport]
    public static async Task<string> Objects()
    {
        await Workspace.Viewport.LoadContentAsync();
        return JsonSerializer.Serialize(Workspace.ContentObjects.Select((o, index) => new { index, text = o.Text, kind = o.Kind.ToString(), x = o.Bounds.X, y = o.Bounds.Y, width = o.Bounds.Width, height = o.Bounds.Height, editable = o.CanEdit }));
    }
    private static FrameworkElement Find(string name) => Elements().FirstOrDefault(e => e.Name == name) ?? throw new ArgumentException("Control missing: " + name);
    private static IEnumerable<FrameworkElement> Elements()
    {
        var pending = new Stack<DependencyObject>(); if (_editor is not null) pending.Push(_editor);
        while (pending.TryPop(out var node))
        {
            if (node is FrameworkElement element) yield return element;
            for (var i = VisualTreeHelper.GetChildrenCount(node) - 1; i >= 0; i--) pending.Push(VisualTreeHelper.GetChild(node, i));
        }
    }
}
