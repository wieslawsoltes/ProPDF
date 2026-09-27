using System.ComponentModel;
using System.Runtime.CompilerServices;
using ProPDF.Core;

namespace ProPDF.Presentation;

/// <summary>Shared editor-shell view model. Bind and invoke commands on the host UI thread; notifications always use its dispatcher.</summary>
public sealed partial class PdfWorkspace : INotifyPropertyChanged, IDisposable
{
    private readonly PdfEditorContext _context;
    private readonly IPdfWorkspaceDialogs _dialogs;
    private readonly Action<Action> _dispatch;
    private readonly CancellationTokenSource _lifetime = new();
    private readonly List<PdfUiCommand> _commands = [];
    private Guid? _knownRevision;
    private Guid? _inspectionRevision;
    private int _refreshQueued;
    private int _reportedPage;
    private volatile bool _disposed;
    private volatile bool _busy;
    private string _searchQuery = "";
    private string _pageInput = "1";
    private string _fieldValue = "";
    private string _comment = "";
    private string _title = "";
    private string _author = "";
    private string _subject = "";
    private string _keywords = "";
    private PdfPageInfo? _selectedPage;
    private PdfAnnotationInfo? _selectedAnnotation;
    private PdfFormFieldInfo? _selectedField;
    private PdfAttachmentInfo? _selectedAttachment;

