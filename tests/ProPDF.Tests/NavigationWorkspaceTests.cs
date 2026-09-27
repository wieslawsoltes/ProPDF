using ProPDF.Core;
using ProPDF.Editing;
using ProPDF.Engine.PdfPig;
using ProPDF.Presentation;
using ProPDF.Rendering.Skia;
using Xunit;

namespace ProPDF.Tests;

public sealed class NavigationWorkspaceTests
{
    [Fact]
    public async Task ExternalLinksRequireAConfirmedExplicitCommand()
    {
        var backend = new PdfPigBackend(); var editor = new ManagedPdfEditor(backend);
        var document = await editor.CreateAsync();
        document = await editor.ApplyAsync(document, new IPdfEditOperation[]
        { new AddAnnotation(1, new PdfRect(20, 30, 100, 20), PdfAnnotationKind.Link, Uri: "https://example.org/review") });
        var session = new PdfSession(backend, editor);
        using (var input = document.OpenRead()) await session.OpenAsync(input);
        await using var renderer = new SkiaPdfRenderer(backend);
        await using var viewport = new PdfViewportController(session, renderer, backend);
        var dialogs = new FakeDialogs();
        using var workspace = new PdfWorkspace(new PdfEditorContext(viewport, editor), dialogs);
        await workspace.LoadNavigationAsync();
        workspace.SelectedLink = Assert.Single(workspace.NavigationLinks);
        Assert.Equal(0, dialogs.Confirmations);
        Assert.Null(dialogs.Opened);
        await workspace.FollowLinkCommand.ExecuteAsync();
        Assert.Equal(1, dialogs.Confirmations);
        Assert.Null(dialogs.Opened);
        dialogs.Allow = true;
        await workspace.FollowLinkCommand.ExecuteAsync();
        Assert.Equal(2, dialogs.Confirmations);
        Assert.Equal("https://example.org/review", dialogs.Opened?.AbsoluteUri);
        await workspace.CopyLinkCommand.ExecuteAsync();
        Assert.Equal("https://example.org/review", dialogs.Copied);
    }

    [Fact]
    public async Task NavigationSelectionIsInvalidatedWhenDocumentChanges()
    {
        var backend = new PdfPigBackend(); var editor = new ManagedPdfEditor(backend);
        var document = await editor.CreateAsync(2);
        document = await editor.ApplyAsync(document, new IPdfEditOperation[] { new InsertOutline("Chapter", 2) });
        var session = new PdfSession(backend, editor);
        using (var input = document.OpenRead()) await session.OpenAsync(input);
        await using var renderer = new SkiaPdfRenderer(backend);
        await using var viewport = new PdfViewportController(session, renderer, backend);
        using var workspace = new PdfWorkspace(new PdfEditorContext(viewport, editor), new FakeDialogs());
        await workspace.LoadNavigationAsync();
        workspace.SelectedBookmark = Assert.Single(workspace.NavigationBookmarks);
        Assert.True(workspace.FollowBookmarkCommand.CanExecute(null));
        await session.ApplyAsync(new RotatePage(1));
        Assert.False(workspace.FollowBookmarkCommand.CanExecute(null));
        await workspace.LoadNavigationAsync();
        Assert.Null(workspace.SelectedBookmark);
        Assert.Single(workspace.NavigationBookmarks);
    }

    private sealed class FakeDialogs : IPdfWorkspaceDialogs, IPdfExternalNavigation
    {
        public bool Allow;
        public int Confirmations;
        public Uri? Opened;
        public string? Copied;
        public Task<string?> PickOpenPathAsync(PdfFileKind kind, CancellationToken cancellationToken) => Task.FromResult<string?>(null);
        public Task<string?> PickSavePathAsync(string suggestedName, CancellationToken cancellationToken) => Task.FromResult<string?>(null);
        public Task<string?> RequestPasswordAsync(CancellationToken cancellationToken) => Task.FromResult<string?>(null);
        public Task<bool> ConfirmAsync(string title, string message, CancellationToken cancellationToken)
        { Confirmations++; return Task.FromResult(Allow); }
        public Task CopyTextAsync(string text, CancellationToken cancellationToken) { Copied = text; return Task.CompletedTask; }
        public Task OpenUriAsync(Uri uri, CancellationToken cancellationToken) { Opened = uri; return Task.CompletedTask; }
    }
}
