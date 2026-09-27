using ProPDF.Kernel;
using static ProPDF.Kernel.PdfValues;
using ProPDF.Core;
using ProPDF.Editing;
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
        var backend = new PdfPigBackend(); var editor = new ManagedPdfEditor(backend);
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
        var backend = new PdfPigBackend(); var editor = new ManagedPdfEditor(backend);
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
        var backend = new PdfPigBackend(); var editor = new ManagedPdfEditor(backend);
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
        var backend = new PdfPigBackend(); var editor = new ManagedPdfEditor(backend);
        using var bytes = new MemoryStream();
        var native = PdfFile.Create();
        var pagesReference = (PdfReference)native.Catalog["Pages"]; var pages = native.Dictionary(pagesReference);
        PdfReference Page() => native.Add(Dictionary(("Type", new PdfName("Page")), ("Parent", pagesReference),
            ("MediaBox", Numbers(0, 0, 595, 842)), ("Resources", new PdfDictionary())));
        var first = Page(); var second = Page(); pages["Kids"] = new PdfArray(first, second); pages["Count"] = new PdfNumber(2L);
        native.Catalog["Names"] = Dictionary(("Dests", Dictionary(("Names", new PdfArray(new PdfString("chapter"), new PdfArray(second, new PdfName("Fit")))))));
        var annotations = new PdfArray(); native.Dictionary(first)["Annots"] = annotations;
        void Link(int y, PdfDictionary action) => annotations.Items.Add(native.Add(Dictionary(("Type", new PdfName("Annot")), ("Subtype", new PdfName("Link")),
            ("Rect", Numbers(10, y, 70, y + 20)), ("A", action))));
        Link(10, Dictionary(("S", new PdfName("GoTo")), ("D", new PdfString("chapter"))));
        Link(40, Dictionary(("S", new PdfName("URI")), ("URI", new PdfString("https://example.org"))));
        Link(70, Dictionary(("S", new PdfName("JavaScript")), ("JS", new PdfString("app.alert('not executed')"))));
        bytes.Write(native.Save());
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
        var backend = new PdfPigBackend(); var editor = new ManagedPdfEditor(backend);
        using var bytes = new MemoryStream();
        var native = PdfFile.Create(); var pagesReference = (PdfReference)native.Catalog["Pages"]; var pages = native.Dictionary(pagesReference);
        var page = native.Add(Dictionary(("Type", new PdfName("Page")), ("Parent", pagesReference), ("MediaBox", Numbers(0, 0, 595, 842))));
        pages["Kids"] = new PdfArray(page); pages["Count"] = new PdfNumber(1L);
        var root = Dictionary(("Type", new PdfName("Outlines"))); var rootReference = native.Add(root);
        var child = Dictionary(("Title", new PdfString("Cycle")), ("Parent", rootReference)); var childReference = native.Add(child);
        child["Next"] = childReference; root["First"] = childReference; root["Last"] = childReference;
        native.Catalog["Outlines"] = rootReference; bytes.Write(native.Save());
        bytes.Position = 0;
        var document = await backend.OpenAsync(bytes);
        await Assert.ThrowsAsync<InvalidDataException>(() => editor.ReadNavigationAsync(document));
    }

    [Fact]
    public async Task NavigationHistoryAndRevisionChecksAreShared()
    {
        var backend = new PdfPigBackend(); var editor = new ManagedPdfEditor(backend);
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