    public PdfWorkspace(PdfEditorContext context, IPdfWorkspaceDialogs dialogs, Action<Action>? dispatch = null)
    {
        _context = context ?? throw new ArgumentNullException(nameof(context));
        _dialogs = dialogs ?? throw new ArgumentNullException(nameof(dialogs));
        _dispatch = dispatch ?? (action => action());
        NewCommand = Command(async token => { if (await ConfirmDiscardAsync(token)) await _context.NewAsync(token); }, () => _context.CreateDocument is not null);
        OpenCommand = Command(token => OpenAsync(false, token));
        OpenProtectedCommand = Command(token => OpenAsync(true, token));
        SaveCommand = Command(token => SaveAsync(false, token), HasDocument);
        SaveAsCommand = Command(token => SaveAsync(true, token), HasDocument);
        UndoCommand = Command(async token => { await Session.UndoAsync(token); }, () => Session.CanUndo);
        RedoCommand = Command(async token => { await Session.RedoAsync(token); }, () => Session.CanRedo);
        PreviousPageCommand = Action(() => Viewport.GoToPage(Math.Max(1, Viewport.CurrentPage - 1)), HasDocument);
        NextPageCommand = Action(() => Viewport.GoToPage(Math.Min(Viewport.PageCount, Viewport.CurrentPage + 1)), HasDocument);
        GoToPageCommand = Action(() =>
        {
            if (!int.TryParse(PageInput, out var number) || number < 1 || number > Viewport.PageCount)
                throw new ArgumentException("Enter a valid page number.");
            Viewport.GoToPage(number);
        }, HasDocument);
        ZoomInCommand = Action(() => Viewport.SetZoom(Viewport.Zoom * 1.15));
        ZoomOutCommand = Action(() => Viewport.SetZoom(Viewport.Zoom / 1.15));
        FitWidthCommand = Action(Viewport.FitWidth, HasDocument);
        FitPageCommand = Action(Viewport.FitPage, HasDocument);
        SearchCommand = Command(token => Viewport.SearchAsync(SearchQuery, cancellationToken: token), HasDocument);
        NextMatchCommand = Action(() => Viewport.NextSearchResult(), () => Viewport.SearchHits.Count > 0);
        PreviousMatchCommand = Action(() => Viewport.NextSearchResult(true), () => Viewport.SearchHits.Count > 0);
        CopyCommand = Command(token => _dialogs.CopyTextAsync(Viewport.SelectedText, token), () => Viewport.SelectedText.Length != 0);
        RotateCommand = Edit(() => new RotatePage(Viewport.CurrentPage), PdfCapability.PageOrganization);
        InsertPageCommand = Edit(() => new InsertBlankPage(Viewport.CurrentPage + 1, Document!.GetPage(Viewport.CurrentPage).Size), PdfCapability.PageOrganization);
        DuplicatePageCommand = Edit(() => new InsertDocumentPages(Document!, [Viewport.CurrentPage], Viewport.CurrentPage + 1), PdfCapability.PageOrganization);
        MoveUpCommand = Edit(() => new MovePage(Viewport.CurrentPage, Viewport.CurrentPage - 1), PdfCapability.PageOrganization, () => Viewport.CurrentPage > 1);
        MoveDownCommand = Edit(() => new MovePage(Viewport.CurrentPage, Viewport.CurrentPage + 1), PdfCapability.PageOrganization, () => Viewport.CurrentPage < Viewport.PageCount);
        DeletePageCommand = Command(async token =>
        {
            var revision = Document!.Id;
            var page = Viewport.CurrentPage;
            if (await _dialogs.ConfirmAsync("Delete page", $"Delete page {page}? This can be undone before closing the document.", token))
                await Session.ApplyAsync(new DeletePage(page), revision, token);
        }, () => Can(PdfCapability.PageOrganization) && Viewport.PageCount > 1);
        CropCommand = Command(token =>
        {
            var selection = Viewport.Selection ?? throw new InvalidOperationException("Select a region first.");
            return Session.ApplyAsync(new CropPage(selection.PageNumber, selection.Bounds), selection.Revision, token);
        }, () => Can(PdfCapability.PageOrganization) && HasSelection());
        InsertImageCommand = Command(InsertImageAsync, () => Can(PdfCapability.ContentInsertion) && HasSelection());
        MergeCommand = Command(MergeAsync, () => Can(PdfCapability.PageOrganization) && _context.DocumentLoader is not null);
        ExtractPageCommand = Command(ExtractPageAsync, () => HasDocument() && _context.PageExtractor is not null);
        ApplyRedactionsCommand = Command(async token =>
        {
            if (await _dialogs.ConfirmAsync("Apply content redactions", "Remove page content in every marked region? Original files and undo history remain unredacted. Metadata, attachments and other pages need separate review. This alpha is not a certified sanitization tool.", token))
                await Viewport.ApplyRedactionsAsync(token);
        }, () => Can(PdfCapability.Redaction) && Viewport.PendingRedactions > 0);
        ClearRedactionsCommand = Action(Viewport.ClearRedactions, () => Viewport.PendingRedactions > 0);
        DeleteAnnotationCommand = Edit(() => new DeleteAnnotation(SelectedAnnotation!.PageNumber, SelectedAnnotation.Id), PdfCapability.Annotations, () => InspectionIsCurrent && SelectedAnnotation is not null);
        UpdateCommentCommand = Edit(() => new UpdateAnnotationComment(SelectedAnnotation!.PageNumber, SelectedAnnotation.Id, Comment, SelectedAnnotation.Author), PdfCapability.Annotations,
            () => InspectionIsCurrent && SelectedAnnotation is not null && SelectedAnnotation.Kind != "FreeText");
        SetFieldValueCommand = Edit(() => new SetFormValue(SelectedField!.Name, FieldValue), PdfCapability.Forms,
            () => InspectionIsCurrent && SelectedField is { ReadOnly: false } && SelectedField.Kind != "Sig");
        FlattenFormsCommand = Command(async token =>
        {
            var revision = Document!.Id;
            if (await _dialogs.ConfirmAsync("Flatten forms", "Convert fields to static content? Fields will no longer be editable in the saved PDF.", token))
                await Session.ApplyAsync(new FlattenForms(), revision, token);
        }, () => Can(PdfCapability.Forms) && Fields.Count > 0);
        SaveMetadataCommand = Edit(() => new SetDocumentMetadata(new PdfMetadata(DocumentTitle, DocumentAuthor, DocumentSubject, DocumentKeywords)), PdfCapability.Metadata);
        AddAttachmentCommand = Command(AddAttachmentAsync, () => Can(PdfCapability.Attachments));
        DeleteAttachmentCommand = Edit(() => new RemoveAttachment(SelectedAttachment!.Name), PdfCapability.Attachments,
            () => InspectionIsCurrent && SelectedAttachment is not null);
        SaveAttachmentCommand = Command(SaveAttachmentAsync, () => InspectionIsCurrent && SelectedAttachment is not null && _context.AttachmentReader is not null);
        AddBookmarkCommand = Edit(() => new AddBookmark(ToolText, Viewport.CurrentPage), PdfCapability.Bookmarks);
        RefreshInspectorCommand = Command(token => LoadInspectionAsync(Document!, token), () => HasDocument() && _context.Inspector is not null);
        Viewport.Invalidated += ViewportInvalidated;
        RefreshState();
    }

