using System.Globalization;
using ProPDF.Core;

namespace ProPDF.Presentation;

/// <summary>Reference for content alignment. Page and Region translate the selection as one unit.</summary>
public enum PdfContentAlignmentReference { Selection, Page, Region }

public sealed partial class PdfWorkspace
{
    private IReadOnlyList<PdfContentObject>? _contentEditorSelection;
    private string _contentX = "0", _contentY = "0", _contentWidth = "100", _contentHeight = "40", _contentText = "", _contentFontSize = "14";
    private PdfUiCommand? _editObjects, _refreshObjects, _applyBounds, _deleteObject, _duplicateObject, _rotateObject, _flipObject, _replaceObjectText, _clipObject;
    private PdfUiCommand? _selectAllObjects, _selectRegionObjects, _clearObjects, _alignObjects, _distributeObjects;
    private PdfSelectionAlignment _contentAlignment;
    private PdfContentAlignmentReference _contentAlignmentReference;
    private PdfSelectionDistribution _contentDistribution;
    public IReadOnlyList<PdfContentObject> ContentObjects => Viewport.ContentObjects;
    public IReadOnlyList<PdfContentObject> SelectedContentObjects => Viewport.SelectedContentObjects;
    public string ContentStatus => Viewport.ContentStatus;
    public string ContentSelectionSummary => $"{SelectedContentObjects.Count} selected · Ctrl/Command or Shift-click to extend selection";
    public PdfContentObject? SelectedContentObject
    {
        get => Viewport.SelectedContentObject;
        set
        {
            if (value != Viewport.SelectedContentObject || SelectedContentObjects.Count > 1)
                SelectContentObjects(value is null ? [] : [value]);
        }
    }
    public void SelectContentObjects(IEnumerable<PdfContentObject> values)
    {
        Viewport.SelectContentObjects(values); RefreshContentState(); Changed(null); RefreshCommands();
    }
    public string ContentX { get => _contentX; set => Set(ref _contentX, value); }
    public string ContentY { get => _contentY; set => Set(ref _contentY, value); }
    public string ContentWidth { get => _contentWidth; set => Set(ref _contentWidth, value); }
    public string ContentHeight { get => _contentHeight; set => Set(ref _contentHeight, value); }
    public string ContentText { get => _contentText; set => Set(ref _contentText, value); }
    public string ContentFontSize
    {
        get => _contentFontSize;
        set
        {
            if (Set(ref _contentFontSize, value) && double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var size) && double.IsFinite(size) && size is >= 1 and <= 1000)
                Viewport.TextBoxFontSize = size;
        }
    }
    public PdfTextAlignment[] TextAlignments { get; } = Enum.GetValues<PdfTextAlignment>();
    public PdfTextAlignment TextAlignment { get => Viewport.TextBoxAlignment; set { Viewport.TextBoxAlignment = value; Changed(); } }
    public IReadOnlyList<PdfContentAlignmentReference> ContentAlignmentReferences { get; } = Array.AsReadOnly(Enum.GetValues<PdfContentAlignmentReference>());
    public PdfContentAlignmentReference ContentAlignmentReference
    {
        get => _contentAlignmentReference;
        set { if (Enum.IsDefined(value) && Set(ref _contentAlignmentReference, value)) RefreshCommands(); }
    }
    public IReadOnlyList<PdfSelectionAlignment> ContentAlignments { get; } = Array.AsReadOnly(Enum.GetValues<PdfSelectionAlignment>());
    public IReadOnlyList<PdfSelectionDistribution> ContentDistributions { get; } = Array.AsReadOnly(Enum.GetValues<PdfSelectionDistribution>());
    public PdfSelectionAlignment ContentAlignment { get => _contentAlignment; set { if (Enum.IsDefined(value)) Set(ref _contentAlignment, value); } }
    public PdfSelectionDistribution ContentDistribution { get => _contentDistribution; set { if (Enum.IsDefined(value)) Set(ref _contentDistribution, value); } }
    // Appearance, image and text replacement tools require exactly one selected object.
    private bool CanEditContent() => SelectedContentObjects.Count == 1 && CanEditContentSelection();
    private bool CanEditContentSelection() => Can(PdfCapability.ContentReplacement) && SelectedContentObjects.Count > 0 &&
        SelectedContentObjects.All(item => item.CanEdit && item.Reference.Revision == Document?.Id);
    private bool CanInspectContent() => HasDocument() && _context.Inspector is IPdfContentService;
    public PdfUiCommand EditObjectsCommand => _editObjects ??= Command(async token =>
    { Viewport.Tool = PdfTool.EditObject; await Viewport.LoadContentAsync(cancellationToken: token); }, CanInspectContent);
    public PdfUiCommand RefreshObjectsCommand => _refreshObjects ??= Command(token => Viewport.LoadContentAsync(force: true, cancellationToken: token), CanInspectContent);
    public PdfUiCommand SelectAllObjectsCommand => _selectAllObjects ??= Command(async token =>
    {
        await Viewport.LoadContentAsync(cancellationToken: token);
        SelectContentObjects(ContentObjects);
        Viewport.Tool = PdfTool.EditObject;
    }, CanInspectContent);
    public PdfUiCommand SelectRegionObjectsCommand => _selectRegionObjects ??= Command(async token =>
    {
        var region = Viewport.Selection ?? throw new InvalidOperationException("Select a page region first.");
        await Viewport.LoadContentAsync(region.PageNumber, cancellationToken: token);
        if (Document?.Id != region.Revision) throw new PdfRevisionConflictException();
        SelectContentObjects(ContentObjects.Where(item => item.Bounds.Intersects(region.Bounds)));
        Viewport.Tool = PdfTool.EditObject;
    }, () => CanInspectContent() && Viewport.Selection is { Bounds.IsEmpty: false } region && region.Revision == Document?.Id);
    public PdfUiCommand ClearObjectsCommand => _clearObjects ??= Command(_ =>
    { SelectContentObjects([]); return Task.CompletedTask; }, () => SelectedContentObjects.Count != 0);
    public PdfUiCommand ApplyObjectBoundsCommand => _applyBounds ??= Command(token =>
    {
        var items = SelectedContentObjects;
        var transform = PdfViewportController.BoundsTransform(PdfContentSelection.Bounds(items), ContentBounds());
        return Session.ApplyAsync(PdfContentSelection.Transform(items, transform), items[0].Reference.Revision, token);
    }, CanEditContentSelection);
    public PdfUiCommand DeleteObjectCommand => _deleteObject ??= Command(token =>
    {
        var items = SelectedContentObjects;
        return Session.ApplyAsync(PdfContentSelection.Delete(items), items[0].Reference.Revision, token);
    }, CanEditContentSelection);
    public PdfUiCommand DuplicateObjectCommand => _duplicateObject ??= Command(token =>
    {
        var items = SelectedContentObjects;
        return Session.ApplyAsync(PdfContentSelection.Duplicate(items, PdfAffineTransform.Translation(12, 12)), items[0].Reference.Revision, token);
    }, CanEditContentSelection);
    public PdfUiCommand RotateObjectCommand => _rotateObject ??= Command(token =>
    {
        var items = SelectedContentObjects; var bounds = PdfContentSelection.Bounds(items);
        return Session.ApplyAsync(PdfContentSelection.Transform(items, PdfAffineTransform.RotationAt(90, new(bounds.X + bounds.Width / 2, bounds.Y + bounds.Height / 2))), items[0].Reference.Revision, token);
    }, CanEditContentSelection);
    public PdfUiCommand FlipObjectCommand => _flipObject ??= Command(token =>
    {
        var items = SelectedContentObjects; var bounds = PdfContentSelection.Bounds(items);
        return Session.ApplyAsync(PdfContentSelection.Transform(items, PdfAffineTransform.ScaleAt(-1, 1, new(bounds.X + bounds.Width / 2, bounds.Y + bounds.Height / 2))), items[0].Reference.Revision, token);
    }, CanEditContentSelection);
    public PdfUiCommand AlignObjectsCommand => _alignObjects ??= Command(token =>
    {
        var items = SelectedContentObjects;
        var edit = ContentAlignmentReference switch
        {
            PdfContentAlignmentReference.Page => PdfContentSelection.AlignToBounds(items, PageAlignmentBounds(items[0].Reference.PageNumber), ContentAlignment),
            PdfContentAlignmentReference.Region => PdfContentSelection.AlignToBounds(items, RegionAlignmentBounds(items), ContentAlignment),
            _ => PdfContentSelection.Align(items, ContentAlignment)
        };
        return Session.ApplyAsync(edit, items[0].Reference.Revision, token);
    }, () => CanEditContentSelection() && (ContentAlignmentReference switch
    {
        PdfContentAlignmentReference.Selection => SelectedContentObjects.Count >= 2,
        PdfContentAlignmentReference.Page => true,
        PdfContentAlignmentReference.Region => HasContentAlignmentRegion(),
        _ => false
    }));
    private PdfRect PageAlignmentBounds(int pageNumber)
    {
        var size = Document!.GetPage(pageNumber).Size;
        return new(0, 0, size.Width, size.Height);
    }
    private bool HasContentAlignmentRegion() => SelectedContentObjects.Count != 0 &&
        Viewport.Selection is { Bounds.IsEmpty: false } region && region.Revision == Document?.Id &&
        region.PageNumber == SelectedContentObjects[0].Reference.PageNumber;
    private PdfRect RegionAlignmentBounds(IReadOnlyList<PdfContentObject> items)
    {
        var region = Viewport.Selection ?? throw new InvalidOperationException("Select an alignment region on the objects' page first.");
        if (region.Revision != items[0].Reference.Revision) throw new PdfRevisionConflictException();
        if (region.Bounds.IsEmpty || region.PageNumber != items[0].Reference.PageNumber)
            throw new InvalidOperationException("The alignment region must be on the selected objects' page.");
        return region.Bounds;
    }
    public PdfUiCommand DistributeObjectsCommand => _distributeObjects ??= Command(token =>
    {
        var items = SelectedContentObjects;
        return Session.ApplyAsync(PdfContentSelection.Distribute(items, ContentDistribution), items[0].Reference.Revision, token);
    }, () => CanEditContentSelection() && SelectedContentObjects.Count >= 3);
    public PdfUiCommand ReplaceObjectTextCommand => _replaceObjectText ??= Command(token =>
    {
        var item = SelectedContentObject!; var size = ContentNumber(ContentFontSize); Viewport.TextBoxFontSize = size;
        return Session.ApplyAsync(new ReplaceContentText(item.Reference, ContentBounds(), ContentText, size, TextAlignment), item.Reference.Revision, token);
    }, () => CanEditContent() && SelectedContentObject?.Kind == PdfContentObjectKind.Text);
    public PdfUiCommand ClipObjectCommand => _clipObject ??= Command(token =>
    {
        var item = SelectedContentObject!; var region = Viewport.Selection!;
        return Session.ApplyAsync(new ClipContentObject(item.Reference, region.Bounds), item.Reference.Revision, token);
    }, () => CanEditContent() && Viewport.Selection is { } region && region.PageNumber == SelectedContentObject!.Reference.PageNumber && !region.Bounds.IsEmpty);
    private PdfRect ContentBounds() => new(ContentNumber(ContentX), ContentNumber(ContentY), ContentNumber(ContentWidth), ContentNumber(ContentHeight));
    private static double ContentNumber(string? input) => double.TryParse(input, NumberStyles.Float, CultureInfo.InvariantCulture, out var value) && double.IsFinite(value)
        ? value : throw new ArgumentException("Enter a finite number in PDF points, using a dot as the decimal separator.");
    private void RefreshContentState()
    {
        if (Viewport.Tool == PdfTool.EditObject && CanInspectContent()) _ = Viewport.LoadContentAsync();
        var items = SelectedContentObjects;
        if (ReferenceEquals(_contentEditorSelection, items)) return;
        _contentEditorSelection = items;
        ResetAppearanceDraft(items.Count == 1 ? items[0] : null);
        _contentText = items.Count == 1 ? items[0].Text ?? "" : "";
        if (Viewport.ContentSelectionBounds is not { } bounds) return;
        static string Number(double value) => value.ToString("0.###", CultureInfo.InvariantCulture);
        _contentX = Number(bounds.X); _contentY = Number(bounds.Y); _contentWidth = Number(bounds.Width); _contentHeight = Number(bounds.Height);
    }
}
