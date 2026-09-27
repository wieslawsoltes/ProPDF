using System.Globalization;
using ProPDF.Core;

namespace ProPDF.Presentation;

public sealed partial class PdfWorkspace
{
    private PdfContentObjectReference? _contentEditorReference;
    private string _contentX = "0", _contentY = "0", _contentWidth = "100", _contentHeight = "40", _contentText = "", _contentFontSize = "14";
    private PdfUiCommand? _editObjects, _refreshObjects, _applyBounds, _deleteObject, _duplicateObject, _rotateObject, _flipObject, _replaceObjectText, _clipObject;
    public IReadOnlyList<PdfContentObject> ContentObjects => Viewport.ContentObjects;
    public string ContentStatus => Viewport.ContentStatus;
    public PdfContentObject? SelectedContentObject
    {
        get => Viewport.SelectedContentObject;
        set { if (value != Viewport.SelectedContentObject) { Viewport.SelectContentObject(value); RefreshContentState(); Changed(null); RefreshCommands(); } }
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
    private bool CanEditContent() => Can(PdfCapability.ContentReplacement) && SelectedContentObject is { CanEdit: true } item && item.Reference.Revision == Document?.Id;
    private bool CanInspectContent() => HasDocument() && _context.Inspector is IPdfContentService;
    public PdfUiCommand EditObjectsCommand => _editObjects ??= Command(async token =>
    { Viewport.Tool = PdfTool.EditObject; await Viewport.LoadContentAsync(cancellationToken: token); }, CanInspectContent);
    public PdfUiCommand RefreshObjectsCommand => _refreshObjects ??= Command(token => Viewport.LoadContentAsync(force: true, cancellationToken: token), CanInspectContent);
    public PdfUiCommand ApplyObjectBoundsCommand => _applyBounds ??= Command(token =>
    {
        var item = SelectedContentObject!;
        return Session.ApplyAsync(new TransformContentObject(item.Reference, PdfViewportController.BoundsTransform(item.Bounds, ContentBounds())), item.Reference.Revision, token);
    }, CanEditContent);
    public PdfUiCommand DeleteObjectCommand => _deleteObject ??= Command(token =>
    {
        var item = SelectedContentObject!; return Session.ApplyAsync(new DeleteContentObject(item.Reference), item.Reference.Revision, token);
    }, CanEditContent);
    public PdfUiCommand DuplicateObjectCommand => _duplicateObject ??= Command(token =>
    {
        var item = SelectedContentObject!; return Session.ApplyAsync(new DuplicateContentObject(item.Reference, PdfAffineTransform.Translation(12, 12)), item.Reference.Revision, token);
    }, CanEditContent);
    public PdfUiCommand RotateObjectCommand => _rotateObject ??= Command(token =>
    {
        var item = SelectedContentObject!; var bounds = item.Bounds;
        return Session.ApplyAsync(new TransformContentObject(item.Reference, PdfAffineTransform.RotationAt(90, new(bounds.X + bounds.Width / 2, bounds.Y + bounds.Height / 2))), item.Reference.Revision, token);
    }, CanEditContent);
    public PdfUiCommand FlipObjectCommand => _flipObject ??= Command(token =>
    {
        var item = SelectedContentObject!; var bounds = item.Bounds;
        return Session.ApplyAsync(new TransformContentObject(item.Reference, PdfAffineTransform.ScaleAt(-1, 1, new(bounds.X + bounds.Width / 2, bounds.Y + bounds.Height / 2))), item.Reference.Revision, token);
    }, CanEditContent);
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
        var item = SelectedContentObject;
        if (_contentEditorReference == item?.Reference) return;
        _contentEditorReference = item?.Reference;
        if (item is null) return;
        static string Number(double value) => value.ToString("0.###", CultureInfo.InvariantCulture);
        _contentX = Number(item.Bounds.X); _contentY = Number(item.Bounds.Y); _contentWidth = Number(item.Bounds.Width); _contentHeight = Number(item.Bounds.Height);
        _contentText = item.Text ?? "";
    }
}