    public PdfViewportController Viewport => _context.Viewport;
    public PdfSession Session => _context.Session;
    public PdfSnapshot? Document => Session.Current;
    public bool IsBusy => _busy;
    public string DisplayName => (Session.FilePath is { } path ? Path.GetFileName(path) : Document?.Metadata.Title is { Length: > 0 } title ? title : "Untitled") + (Session.IsDirty ? " *" : "");
    public string PageLabel => $"{Viewport.CurrentPage} / {Viewport.PageCount}";
    public string ZoomLabel => $"{Viewport.Zoom:P0}";
    public string MatchLabel => Viewport.SearchHits.Count == 0 ? "No matches" : $"{Viewport.SearchIndex + 1} / {Viewport.SearchHits.Count}";
    public string Status => Viewport.LastError ?? (_busy ? "Working…" : Viewport.PendingRedactions > 0 ? $"{Viewport.PendingRedactions} redaction marks — apply or clear before saving" :
        Viewport.IsRendering ? "Rendering visible tiles…" : Document is null ? "Open a PDF or create a document" : "Ready · Skia rendering · Changes stay local");
    public IReadOnlyList<PdfPageInfo> Pages { get; private set; } = Array.Empty<PdfPageInfo>();
    public IReadOnlyList<PdfAnnotationInfo> Annotations { get; private set; } = Array.Empty<PdfAnnotationInfo>();
    public IReadOnlyList<PdfFormFieldInfo> Fields { get; private set; } = Array.Empty<PdfFormFieldInfo>();
    public IReadOnlyList<PdfAttachmentInfo> Attachments { get; private set; } = Array.Empty<PdfAttachmentInfo>();
    public IReadOnlyList<PdfBookmarkInfo> Bookmarks { get; private set; } = Array.Empty<PdfBookmarkInfo>();
    public string SearchQuery { get => _searchQuery; set => Set(ref _searchQuery, value ?? ""); }
    public string PageInput { get => _pageInput; set => Set(ref _pageInput, value ?? ""); }
    public string ToolText { get => Viewport.ToolText; set { if (Viewport.ToolText != value) { Viewport.ToolText = value ?? ""; Changed(); } } }
    public string FieldValue { get => _fieldValue; set => Set(ref _fieldValue, value ?? ""); }
    public string Comment { get => _comment; set => Set(ref _comment, value ?? ""); }
    public string DocumentTitle { get => _title; set => Set(ref _title, value ?? ""); }
    public string DocumentAuthor { get => _author; set => Set(ref _author, value ?? ""); }
    public string DocumentSubject { get => _subject; set => Set(ref _subject, value ?? ""); }
    public string DocumentKeywords { get => _keywords; set => Set(ref _keywords, value ?? ""); }
    public PdfLayoutMode[] LayoutModes { get; } = Enum.GetValues<PdfLayoutMode>();
    public PdfLayoutMode LayoutMode { get => Viewport.LayoutMode; set { if (Viewport.LayoutMode != value) { Viewport.SetLayoutMode(value); Changed(); } } }
    public IReadOnlyList<PdfToolDescriptor> Tools => AllTools.Where(item => Available(item.Tool)).ToArray();
    public PdfToolDescriptor? SelectedTool
    {
        get => AllTools.FirstOrDefault(item => item.Tool == Viewport.Tool);
        set { if (value is not null && value.Tool != Viewport.Tool && Available(value.Tool)) { Viewport.Tool = value.Tool; Changed(); } }
    }
    public PdfPageInfo? SelectedPage
    {
        get => _selectedPage;
        set { if (Set(ref _selectedPage, value) && value is not null && value.Number != Viewport.CurrentPage) Viewport.GoToPage(value.Number); }
    }
    public PdfAnnotationInfo? SelectedAnnotation
    {
        get => _selectedAnnotation;
        set { if (Set(ref _selectedAnnotation, value)) { Comment = value?.Contents ?? ""; if (value is not null) Viewport.GoToPage(value.PageNumber); RefreshCommands(); } }
    }
    public PdfFormFieldInfo? SelectedField
    {
        get => _selectedField;
        set { if (Set(ref _selectedField, value)) { FieldValue = value?.Value ?? ""; RefreshCommands(); } }
    }
    public PdfAttachmentInfo? SelectedAttachment
    {
        get => _selectedAttachment;
        set { if (Set(ref _selectedAttachment, value)) RefreshCommands(); }
    }
    private bool InspectionIsCurrent => _inspectionRevision == Document?.Id;
    private bool HasDocument() => Document is not null;
    private bool Can(PdfCapability capability) => HasDocument() && Session.Capabilities.Contains(capability);
    private bool HasSelection() => Viewport.Selection is { Bounds.Width: >= 1, Bounds.Height: >= 1 };

