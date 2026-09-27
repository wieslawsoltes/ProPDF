using System.Collections.Immutable;

namespace ProPDF.Core;

public readonly record struct PdfColor(byte R, byte G, byte B, byte A = 255)
{
    public static PdfColor Black => new(0, 0, 0);
    public static PdfColor White => new(255, 255, 255);
    public static PdfColor Yellow => new(255, 224, 72);
    public static PdfColor Blue => new(45, 99, 230);
}

/// <summary>Immutable, bounded image, attachment or font bytes. The caller retains ownership of its original buffer.</summary>
public sealed class PdfBinaryAsset
{
    private readonly byte[] _bytes;
    public PdfBinaryAsset(ReadOnlySpan<byte> bytes, int maximumBytes = 128 * 1024 * 1024)
    {
        if (bytes.IsEmpty || bytes.Length > maximumBytes) throw new ArgumentOutOfRangeException(nameof(bytes));
        _bytes = bytes.ToArray();
    }
    public int Length => _bytes.Length;
    public byte[] ToArray() => (byte[])_bytes.Clone();
    public override string ToString() => $"Binary asset ({Length} bytes)";
}

public abstract record PdfEditOperation(PdfCapability Capability, string Description) : IPdfEditOperation;
public sealed record RotatePage(int PageNumber, int ClockwiseDegrees = 90) : PdfEditOperation(PdfCapability.PageOrganization, "Rotate page");
public sealed record DeletePage(int PageNumber) : PdfEditOperation(PdfCapability.PageOrganization, "Delete page");
/// <summary>Destination is the final one-based page position, not an insert-before index.</summary>
public sealed record MovePage(int PageNumber, int Destination) : PdfEditOperation(PdfCapability.PageOrganization, "Move page");
public sealed record InsertBlankPage(int BeforePage, PdfSize Size) : PdfEditOperation(PdfCapability.PageOrganization, "Insert blank page");
public sealed record CropPage(int PageNumber, PdfRect ViewBounds) : PdfEditOperation(PdfCapability.PageOrganization, "Crop page");
public sealed record InsertDocumentPages : PdfEditOperation
{
    public InsertDocumentPages(PdfSnapshot document, IEnumerable<int> pages, int beforePage)
        : base(PdfCapability.PageOrganization, "Insert document pages")
    {
        Document = document ?? throw new ArgumentNullException(nameof(document));
        Pages = pages.ToImmutableArray();
        BeforePage = beforePage;
    }
    public PdfSnapshot Document { get; }
    public ImmutableArray<int> Pages { get; }
    public int BeforePage { get; }
}

/// <summary>Native PDF text insertion. Baseline uses the normalized top-left viewer coordinate system.</summary>
public sealed record AddText(int PageNumber, PdfPoint Baseline, string Text, double FontSize = 14,
    string StandardFont = "Helvetica", PdfColor? Color = null, PdfBinaryAsset? EmbeddedFont = null)
    : PdfEditOperation(PdfCapability.ContentInsertion, "Insert text");
public sealed record AddImage(int PageNumber, PdfRect Bounds, PdfBinaryAsset Image)
    : PdfEditOperation(PdfCapability.ContentInsertion, "Insert image");
public enum PdfShapeKind { Rectangle, Ellipse }
public sealed record AddShape(int PageNumber, PdfRect Bounds, PdfShapeKind Kind = PdfShapeKind.Rectangle,
    PdfColor? Stroke = null, PdfColor? Fill = null, double StrokeWidth = 1)
    : PdfEditOperation(PdfCapability.ContentInsertion, "Insert vector shape");

public enum PdfAnnotationKind { Note, FreeText, Highlight, Underline, StrikeOut, Rectangle, Ellipse, Link }
public sealed record AddAnnotation(int PageNumber, PdfRect Bounds, PdfAnnotationKind Kind, string Contents = "",
    string Author = "", PdfColor? Color = null, string? Uri = null, PdfBinaryAsset? EmbeddedFont = null)
    : PdfEditOperation(PdfCapability.Annotations, "Add annotation");
public sealed record AddInkAnnotation : PdfEditOperation
{
    public AddInkAnnotation(int pageNumber, IEnumerable<PdfPoint> points, PdfColor? color = null, double width = 2)
        : base(PdfCapability.Annotations, "Add ink annotation")
    {
        PageNumber = pageNumber;
        Points = points.ToImmutableArray();
        Color = color ?? PdfColor.Blue;
        Width = width;
    }
    public int PageNumber { get; }
    public ImmutableArray<PdfPoint> Points { get; }
    public PdfColor Color { get; }
    public double Width { get; }
}
public sealed record DeleteAnnotation(int PageNumber, string Id) : PdfEditOperation(PdfCapability.Annotations, "Delete annotation");
public sealed record UpdateAnnotationComment(int PageNumber, string Id, string Contents, string Author = "")
    : PdfEditOperation(PdfCapability.Annotations, "Update annotation comment");

