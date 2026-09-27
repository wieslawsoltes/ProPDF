using System.Runtime.InteropServices.JavaScript;
using System.Text;
using System.Text.Json;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using ProPDF.Presentation;
using ProPDF.SampleSupport;

namespace ProPDF.Uno.Sample;

/// <summary>Opt-in bridge to real controls. Explicit JSON writing avoids reflection/trimming assumptions.</summary>
public static partial class BrowserTest
{
    private static PdfEditor? _editor;
    private static DemoWorkspace? _runtime;
    internal static void Initialize(PdfEditor editor, DemoWorkspace runtime) { _editor = editor; _runtime = runtime; }
    private static PdfWorkspace Workspace => _editor?.Workspace ?? throw new InvalidOperationException("Workspace not initialized.");
    private static string Json(Action<Utf8JsonWriter> write)
    {
        using var output = new MemoryStream();
        using (var writer = new Utf8JsonWriter(output)) { write(writer); writer.Flush(); }
        return Encoding.UTF8.GetString(output.ToArray());
    }
    [JSExport]
    public static string State() => Json(writer =>
    {
        var w = Workspace; var c = w.Viewport;
        using var scene = c.CaptureScene();
        writer.WriteStartObject();
        writer.WriteString("revision", w.Document?.Id.ToString()); writer.WriteNumber("pages", c.PageCount);
        writer.WriteNumber("page", c.CurrentPage); writer.WriteNumber("zoom", c.Zoom);
        writer.WriteBoolean("dirty", w.Session.IsDirty); writer.WriteBoolean("canUndo", w.Session.CanUndo);
        writer.WriteBoolean("canRedo", w.Session.CanRedo); writer.WriteBoolean("busy", w.IsBusy);
        writer.WriteString("error", c.LastError); writer.WriteNumber("tiles", scene.TileCount);
        writer.WriteNumber("matches", c.SearchHits.Count); writer.WriteNumber("selected", w.SelectedContentObjects.Count);
        writer.WriteString("title", w.Document?.Metadata.Title); writer.WriteNumber("fields", w.Fields.Count);
        writer.WriteNumber("annotations", w.Annotations.Count); writer.WriteString("tool", c.Tool.ToString());
        writer.WriteEndObject();
    });
    [JSExport]
    public static string Controls() => Json(writer =>
    {
        writer.WriteStartArray();
        foreach (var e in Elements().Where(e => !string.IsNullOrEmpty(e.Name)))
        {
            var p = e.TransformToVisual(_editor).TransformPoint(new Windows.Foundation.Point(0, 0));
            writer.WriteStartObject(); writer.WriteString("name", e.Name);
            writer.WriteNumber("x", p.X); writer.WriteNumber("y", p.Y);
            writer.WriteNumber("width", e.ActualWidth); writer.WriteNumber("height", e.ActualHeight);
            writer.WriteString("type", e.GetType().Name); writer.WriteEndObject();
        }
        writer.WriteEndArray();
    });
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
        return Json(writer =>
        {
            writer.WriteStartArray();
            for (var index = 0; index < Workspace.ContentObjects.Count; index++)
            {
                var o = Workspace.ContentObjects[index];
                writer.WriteStartObject(); writer.WriteNumber("index", index); writer.WriteString("text", o.Text);
                writer.WriteString("kind", o.Kind.ToString()); writer.WriteNumber("x", o.Bounds.X); writer.WriteNumber("y", o.Bounds.Y);
                writer.WriteNumber("width", o.Bounds.Width); writer.WriteNumber("height", o.Bounds.Height);
                writer.WriteBoolean("editable", o.CanEdit); writer.WriteEndObject();
            }
            writer.WriteEndArray();
        });
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