    public PdfUiCommand NewCommand { get; }
    public PdfUiCommand OpenCommand { get; }
    public PdfUiCommand OpenProtectedCommand { get; }
    public PdfUiCommand SaveCommand { get; }
    public PdfUiCommand SaveAsCommand { get; }
    public PdfUiCommand UndoCommand { get; }
    public PdfUiCommand RedoCommand { get; }
    public PdfUiCommand PreviousPageCommand { get; }
    public PdfUiCommand NextPageCommand { get; }
    public PdfUiCommand GoToPageCommand { get; }
    public PdfUiCommand ZoomInCommand { get; }
    public PdfUiCommand ZoomOutCommand { get; }
    public PdfUiCommand FitWidthCommand { get; }
    public PdfUiCommand FitPageCommand { get; }
    public PdfUiCommand SearchCommand { get; }
    public PdfUiCommand NextMatchCommand { get; }
    public PdfUiCommand PreviousMatchCommand { get; }
    public PdfUiCommand CopyCommand { get; }
    public PdfUiCommand RotateCommand { get; }
    public PdfUiCommand InsertPageCommand { get; }
    public PdfUiCommand DuplicatePageCommand { get; }
    public PdfUiCommand MoveUpCommand { get; }
    public PdfUiCommand MoveDownCommand { get; }
    public PdfUiCommand DeletePageCommand { get; }
    public PdfUiCommand CropCommand { get; }
    public PdfUiCommand InsertImageCommand { get; }
    public PdfUiCommand MergeCommand { get; }
    public PdfUiCommand ExtractPageCommand { get; }
    public PdfUiCommand ApplyRedactionsCommand { get; }
    public PdfUiCommand ClearRedactionsCommand { get; }
    public PdfUiCommand DeleteAnnotationCommand { get; }
    public PdfUiCommand UpdateCommentCommand { get; }
    public PdfUiCommand SetFieldValueCommand { get; }
    public PdfUiCommand FlattenFormsCommand { get; }
    public PdfUiCommand SaveMetadataCommand { get; }
    public PdfUiCommand AddAttachmentCommand { get; }
    public PdfUiCommand DeleteAttachmentCommand { get; }
    public PdfUiCommand SaveAttachmentCommand { get; }
    public PdfUiCommand AddBookmarkCommand { get; }
    public PdfUiCommand RefreshInspectorCommand { get; }
    public event PropertyChangedEventHandler? PropertyChanged;

    private PdfUiCommand Command(Func<CancellationToken, Task> action, Func<bool>? canExecute = null)
    {
        var command = new PdfUiCommand(() => RunAsync(action), () => !_disposed && !_busy && (canExecute?.Invoke() ?? true));
        _commands.Add(command);
        return command;
    }
    private PdfUiCommand Action(Action action, Func<bool>? canExecute = null) => Command(_ => { action(); return Task.CompletedTask; }, canExecute);
    private PdfUiCommand Edit(Func<IPdfEditOperation> operation, PdfCapability capability, Func<bool>? condition = null) =>
        Command(token => Session.ApplyAsync(operation(), Document!.Id, token), () => Can(capability) && (condition?.Invoke() ?? true));
    private async Task RunAsync(Func<CancellationToken, Task> action)
    {
        if (_disposed || _busy) return;
        _busy = true;
        var token = _lifetime.Token;
        Changed(null);
        RefreshCommands();
        try { await action(token); }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        catch (Exception error) { if (!_disposed) Viewport.ReportError(error); }
        finally
        {
            _busy = false;
            if (!_disposed) { Changed(null); RefreshCommands(); }
        }
    }

    public async Task<bool> ConfirmDiscardAsync(CancellationToken cancellationToken = default) =>
        (!Session.IsDirty && Viewport.PendingRedactions == 0) || await _dialogs.ConfirmAsync("Unsaved work",
            "Discard unsaved edits and unapplied redaction marks? Save first to retain edits.", cancellationToken);

