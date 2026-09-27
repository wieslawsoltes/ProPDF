using iText.Kernel.Geom;
using iText.Kernel.Pdf;
using iText.Kernel.Pdf.Action;
using iText.Kernel.Pdf.Annot;
using iText.Kernel.Pdf.Navigation;
using ProPDF.Core;
using ProPDF.Editing.iText;
using ProPDF.Engine.PdfPig;
using ProPDF.Presentation;
using ProPDF.Rendering.Skia;
using Xunit;

namespace ProPDF.Tests;

public sealed class NavigationTests
{
    [Theory]
    [InlineData("https://example.org/document", true)]
    [InlineData("mailto:review@example.org", true)]
    [InlineData("http://example.org", true)]
    [InlineData("javascript:alert(1)", false)]
    [InlineData("file:///private/document", false)]
    [InlineData("https://user:secret@example.org", false)]
    [InlineData("https://example.org\nInjected", false)]
    [InlineData("data:text/html,hello", false)]
    public void UriPolicyIsExplicit(string uri, bool allowed) => Assert.Equal(allowed, PdfUriPolicy.TryNormalize(uri, out _));

    [Fact]
    public async Task HierarchicalBookmarksCanBeRenamedMovedAndDeleted()
    {
        var backend = new PdfPigBackend(); var editor = new ITextPdfEditor(backend);
        var original = await editor.CreateAsync(3);
        var document = await editor.ApplyAsync(original, new IPdfEditOperation[]
        {
            new InsertOutline("Parent", 1), new InsertOutline("Child", 2, "0"),
            new InsertOutline("Grandchild", 3, "0/0"), new InsertOutline("Other", 3),
            new UpdateOutline("0/0", "Renamed child"), new MoveOutline("0/0", null, 1)
        });
        var bookmarks = (await editor.ReadNavigationAsync(document)).Bookmarks;
        Assert.Equal(new[] { "Parent", "Renamed child", "Grandchild", "Other" }, bookmarks.Select(item => item.Title));
        Assert.Equal(new[] { "0", "1", "1/0", "2" }, bookmarks.Select(item => item.Path));
        Assert.Equal(2, bookmarks[1].Target.PageNumber);
        Assert.Equal(3, bookmarks[2].Target.PageNumber);
        document = await editor.ApplyAsync(document, new IPdfEditOperation[] { new DeleteOutline("1") });
        Assert.Equal(new[] { "Parent", "Other" }, (await editor.ReadNavigationAsync(document)).Bookmarks.Select(item => item.Title));
        Assert.Empty((await editor.ReadNavigationAsync(original)).Bookmarks);
    }

    [Fact]
    public async Task SameParentMovesUseFinalSiblingPositionAndCyclesFail()
    {
        var backend = new PdfPigBackend(); var editor = new ITextPdfEditor(backend);
        var document = await editor.CreateAsync();
        document = await editor.ApplyAsync(document, new IPdfEditOperation[]
        { new InsertOutline("A", 1), new InsertOutline("B", 1), new InsertOutline("C", 1), new MoveOutline("0", null, 2) });
        Assert.Equal(new[] { "B", "C", "A" }, (await editor.ReadNavigationAsync(document)).Bookmarks.Select(item => item.Title));
        document = await editor.ApplyAsync(document, new IPdfEditOperation[] { new InsertOutline("Child", 1, "0") });
        await Assert.ThrowsAsync<InvalidOperationException>(() => editor.ApplyAsync(document, new IPdfEditOperation[] { new MoveOutline("0", "0/0") }));
        await Assert.ThrowsAsync<KeyNotFoundException>(() => editor.ApplyAsync(document, new IPdfEditOperation[] { new DeleteOutline("999") }));
    }

    [Theory]
    [InlineData(0)] [InlineData(90)] [InlineData(180)] [InlineData(270)]
    public async Task InternalLinkCoordinatesRoundTripAcrossPageRotation(int rotation)
    {
        var backend = new PdfPigBackend(); var editor = new ITextPdfEditor(backend);
        var document = await editor.CreateAsync(2, new PdfSize(200, 300));
        document = await editor.ApplyAsync(document, new IPdfEditOperation[]
        { new RotatePage(2, rotation), new AddInternalLink(1, new PdfRect(20, 30, 70, 25), 2, new PdfPoint(40, 50)) });
        var link = Assert.Single((await editor.ReadNavigationAsync(document)).Links);
        Assert.Equal(20, link.Bounds.X, 3); Assert.Equal(30, link.Bounds.Y, 3);
        Assert.Equal(2, link.Target.PageNumber);
        Assert.Equal(40, link.Target.X!.Value, 3); Assert.Equal(50, link.Target.Y!.Value, 3);
        Assert.Null(link.Target.UnsupportedReason);
    }

