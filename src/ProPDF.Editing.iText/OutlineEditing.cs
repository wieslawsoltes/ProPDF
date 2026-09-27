using System.Globalization;
using iText.Kernel.Pdf;
using iText.Kernel.Pdf.Annot;
using iText.Kernel.Pdf.Navigation;
using ProPDF.Core;

namespace ProPDF.Editing.iText;

public sealed partial class ITextPdfEditor
{
    private static PdfOutline FindOutline(PdfDocument document, string? path, bool allowRoot = false)
    {
        var current = document.GetOutlines(false);
        if (string.IsNullOrEmpty(path))
            return allowRoot ? current : throw new ArgumentException("A bookmark path is required.", nameof(path));
        if (path.Length > 4096) throw new ArgumentOutOfRangeException(nameof(path));
        var parts = path.Split('/');
        if (parts.Length > 128) throw new ArgumentOutOfRangeException(nameof(path));
        foreach (var part in parts)
        {
            if (!int.TryParse(part, NumberStyles.None, CultureInfo.InvariantCulture, out var index) || index < 0 || index >= current.GetAllChildren().Count)
                throw new KeyNotFoundException($"Bookmark path does not exist: {path}");
            current = current.GetAllChildren()[index];
        }
        return current;
    }

    private static void ApplyOutlineEdit(PdfDocument document, PdfOutlineEdit edit, CancellationToken token)
    {
        // Validate raw outline size/cycles before asking iText to construct its editable outline model.
        ReadOutlineNavigation(document, new PdfNavigationOptions(), token);
        switch (edit)
        {
            case InsertOutline insert:
                ArgumentException.ThrowIfNullOrWhiteSpace(insert.Title);
                if (insert.Title.Length > 16_384) throw new ArgumentOutOfRangeException(nameof(insert.Title));
                var parent = FindOutline(document, insert.ParentPath, true);
                if (insert.Position < -1 || insert.Position > parent.GetAllChildren().Count) throw new ArgumentOutOfRangeException(nameof(insert.Position));
                var added = parent.AddOutline(insert.Title, insert.Position);
                added.AddDestination(PdfExplicitDestination.CreateFit(GetPage(document, insert.PageNumber)));
                added.SetOpen(insert.IsOpen);
                break;
            case UpdateOutline update:
                var entry = FindOutline(document, update.Path);
                if (update.Title is not null)
                {
                    ArgumentException.ThrowIfNullOrWhiteSpace(update.Title);
                    if (update.Title.Length > 16_384) throw new ArgumentOutOfRangeException(nameof(update.Title));
                    entry.SetTitle(update.Title);
                }
                if (update.PageNumber is { } page)
                {
                    entry.GetContent().Remove(PdfName.A);
                    entry.AddDestination(PdfExplicitDestination.CreateFit(GetPage(document, page)));
                }
                if (update.IsOpen is { } open) entry.SetOpen(open);
                break;
            case DeleteOutline delete:
                FindOutline(document, delete.Path).RemoveOutline();
                break;
            case MoveOutline move:
                var source = FindOutline(document, move.Path);
                var destinationParent = FindOutline(document, move.ParentPath, true);
                for (var ancestor = destinationParent; ancestor is not null; ancestor = ancestor.GetParent())
                    if (ReferenceEquals(ancestor, source)) throw new InvalidOperationException("A bookmark cannot be moved into itself or a descendant.");
                var sameParent = ReferenceEquals(source.GetParent(), destinationParent);
                var finalCount = destinationParent.GetAllChildren().Count - (sameParent ? 1 : 0);
                var position = move.Position == -1 ? finalCount : move.Position;
                if (position < 0 || position > finalCount) throw new ArgumentOutOfRangeException(nameof(move.Position));
                var sourceIndex = source.GetParent().GetAllChildren().IndexOf(source);
                var insertion = sameParent && sourceIndex <= position ? position + 1 : position;
                var count = 0;
                CloneOutline(source, destinationParent, insertion, 0, ref count, token);
                source.RemoveOutline();
                break;
            default: throw new NotSupportedException(edit.GetType().Name);
        }
    }

    private static void CloneOutline(PdfOutline source, PdfOutline parent, int position, int depth, ref int count, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (depth > 128 || ++count > 100_000) throw new InvalidDataException("Bookmark subtree exceeds the copy budget.");
        var copy = parent.AddOutline(source.GetTitle(), position);
        if (source.GetDestination() is { } destination) copy.AddDestination(destination);
        foreach (var key in source.GetContent().KeySet())
        {
            if (key.Equals(PdfName.Parent) || key.Equals(PdfName.First) || key.Equals(PdfName.Last) || key.Equals(PdfName.Next) ||
                key.Equals(PdfName.Prev) || key.Equals(PdfName.Count) || key.Equals(PdfName.Title) || key.Equals(PdfName.Dest)) continue;
            copy.GetContent().Put(key, source.GetContent().Get(key));
        }
        foreach (var child in source.GetAllChildren().ToArray()) CloneOutline(child, copy, -1, depth + 1, ref count, token);
        copy.SetOpen(source.IsOpen());
    }

    private static void InsertInternalLink(PdfDocument document, AddInternalLink operation)
    {
        var page = GetPage(document, operation.PageNumber);
        var target = GetPage(document, operation.DestinationPage);
        ValidateBounds(page, operation.Bounds);
        var annotation = new PdfLinkAnnotation(ToRectangle(Transform(page).ToPdf(operation.Bounds)));
        PdfDestination destination = PdfExplicitDestination.CreateFit(target);
        if (operation.DestinationPoint is { } point)
        {
            var transform = Transform(target);
            if (!double.IsFinite(point.X) || !double.IsFinite(point.Y) || point.X < 0 || point.Y < 0 || point.X > transform.ViewSize.Width || point.Y > transform.ViewSize.Height)
                throw new ArgumentOutOfRangeException(nameof(operation.DestinationPoint));
            var raw = transform.ToPdf(point);
            destination = PdfExplicitDestination.CreateXYZ(target, (float)raw.X, (float)raw.Y, 0);
        }
        annotation.SetDestination(destination);
        annotation.SetBorder(new PdfArray(new float[] { 0, 0, 0 }));
        annotation.SetFlags(PdfAnnotation.PRINT);
        annotation.GetPdfObject().Put(PdfName.NM, new PdfString(Guid.NewGuid().ToString("N")));
        page.AddAnnotation(annotation);
    }
}