    private async Task OpenAsync(bool protectedDocument, CancellationToken token)
    {
        if (!await ConfirmDiscardAsync(token)) return;
        var path = await _dialogs.PickOpenPathAsync(PdfFileKind.Pdf, token);
        if (path is null) return;
        var password = protectedDocument ? await _dialogs.RequestPasswordAsync(token) : null;
        if (protectedDocument && password is null) return;
        await _context.OpenPathAsync(path, password, token);
    }
    private async Task SaveAsync(bool saveAs, CancellationToken token)
    {
        if (Viewport.PendingRedactions != 0) throw new InvalidOperationException("Apply or clear redaction marks before saving. Marks alone do not redact a PDF.");
        var path = !saveAs ? Session.FilePath : null;
        path ??= await _dialogs.PickSavePathAsync(Session.FilePath is { } existing ? Path.GetFileName(existing) : "document.pdf", token);
        if (Viewport.PendingRedactions != 0) throw new InvalidOperationException("Redaction marks were added while the save dialog was open. Apply or clear them first.");
        if (path is not null) await Session.SaveAsAsync(path, token);
    }
    private async Task InsertImageAsync(CancellationToken token)
    {
        var selection = Viewport.Selection ?? throw new InvalidOperationException("Select the destination region first.");
        var path = await _dialogs.PickOpenPathAsync(PdfFileKind.Image, token);
        if (path is null) return;
        await using var input = File.OpenRead(path);
        var bytes = await PdfStreams.ReadBoundedAsync(input, 32L * 1024 * 1024, token);
        await Session.ApplyAsync(new AddImage(selection.PageNumber, selection.Bounds, new PdfBinaryAsset(bytes)), selection.Revision, token);
    }
    private async Task MergeAsync(CancellationToken token)
    {
        var revision = Document!.Id;
        var before = Viewport.CurrentPage + 1;
        var path = await _dialogs.PickOpenPathAsync(PdfFileKind.Pdf, token);
        if (path is null) return;
        await using var input = File.OpenRead(path);
        var other = await _context.DocumentLoader!.OpenAsync(input, cancellationToken: token);
        await Session.ApplyAsync(new InsertDocumentPages(other, Enumerable.Range(1, other.Pages.Count), before), revision, token);
    }
    private async Task ExtractPageAsync(CancellationToken token)
    {
        var document = Document!;
        var page = Viewport.CurrentPage;
        var path = await _dialogs.PickSavePathAsync($"page-{page}.pdf", token);
        if (path is null) return;
        if (!await _dialogs.ConfirmAsync("Export page", "The extracted document is unsigned and unencrypted. Continue?", token)) return;
        var extracted = await _context.PageExtractor!.ExtractPagesAsync(document, [page], token);
        await PdfStreams.SaveAtomicAsync(extracted, path, token);
    }
    private async Task AddAttachmentAsync(CancellationToken token)
    {
        var revision = Document!.Id;
        var path = await _dialogs.PickOpenPathAsync(PdfFileKind.Attachment, token);
        if (path is null) return;
        await using var input = File.OpenRead(path);
        var bytes = await PdfStreams.ReadBoundedAsync(input, 128L * 1024 * 1024, token);
        await Session.ApplyAsync(new AddAttachment(Path.GetFileName(path), new PdfBinaryAsset(bytes)), revision, token);
    }
    private async Task SaveAttachmentAsync(CancellationToken token)
    {
        var document = Document!;
        var attachment = SelectedAttachment!;
        var suggested = Path.GetFileName(attachment.FileName.Replace('\\', '/'));
        var path = await _dialogs.PickSavePathAsync(string.IsNullOrWhiteSpace(suggested) ? "attachment.bin" : suggested, token);
        if (path is null) return;
        var asset = await _context.AttachmentReader!.ReadAttachmentAsync(document, attachment.Name, token);
        var target = Path.GetFullPath(path);
        var temporary = Path.Combine(Path.GetDirectoryName(target)!, $".propdf-{Guid.NewGuid():N}.tmp");
        try
        {
            await using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, FileOptions.Asynchronous | FileOptions.WriteThrough))
            {
                await stream.WriteAsync(asset.ToArray(), token);
                await stream.FlushAsync(token);
                stream.Flush(true);
            }
            token.ThrowIfCancellationRequested();
            File.Move(temporary, target, overwrite: true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }

    private void ViewportInvalidated(object? sender, EventArgs args)
    {
        if (Interlocked.Exchange(ref _refreshQueued, 1) != 0) return;
        _dispatch(() => { Interlocked.Exchange(ref _refreshQueued, 0); if (!_disposed) RefreshState(); });
    }
    private void RefreshState()
    {
        var document = Document;
        if (_knownRevision != document?.Id)
        {
            _knownRevision = document?.Id;
            Pages = document?.Pages ?? Array.Empty<PdfPageInfo>();
            _title = document?.Metadata.Title ?? "";
            _author = document?.Metadata.Author ?? "";
            _subject = document?.Metadata.Subject ?? "";
            _keywords = document?.Metadata.Keywords ?? "";
            _selectedAnnotation = null;
            _selectedField = null;
            _selectedAttachment = null;
            Annotations = Array.Empty<PdfAnnotationInfo>();
            Fields = Array.Empty<PdfFormFieldInfo>();
            Attachments = Array.Empty<PdfAttachmentInfo>();
            Bookmarks = Array.Empty<PdfBookmarkInfo>();
            if (document is not null && _context.Inspector is not null) _ = LoadInspectionAsync(document, _lifetime.Token);
        }
        RefreshContentState();
        var page = Math.Clamp(Viewport.CurrentPage, 1, Math.Max(1, document?.Pages.Count ?? 1));
        _selectedPage = document?.GetPage(page);
        // Progressive tile updates must not overwrite a page number the user is currently typing.
        if (_reportedPage != page)
        {
            _reportedPage = page;
            _pageInput = page.ToString(System.Globalization.CultureInfo.InvariantCulture);
        }
        Changed(null);
        RefreshCommands();
    }
    private async Task LoadInspectionAsync(PdfSnapshot document, CancellationToken token)
    {
        if (_context.Inspector is null) return;
        try
        {
            var inspection = await _context.Inspector.InspectAsync(document, token);
            _dispatch(() =>
            {
                if (_disposed || Document?.Id != document.Id) return;
                Annotations = inspection.Annotations.Where(annotation => annotation.Kind != "Widget").ToArray();
                Fields = inspection.Fields;
                Attachments = inspection.Attachments;
                Bookmarks = inspection.Bookmarks;
                _inspectionRevision = document.Id;
                Changed(null);
                RefreshCommands();
            });
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        catch (Exception error) { if (!_disposed) _dispatch(() => { if (!_disposed) Viewport.ReportError(error); }); }
    }
    private bool Available(PdfTool tool) => tool switch
    {
        PdfTool.Pan or PdfTool.SelectRegion or PdfTool.SelectText => true,
        PdfTool.EditObject => _context.Inspector is IPdfContentService,
        PdfTool.Highlight or PdfTool.Note or PdfTool.FreeText or PdfTool.Ink => Session.Capabilities.Contains(PdfCapability.Annotations),
        PdfTool.Redact => Session.Capabilities.Contains(PdfCapability.Redaction),
        PdfTool.ReplaceText => Session.Capabilities.Contains(PdfCapability.ContentReplacement),
        PdfTool.TextField or PdfTool.CheckBox => Session.Capabilities.Contains(PdfCapability.Forms),
        _ => Session.Capabilities.Contains(PdfCapability.ContentInsertion)
    };
    private static readonly PdfToolDescriptor[] AllTools =
    [
        new(PdfTool.EditObject, "Edit existing objects"), new(PdfTool.TextBox, "Wrapped text box"),
        new(PdfTool.Pan, "Hand / pan"), new(PdfTool.SelectText, "Select text"), new(PdfTool.SelectRegion, "Select region"),
        new(PdfTool.Highlight, "Highlight"), new(PdfTool.Note, "Sticky note"), new(PdfTool.FreeText, "Text comment"),
        new(PdfTool.Text, "Insert text"), new(PdfTool.Rectangle, "Rectangle"), new(PdfTool.Ellipse, "Ellipse"),
        new(PdfTool.Ink, "Ink"), new(PdfTool.Redact, "Mark redaction"), new(PdfTool.ReplaceText, "Replace region text"),
        new(PdfTool.TextField, "Text form field"), new(PdfTool.CheckBox, "Checkbox field")
    ];
    private bool Set<T>(ref T field, T value, [CallerMemberName] string? property = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return false;
        field = value;
        Changed(property);
        return true;
    }
    private void Changed([CallerMemberName] string? property = null) => _dispatch(() =>
    {
        if (!_disposed) PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(property));
    });
    private void RefreshCommands() => _dispatch(() =>
    {
        // WPF ButtonBase subscribes directly: raising this event on a pool continuation violates UI affinity.
        foreach (var command in _commands) command.Refresh();
    });
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        Viewport.Invalidated -= ViewportInvalidated;
        _lifetime.Cancel();
        _lifetime.Dispose();
        RefreshCommands();
    }
}