public enum PdfFormFieldKind { Text, CheckBox, ComboBox, ListBox }
public sealed record AddFormField : PdfEditOperation
{
    public AddFormField(int pageNumber, PdfRect bounds, string name, PdfFormFieldKind kind = PdfFormFieldKind.Text,
        string value = "", IEnumerable<string>? choices = null, bool readOnly = false, bool required = false)
        : base(PdfCapability.Forms, "Create form field")
    {
        PageNumber = pageNumber;
        Bounds = bounds;
        Name = name;
        Kind = kind;
        Value = value;
        Choices = choices?.ToImmutableArray() ?? [];
        ReadOnly = readOnly;
        Required = required;
    }
    public int PageNumber { get; }
    public PdfRect Bounds { get; }
    public string Name { get; }
    public PdfFormFieldKind Kind { get; }
    public string Value { get; }
    public ImmutableArray<string> Choices { get; }
    public bool ReadOnly { get; }
    public bool Required { get; }
}
public sealed record SetFormValue(string Name, string Value) : PdfEditOperation(PdfCapability.Forms, "Fill form field");
public sealed record RemoveFormField(string Name) : PdfEditOperation(PdfCapability.Forms, "Remove form field");
public sealed record FlattenForms(string? Name = null) : PdfEditOperation(PdfCapability.Forms, "Flatten forms");

/// <summary>Removes supported intersecting content groups with the owned native redactor; it is not a painted rectangle. Does not redact other pages or metadata.</summary>
public sealed record RedactRegion(int PageNumber, PdfRect Bounds, PdfColor? Fill = null)
    : PdfEditOperation(PdfCapability.Redaction, "Apply content redaction");
/// <summary>Removes everything in a region and inserts new text. This is not automatic paragraph reflow.</summary>
public sealed record ReplaceRegionText(int PageNumber, PdfRect Bounds, string Text, double FontSize = 14, PdfBinaryAsset? EmbeddedFont = null)
    : PdfEditOperation(PdfCapability.ContentReplacement, "Replace region content with text");
public sealed record SetDocumentMetadata(PdfMetadata Metadata) : PdfEditOperation(PdfCapability.Metadata, "Update document metadata");
public sealed record AddAttachment(string Name, PdfBinaryAsset Data, string MediaType = "application/octet-stream")
    : PdfEditOperation(PdfCapability.Attachments, "Embed attachment");
public sealed record RemoveAttachment(string Name) : PdfEditOperation(PdfCapability.Attachments, "Remove attachment");
public sealed record AddBookmark(string Title, int PageNumber) : PdfEditOperation(PdfCapability.Bookmarks, "Add bookmark");
public sealed record RemoveBookmark(int RootIndex) : PdfEditOperation(PdfCapability.Bookmarks, "Remove bookmark");

/// <summary>Credentials are excluded from diagnostic and JSON property output.</summary>
public sealed class PdfEncryptionSettings
{
    private readonly string _userPassword;
    private readonly string _ownerPassword;
    public PdfEncryptionSettings(string userPassword, string ownerPassword, bool allowPrinting = true, bool allowCopy = false)
    {
        if (string.IsNullOrEmpty(ownerPassword) || ownerPassword.Length < 8)
            throw new ArgumentException("Use an owner password of at least eight characters.", nameof(ownerPassword));
        _userPassword = userPassword ?? throw new ArgumentNullException(nameof(userPassword));
        _ownerPassword = ownerPassword;
        AllowPrinting = allowPrinting;
        AllowCopy = allowCopy;
    }
    public bool AllowPrinting { get; }
    public bool AllowCopy { get; }
    public string GetUserPassword() => _userPassword;
    public string GetOwnerPassword() => _ownerPassword;
    public override string ToString() => "AES-256 settings; credentials [redacted]";
}
/// <summary>Null settings remove encryption, but only after successful owner-authorized opening.</summary>
public sealed record ChangeEncryption(PdfEncryptionSettings? Settings)
    : PdfEditOperation(PdfCapability.Encryption, "Change document encryption");

public sealed record PdfAnnotationInfo(int PageNumber, string Id, string Kind, PdfRect Bounds, string Contents, string Author);
public sealed record PdfFormFieldInfo(string Name, string Kind, string Value, bool ReadOnly, bool Required);
public sealed record PdfAttachmentInfo(string Name, string FileName);
public sealed record PdfBookmarkInfo(string Title, int Depth);
public sealed record PdfDocumentInspection(IReadOnlyList<PdfAnnotationInfo> Annotations, IReadOnlyList<PdfFormFieldInfo> Fields,
    IReadOnlyList<PdfAttachmentInfo> Attachments, IReadOnlyList<PdfBookmarkInfo> Bookmarks, bool IsEncrypted, bool HasSignatures, bool HasXfa);

public interface IPdfDocumentInspector
{
    Task<PdfDocumentInspection> InspectAsync(PdfSnapshot source, CancellationToken cancellationToken = default);
}
