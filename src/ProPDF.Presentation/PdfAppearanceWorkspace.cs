using System.Globalization;
using ProPDF.Core;

namespace ProPDF.Presentation;

public sealed partial class PdfWorkspace
{
    private string _contentFillColor = "", _contentStrokeColor = "", _contentLineWidth = "", _contentOpacity = "";
    private string _contentBlend = "Preserve", _contentCap = "Preserve", _contentJoin = "Preserve", _contentDash = "";
    private PdfPathPaintMode _contentPainting;
    private bool _contentImageInterpolation;
    private PdfUiCommand? _applyAppearance, _replaceImage, _applyInterpolation;
    private static readonly IReadOnlyList<string> BlendChoices = Array.AsReadOnly(new[] { "Preserve" }.Concat(Enum.GetNames<PdfBlendMode>()).ToArray());
    private static readonly IReadOnlyList<string> CapChoices = Array.AsReadOnly(new[] { "Preserve" }.Concat(Enum.GetNames<PdfLineCap>()).ToArray());
    private static readonly IReadOnlyList<string> JoinChoices = Array.AsReadOnly(new[] { "Preserve" }.Concat(Enum.GetNames<PdfLineJoin>()).ToArray());

    public string ContentFillColor { get => _contentFillColor; set => Set(ref _contentFillColor, value ?? ""); }
    public string ContentStrokeColor { get => _contentStrokeColor; set => Set(ref _contentStrokeColor, value ?? ""); }
    public string ContentLineWidth { get => _contentLineWidth; set => Set(ref _contentLineWidth, value ?? ""); }
    public string ContentOpacity { get => _contentOpacity; set => Set(ref _contentOpacity, value ?? ""); }
    public string ContentBlend { get => _contentBlend; set { if (value is not null) Set(ref _contentBlend, value); } }
    public string ContentCap { get => _contentCap; set { if (value is not null) Set(ref _contentCap, value); } }
    public string ContentJoin { get => _contentJoin; set { if (value is not null) Set(ref _contentJoin, value); } }
    public string ContentDash { get => _contentDash; set => Set(ref _contentDash, value ?? ""); }
    public PdfPathPaintMode ContentPainting { get => _contentPainting; set => Set(ref _contentPainting, value); }
    public bool ContentImageInterpolation { get => _contentImageInterpolation; set => Set(ref _contentImageInterpolation, value); }
    public IReadOnlyList<string> ContentBlendModes => BlendChoices;
    public IReadOnlyList<string> ContentLineCaps => CapChoices;
    public IReadOnlyList<string> ContentLineJoins => JoinChoices;
    public IReadOnlyList<PdfPathPaintMode> ContentPaintModes { get; } = Array.AsReadOnly(Enum.GetValues<PdfPathPaintMode>());
    public bool CanSetContentColors => CanEditContent() && SelectedContentObject?.Kind is PdfContentObjectKind.Path or PdfContentObjectKind.Text;
    public bool CanSetContentPainting => CanEditContent() && SelectedContentObject?.Kind == PdfContentObjectKind.Path;
    public string ContentImageSummary => SelectedContentObject?.ImageInfo is { } image
        ? $"{image.PixelWidth} × {image.PixelHeight} pixels · {image.BitsPerComponent} bits/component · {image.ColorSpace}" + (image.HasSoftMask ? " · soft mask" : "")
        : "Select an image to replace its pixels or change interpolation. Other uses of the same image resource are preserved.";

    public PdfUiCommand ApplyAppearanceCommand => _applyAppearance ??= Command(token =>
    {
        var item = SelectedContentObject!;
        var patch = new PdfContentAppearance(ParseContentColor(ContentFillColor), ParseContentColor(ContentStrokeColor),
            OptionalContentNumber(ContentLineWidth), OptionalContentNumber(ContentOpacity) / 100,
            OptionalEnum<PdfBlendMode>(ContentBlend), ContentPainting, OptionalEnum<PdfLineCap>(ContentCap),
            OptionalEnum<PdfLineJoin>(ContentJoin), ParseContentDash(ContentDash));
        patch.Validate();
        return Session.ApplyAsync(new SetContentAppearance(item.Reference, patch), item.Reference.Revision, token);
    }, () => CanEditContent() && SelectedContentObject?.Kind is PdfContentObjectKind.Path or PdfContentObjectKind.Text or PdfContentObjectKind.Image);

    public PdfUiCommand ReplaceImageCommand => _replaceImage ??= Command(async token =>
    {
        // Capture selection and options before showing a modal dialog. A later revision must not receive this edit.
        var item = SelectedContentObject!;
        var interpolation = ContentImageInterpolation;
        var path = await _dialogs.PickOpenPathAsync(PdfFileKind.Image, token);
        if (path is null) return;
        await using var input = File.OpenRead(path);
        var bytes = await PdfStreams.ReadBoundedAsync(input, 32L * 1024 * 1024, token);
        await Session.ApplyAsync(new ReplaceContentImage(item.Reference, new PdfBinaryAsset(bytes), interpolation), item.Reference.Revision, token);
    }, () => CanEditContent() && SelectedContentObject?.Kind == PdfContentObjectKind.Image);

    public PdfUiCommand ApplyImageInterpolationCommand => _applyInterpolation ??= Command(token =>
    {
        var item = SelectedContentObject!;
        return Session.ApplyAsync(new SetContentImageInterpolation(item.Reference, ContentImageInterpolation), item.Reference.Revision, token);
    }, () => CanEditContent() && SelectedContentObject?.Kind == PdfContentObjectKind.Image);

    private static double? OptionalContentNumber(string value) => string.IsNullOrWhiteSpace(value) ? null : ContentNumber(value);
    private static T? OptionalEnum<T>(string value) where T : struct, Enum => value == "Preserve" ? null :
        Enum.TryParse<T>(value, out var result) && Enum.IsDefined(result) ? result : throw new ArgumentException("Unknown appearance option.");
    private static PdfColor? ParseContentColor(string value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        var hex = value.Trim(); if (hex.StartsWith('#')) hex = hex[1..];
        if (hex.Length is not (6 or 8) || !uint.TryParse(hex, NumberStyles.AllowHexSpecifier, CultureInfo.InvariantCulture, out var rgba))
            throw new ArgumentException("Use #RRGGBB or #RRGGBBAA for colors, or leave blank to preserve the original.");
        return hex.Length == 6 ? new((byte)(rgba >> 16), (byte)(rgba >> 8), (byte)rgba) :
            new((byte)(rgba >> 24), (byte)(rgba >> 16), (byte)(rgba >> 8), (byte)rgba);
    }
    private static PdfDashPattern? ParseContentDash(string value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        if (value.Trim().Equals("solid", StringComparison.OrdinalIgnoreCase)) return PdfDashPattern.Solid;
        if (value.Length > 512) throw new ArgumentException("Dash pattern is too long.");
        return new(value.Split([' ', ',', ';'], StringSplitOptions.RemoveEmptyEntries).Select(ContentNumber));
    }
    private void ResetAppearanceDraft(PdfContentObject? item)
    {
        _contentFillColor = _contentStrokeColor = _contentLineWidth = _contentOpacity = _contentDash = "";
        _contentBlend = _contentCap = _contentJoin = "Preserve";
        _contentPainting = PdfPathPaintMode.Preserve;
        _contentImageInterpolation = item?.ImageInfo?.Interpolate ?? false;
    }
}
