using System.ComponentModel;

namespace ProPDF.Uno;

/// <summary>Native Uno inspector over the same content-editing workspace used by WPF and Avalonia.</summary>
public sealed class PdfContentPanel : UserControl
{
    private readonly ListView _objects;
    private PdfWorkspace? _workspace;
    private bool _syncing, _queued;
    public PdfContentPanel()
    {
        var panel = PdfUi.Stack(); panel.Margin = new Thickness(12);
        Content = new ScrollViewer { Content = panel, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled };
        panel.Children.Add(PdfUi.Label("EXISTING CONTENT", true));
        PdfUi.Actions(panel, ("Select / drag", "EditObjectsCommand"), ("Refresh", "RefreshObjectsCommand"));
        panel.Children.Add(PdfUi.BoundText("ContentStatus"));
        _objects = new ListView { Name = "ContentObjectsList", Height = 150, SelectionMode = ListViewSelectionMode.Extended };
        AutomationProperties.SetName(_objects, "Content objects"); AutomationProperties.SetAutomationId(_objects, _objects.Name);
        PdfUi.Bind(_objects, ItemsControl.ItemsSourceProperty, "ContentObjects"); panel.Children.Add(_objects);
        panel.Children.Add(PdfUi.BoundText("ContentSelectionSummary"));
        PdfUi.Actions(panel, ("Select all", "SelectAllObjectsCommand"), ("In region", "SelectRegionObjectsCommand"), ("Clear", "ClearObjectsCommand"));
        panel.Children.Add(PdfUi.Label("POSITION / SIZE · PDF POINTS", true));
        var geometry = new Grid();
        geometry.ColumnDefinitions.Add(new()); geometry.ColumnDefinitions.Add(new()); geometry.RowDefinitions.Add(new()); geometry.RowDefinitions.Add(new());
        var fields = new[] { ("ContentX", "X"), ("ContentY", "Y"), ("ContentWidth", "Width"), ("ContentHeight", "Height") };
        for (var i = 0; i < fields.Length; i++) { var input = PdfUi.Input(fields[i].Item1, fields[i].Item2); input.Margin = new Thickness(2); Grid.SetRow(input, i / 2); Grid.SetColumn(input, i % 2); geometry.Children.Add(input); }
        panel.Children.Add(geometry); panel.Children.Add(PdfUi.Button("Apply position / size", "ApplyObjectBoundsCommand"));
        PdfUi.Actions(panel, ("Rotate 90°", "RotateObjectCommand"), ("Flip", "FlipObjectCommand"));
        PdfUi.Actions(panel, ("Duplicate", "DuplicateObjectCommand"), ("Delete", "DeleteObjectCommand"));
        var align = PdfUi.Stack();
        align.Children.Add(PdfUi.Label("Selection aligns members. Page or Region moves the selection together and keeps spacing. Bounds are approximate."));
        align.Children.Add(PdfUi.Choice("ContentAlignmentReferences", "ContentAlignmentReference", "Align relative to"));
        align.Children.Add(PdfUi.Choice("ContentAlignments", "ContentAlignment", "Alignment")); align.Children.Add(PdfUi.Button("Align objects", "AlignObjectsCommand"));
        align.Children.Add(PdfUi.Choice("ContentDistributions", "ContentDistribution", "Distribution")); align.Children.Add(PdfUi.Button("Distribute objects", "DistributeObjectsCommand"));
        var alignmentSection = Section("Align / space", align); alignmentSection.Name = "AlignmentSection"; panel.Children.Add(alignmentSection);
        var appearance = PdfUi.Stack();
        appearance.Children.Add(PdfUi.Label("Select one object. Blank values preserve its appearance. Colors use #RRGGBB or #RRGGBBAA."));
        var colors = PdfUi.Stack();
        PdfUi.Field(colors, "Fill / text color", "ContentFillColor"); PdfUi.Field(colors, "Stroke color", "ContentStrokeColor");
        PdfUi.Field(colors, "Line width", "ContentLineWidth"); PdfUi.Field(colors, "Dash lengths, or solid", "ContentDash");
        colors.Children.Add(PdfUi.Choice("ContentLineCaps", "ContentCap", "Line cap")); colors.Children.Add(PdfUi.Choice("ContentLineJoins", "ContentJoin", "Line join"));
        // IsEnabled is a Control property in Uno, not a Panel property. A control
        // wrapper both owns the binding and propagates disabled state to its fields.
        var colorGroup = new ContentControl { Content = colors, HorizontalContentAlignment = HorizontalAlignment.Stretch };
        PdfUi.Bind(colorGroup, Control.IsEnabledProperty, "CanSetContentColors");
        appearance.Children.Add(colorGroup);
        var painting = PdfUi.Choice("ContentPaintModes", "ContentPainting", "Path painting"); PdfUi.Bind(painting, IsEnabledProperty, "CanSetContentPainting"); appearance.Children.Add(painting);
        PdfUi.Field(appearance, "Opacity · 0–100%", "ContentOpacity"); appearance.Children.Add(PdfUi.Choice("ContentBlendModes", "ContentBlend", "Blend mode"));
        appearance.Children.Add(PdfUi.Button("Apply appearance", "ApplyAppearanceCommand")); panel.Children.Add(Section("Appearance", appearance, true));
        var image = PdfUi.Stack(); image.Children.Add(PdfUi.BoundText("ContentImageSummary"));
        var interpolation = new CheckBox { Content = "Interpolate pixels" }; PdfUi.Bind(interpolation, ToggleButton.IsCheckedProperty, "ContentImageInterpolation", true); image.Children.Add(interpolation);
        image.Children.Add(PdfUi.Button("Replace image…", "ReplaceImageCommand")); image.Children.Add(PdfUi.Button("Apply interpolation", "ApplyImageInterpolationCommand"));
        panel.Children.Add(Section("Image", image));
        var text = PdfUi.Stack(); PdfUi.Field(text, "Replacement text", "ContentText", true); PdfUi.Field(text, "Font size", "ContentFontSize");
        text.Children.Add(PdfUi.Choice("TextAlignments", "TextAlignment", "Text alignment")); text.Children.Add(PdfUi.Button("Replace selected text", "ReplaceObjectTextCommand"));
        text.Children.Add(PdfUi.Label("Wraps within the selected size. Overflow is rejected; other objects are preserved.")); panel.Children.Add(Section("Text replacement", text));
        panel.Children.Add(PdfUi.Button("Clip to selected region", "ClipObjectCommand")); panel.Children.Add(PdfUi.Label("Clipping hides content. It is not redaction."));
        _objects.SelectionChanged += Selected; DataContextChanged += (_, _) => Connect(); Loaded += (_, _) => Connect(); Unloaded += (_, _) => Disconnect();
    }
    private static Expander Section(string title, UIElement content, bool expanded = false) => new() { Header = title, Content = content, IsExpanded = expanded, HorizontalAlignment = HorizontalAlignment.Stretch };
    private void Disconnect() { if (_workspace is { } w) w.PropertyChanged -= Updated; _workspace = null; }
    private void Connect() { Disconnect(); _workspace = IsLoaded ? DataContext as PdfWorkspace : null; if (_workspace is { } w) w.PropertyChanged += Updated; Sync(); }
    private void Updated(object? sender, PropertyChangedEventArgs e) => Sync();
    private void Sync()
    {
        if (_queued) return; _queued = true;
        DispatcherQueue.TryEnqueue(() =>
        {
            _queued = false; if (!IsLoaded || _workspace is not { } w) return;
            _syncing = true;
            try
            {
                foreach (var item in _objects.SelectedItems.OfType<PdfContentObject>().ToArray()) if (!w.SelectedContentObjects.Contains(item)) _objects.SelectedItems.Remove(item);
                foreach (var item in w.SelectedContentObjects) if (!_objects.SelectedItems.Contains(item)) _objects.SelectedItems.Add(item);
            }
            finally { _syncing = false; }
        });
    }
    private void Selected(object sender, SelectionChangedEventArgs e)
    {
        if (_syncing || _workspace is not { } w || !ReferenceEquals(_objects.ItemsSource, w.ContentObjects)) return;
        try { w.SelectContentObjects(_objects.SelectedItems.OfType<PdfContentObject>()); }
        catch (Exception error) { w.Viewport.ReportError(error); Sync(); }
    }
}