    [Fact]
    public async Task NamedDestinationsResolveAndUnsafeActionsRemainInert()
    {
        var backend = new PdfPigBackend(); var editor = new ITextPdfEditor(backend);
        using var bytes = new MemoryStream();
        using (var writer = new PdfWriter(bytes))
        {
            writer.SetCloseStream(false);
            using var native = new PdfDocument(writer);
            var first = native.AddNewPage(); var second = native.AddNewPage();
            native.AddNamedDestination("chapter", PdfExplicitDestination.CreateFit(second).GetPdfObject());
            first.AddAnnotation(new PdfLinkAnnotation(new Rectangle(10, 10, 60, 20)).SetAction(PdfAction.CreateGoTo("chapter")));
            first.AddAnnotation(new PdfLinkAnnotation(new Rectangle(10, 40, 60, 20)).SetAction(PdfAction.CreateURI("https://example.org")));
            var dangerous = new PdfLinkAnnotation(new Rectangle(10, 70, 60, 20));
            var action = new PdfDictionary(); action.Put(PdfName.S, PdfName.JavaScript); action.Put(PdfName.JS, new PdfString("app.alert('not executed')"));
            dangerous.GetPdfObject().Put(PdfName.A, action); first.AddAnnotation(dangerous);
        }
        bytes.Position = 0;
        var document = await backend.OpenAsync(bytes);
        var links = (await editor.ReadNavigationAsync(document)).Links;
        Assert.Equal(3, links.Count);
        Assert.Equal(2, links[0].Target.PageNumber);
        Assert.Equal("https://example.org/", links[1].Target.ExternalUri);
        Assert.False(links[2].Target.IsSupported);
        Assert.Contains("disabled", links[2].Target.UnsupportedReason!);
        await Assert.ThrowsAsync<InvalidDataException>(() => editor.ReadNavigationAsync(document, new PdfNavigationOptions(MaximumEntries: 1)));
    }

    [Fact]
    public async Task OutlineCycleIsRejectedWithoutRecursing()
    {
        var backend = new PdfPigBackend(); var editor = new ITextPdfEditor(backend);
        using var bytes = new MemoryStream();
        using (var writer = new PdfWriter(bytes))
        {
            writer.SetCloseStream(false);
            using var native = new PdfDocument(writer); native.AddNewPage();
            var root = new PdfDictionary(); root.MakeIndirect(native); root.Put(PdfName.Type, PdfName.Outlines);
            var child = new PdfDictionary(); child.MakeIndirect(native); child.Put(PdfName.Title, new PdfString("Cycle"));
            child.Put(PdfName.Parent, root); child.Put(PdfName.Next, child);
            root.Put(PdfName.First, child); root.Put(PdfName.Last, child);
            native.GetCatalog().GetPdfObject().Put(PdfName.Outlines, root);
        }
        bytes.Position = 0;
        var document = await backend.OpenAsync(bytes);
        await Assert.ThrowsAsync<InvalidDataException>(() => editor.ReadNavigationAsync(document));
    }

    [Fact]
    public async Task NavigationHistoryAndRevisionChecksAreShared()
    {
        var backend = new PdfPigBackend(); var editor = new ITextPdfEditor(backend);
        var document = await editor.CreateAsync(3);
        var session = new PdfSession(backend, editor);
        using (var input = document.OpenRead()) await session.OpenAsync(input);
        await using var renderer = new SkiaPdfRenderer(backend);
        await using var viewport = new PdfViewportController(session, renderer, backend);
        viewport.SetViewport(600, 500); viewport.SetZoom(1.5);
        var before = viewport.Offset; var revision = session.Current!.Id;
        viewport.NavigateTo(new PdfNavigationTarget(2), revision);
        Assert.Equal(2, viewport.CurrentPage); Assert.True(viewport.CanNavigateBack);
        viewport.NavigateBack(); Assert.Equal(1, viewport.CurrentPage); Assert.Equal(1.5, viewport.Zoom); Assert.Equal(before, viewport.Offset);
        viewport.NavigateForward(); Assert.Equal(2, viewport.CurrentPage);
        viewport.NavigateTo(new PdfNavigationTarget(NamedAction: "NextPage"), revision); Assert.Equal(3, viewport.CurrentPage);
        await session.ApplyAsync(new RotatePage(1));
        Assert.False(viewport.CanNavigateBack);
        Assert.Throws<PdfRevisionConflictException>(() => viewport.NavigateTo(new PdfNavigationTarget(1), revision));
        Assert.Throws<NotSupportedException>(() => viewport.NavigateTo(new PdfNavigationTarget(ExternalUri: "https://example.org"), session.Current!.Id));
    }
}
