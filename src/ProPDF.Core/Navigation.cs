namespace ProPDF.Core;

public enum PdfDestinationFit { KeepZoom, Page, Width, Height, Rectangle }

/// <summary>A data-only destination. External links and named actions are never executed by the parser.</summary>
public sealed record PdfNavigationTarget(int PageNumber = 0, PdfDestinationFit Fit = PdfDestinationFit.Page,
    double? X = null, double? Y = null, double? Zoom = null, PdfRect? Region = null,
    string? ExternalUri = null, string? NamedAction = null, string? UnsupportedReason = null)
{
    public bool IsSupported => UnsupportedReason is null && (PageNumber > 0 || ExternalUri is not null ||
        NamedAction is "FirstPage" or "LastPage" or "NextPage" or "PrevPage");
}
public sealed record PdfNavigationBookmark(string Path, string Title, int Depth, bool IsOpen, PdfNavigationTarget Target)
{
    public override string ToString() => new string(' ', Math.Min(Depth, 32) * 2) + Title;
}
public sealed record PdfNavigationLink(int PageNumber, string Id, PdfRect Bounds, PdfNavigationTarget Target)
{
    public override string ToString() => $"Page {PageNumber}: " + (Target.ExternalUri ?? Target.NamedAction ??
        (Target.PageNumber > 0 ? $"page {Target.PageNumber}" : Target.UnsupportedReason ?? "Unresolved link"));
}
public sealed record PdfDocumentNavigation(Guid Revision, IReadOnlyList<PdfNavigationBookmark> Bookmarks,
    IReadOnlyList<PdfNavigationLink> Links);
public sealed record PdfNavigationOptions(int MaximumEntries = 100_000, int MaximumDepth = 128);

public interface IPdfNavigationService
{
    Task<PdfDocumentNavigation> ReadNavigationAsync(PdfSnapshot source, PdfNavigationOptions? options = null,
        CancellationToken cancellationToken = default);
}

public static class PdfUriPolicy
{
    public static bool TryNormalize(string? value, out Uri? uri)
    {
        uri = null;
        if (string.IsNullOrWhiteSpace(value) || value.Length > 4096 || value.Any(char.IsControl) || value.Contains('\\')) return false;
        if (!System.Uri.TryCreate(value, UriKind.Absolute, out var candidate) || candidate.Scheme is not ("https" or "http" or "mailto")) return false;
        if (!string.IsNullOrEmpty(candidate.UserInfo)) return false;
        uri = candidate;
        return true;
    }
}

/// <summary>Outline paths use zero-based sibling indexes, such as 0/2. Paths apply to the transaction's current revision.</summary>
public abstract record PdfOutlineEdit(string Description) : PdfEditOperation(PdfCapability.Bookmarks, Description);
public sealed record InsertOutline(string Title, int PageNumber, string? ParentPath = null, int Position = -1, bool IsOpen = true)
    : PdfOutlineEdit("Insert hierarchical bookmark");
public sealed record UpdateOutline(string Path, string? Title = null, int? PageNumber = null, bool? IsOpen = null)
    : PdfOutlineEdit("Update bookmark");
public sealed record DeleteOutline(string Path) : PdfOutlineEdit("Delete bookmark subtree");
public sealed record MoveOutline(string Path, string? ParentPath, int Position = -1) : PdfOutlineEdit("Move bookmark subtree");
public sealed record AddInternalLink(int PageNumber, PdfRect Bounds, int DestinationPage, PdfPoint? DestinationPoint = null)
    : PdfEditOperation(PdfCapability.Annotations, "Add internal page link");
