namespace ProPDF.Uno;

/// <summary>Review comments, native form fields, document metadata, attachments, navigation and output panels.</summary>
public sealed class PdfInspectorPanels : UserControl
{
    private readonly ContentControl _body = new();
    private readonly UIElement[] _panels;
    public PdfInspectorPanels(bool showSelector = true)
    {
        _panels = [new PdfContentPanel(), Review(), Forms(), Navigate(), Output(), Document()];
        var root = new Grid(); root.RowDefinitions.Add(new() { Height = GridLength.Auto }); root.RowDefinitions.Add(new() { Height = new(1, GridUnitType.Star) });
        var choice = new ComboBox { ItemsSource = new[] { "Edit content", "Review", "Forms", "Navigate", "Export / compare", "Document" }, SelectedIndex = 0,
            HorizontalAlignment = HorizontalAlignment.Stretch, Margin = new Thickness(12, 8, 12, 4) };
        AutomationProperties.SetAutomationId(choice, "InspectorSection"); AutomationProperties.SetName(choice, "Inspector section");
        choice.SelectionChanged += (_, _) => { if (choice.SelectedIndex >= 0) _body.Content = _panels[choice.SelectedIndex]; };
        if (showSelector) root.Children.Add(choice); Grid.SetRow(_body, 1); root.Children.Add(_body); _body.Content = _panels[0]; Content = root;
    }
    /// <summary>Select an existing inspector without recreating its controls or losing drafts.</summary>
    public void SelectSection(int index)
    {
        if ((uint)index >= (uint)_panels.Length) throw new ArgumentOutOfRangeException(nameof(index));
        if (!ReferenceEquals(_body.Content, _panels[index])) _body.Content = _panels[index];
    }
    private static StackPanel Body() => new() { Margin = new Thickness(12), Spacing = 6 };
    private static ScrollViewer Scroll(StackPanel p) => new() { Content = p, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled };
    private static UIElement Review()
    {
        var p = Body(); p.Children.Add(PdfUi.Label("COMMENTS", true));
        p.Children.Add(PdfUi.List("Annotations", "SelectedAnnotation", "Comments", "Contents", 220));
        PdfUi.Field(p, "Comment", "Comment", true); PdfUi.Actions(p, ("Update", "UpdateCommentCommand"), ("Delete", "DeleteAnnotationCommand"));
        p.Children.Add(PdfUi.Label("Choose a review tool, then drag on the page. Text and note tools use the tool-settings text."));
        p.Children.Add(PdfUi.Label("REDACTION", true));
        p.Children.Add(PdfUi.Label("Marks are staging only. Apply removes content. Original files and undo history still retain prior content."));
        p.Children.Add(PdfUi.Button("Apply redactions", "ApplyRedactionsCommand")); p.Children.Add(PdfUi.Button("Clear marks", "ClearRedactionsCommand"));
        p.Children.Add(PdfUi.Button("Refresh inspector", "RefreshInspectorCommand")); return Scroll(p);
    }
    private static UIElement Forms()
    {
        var p = Body(); p.Children.Add(PdfUi.Label("FORM FIELDS", true));
        p.Children.Add(PdfUi.Choice("Fields", "SelectedField", "Field", "Name")); PdfUi.Field(p, "Field value", "FieldValue", true);
        p.Children.Add(PdfUi.Button("Apply value", "SetFieldValueCommand")); p.Children.Add(PdfUi.Button("Flatten all fields", "FlattenFormsCommand"));
        p.Children.Add(PdfUi.Label("Draw fields using Text form field or Checkbox in the tool selector. Toolbar text supplies a unique name. Read-only fields remain protected.")); return Scroll(p);
    }
    private static UIElement Navigate()
    {
        var p = Body(); p.Children.Add(PdfUi.Label("BOOKMARKS", true));
        p.Children.Add(PdfUi.List("NavigationBookmarks", "SelectedBookmark", "Bookmarks", height:210));
        PdfUi.Actions(p, ("Go", "FollowBookmarkCommand"), ("Back", "BackCommand"), ("Forward", "ForwardCommand"));
        PdfUi.Actions(p, ("Child", "InsertChildBookmarkCommand"), ("Rename", "RenameBookmarkCommand"), ("Delete", "DeleteBookmarkCommand"));
        p.Children.Add(PdfUi.Button("Bookmark current page", "AddBookmarkCommand"));
        p.Children.Add(PdfUi.Label("New and renamed bookmarks use tool-settings text."));
        p.Children.Add(PdfUi.Label("LINKS", true)); p.Children.Add(PdfUi.List("NavigationLinks", "SelectedLink", "Document links", height:150));
        PdfUi.Actions(p, ("Follow", "FollowLinkCommand"), ("Copy URI", "CopyLinkCommand")); p.Children.Add(PdfUi.Button("Link selected region", "LinkSelectionCommand"));
        p.Children.Add(PdfUi.Label("Toolbar text sets the destination page. External links require confirmation."));
        p.Children.Add(PdfUi.Button("Refresh navigation", "ReloadNavigationCommand")); return Scroll(p);
    }
    private static UIElement Output()
    {
        var p = Body(); p.Children.Add(PdfUi.Label("EXPORT", true)); PdfUi.Field(p, "Image DPI · 18 to 1200", "ExportDpi");
        PdfUi.Actions(p, ("PNG", "ExportPngCommand"), ("JPEG", "ExportJpegCommand"));
        PdfUi.Field(p, "Pages · all, odd, even, 1-3, last", "PageRange");
        p.Children.Add(PdfUi.Button("Export text", "ExportTextCommand")); p.Children.Add(PdfUi.Button("Extract range", "ExtractRangeCommand"));
        p.Children.Add(PdfUi.Label("COMPARE", true)); p.Children.Add(PdfUi.Button("Compare PDF", "CompareCommand"));
        p.Children.Add(PdfUi.Choice("ComparisonPages", "SelectedComparisonPage", "Comparison page")); p.Children.Add(PdfUi.BoundText("ComparisonSummary"));
        p.Children.Add(PdfUi.Button("Save comparison", "SaveComparisonCommand")); return Scroll(p);
    }
    private static UIElement Document()
    {
        var p = Body(); p.Children.Add(PdfUi.Label("DOCUMENT", true));
        PdfUi.Field(p, "Title", "DocumentTitle"); PdfUi.Field(p, "Author", "DocumentAuthor"); PdfUi.Field(p, "Subject", "DocumentSubject"); PdfUi.Field(p, "Keywords", "DocumentKeywords");
        p.Children.Add(PdfUi.Button("Save metadata", "SaveMetadataCommand"));
        p.Children.Add(PdfUi.Label("ATTACHMENTS", true)); p.Children.Add(PdfUi.List("Attachments", "SelectedAttachment", "Attachments", "Name", 150));
        PdfUi.Actions(p, ("Add", "AddAttachmentCommand"), ("Save", "SaveAttachmentCommand"), ("Remove", "DeleteAttachmentCommand"));
        return Scroll(p);
    }
}
