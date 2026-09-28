using System.ComponentModel;
using System.Windows.Input;

namespace ProPDF.Presentation;

public enum PdfShellSection { AllTools, Edit, Review, Forms, Navigate, Export, Document, Organize, Redact, File }
public sealed record PdfShellCategory(PdfShellSection Section, string Title, string Description, string Icon, uint Accent);

/// <summary>UI-thread-owned document workspace navigation. Independent of PDF parsing and any UI framework.</summary>
public sealed class PdfShellState : INotifyPropertyChanged
{
    private double _width = 1450;
    private bool _toolsOpen = true, _toolsExplicit, _pagesOpen, _lastWasPages, _searchOpen;
    private PdfShellSection _section;
    public event PropertyChangedEventHandler? PropertyChanged;
    public PdfShellState()
    {
        ShowSectionCommand = new PdfShellCommand(value => ShowSection(Parse(value)), value => TryParse(value, out _));
        CloseToolsCommand = Action(CloseTools);
        TogglePagesCommand = Action(TogglePages);
        ToggleSearchCommand = Action(() => SetSearchOpen(!IsSearchOpen));
    }
    public static IReadOnlyList<PdfShellCategory> Categories { get; } = Array.AsReadOnly(new[]
    {
        new PdfShellCategory(PdfShellSection.Edit, "Edit a PDF", "Text, images and page objects", PdfShellIcons.Edit, 0x7A3ED1),
        new PdfShellCategory(PdfShellSection.Export, "Export a PDF", "Images, text and page ranges", PdfShellIcons.Export, 0x1678CB),
        new PdfShellCategory(PdfShellSection.Organize, "Organize pages", "Insert, rotate, reorder and combine", PdfShellIcons.Pages, 0x7046C7),
        new PdfShellCategory(PdfShellSection.Review, "Add comments", "Highlight, annotate and review", PdfShellIcons.Comment, 0xC17A00),
        new PdfShellCategory(PdfShellSection.Forms, "Prepare a form", "Create, fill and flatten fields", PdfShellIcons.Form, 0x2A865C),
        new PdfShellCategory(PdfShellSection.Redact, "Redact a PDF", "Mark and remove sensitive content", PdfShellIcons.Redact, 0xC43E49),
        new PdfShellCategory(PdfShellSection.Navigate, "Bookmarks & links", "Navigate and author destinations", PdfShellIcons.Bookmark, 0x1678CB),
        new PdfShellCategory(PdfShellSection.Document, "Document properties", "Metadata and file attachments", PdfShellIcons.Info, 0x59636E)
    });
    public PdfShellSection Section => _section;
    public int InspectorIndex => (int)_section >= 1 && (int)_section <= 8 ? (int)_section - 1 : 0;
    public string Title => _section switch
    {
        PdfShellSection.AllTools => "All tools", PdfShellSection.File => "File & workspace",
        _ => Categories.First(item => item.Section == _section).Title
    };
    public bool IsAllTools => _section == PdfShellSection.AllTools;
    public bool IsFile => _section == PdfShellSection.File;
    public bool IsInspector => !IsAllTools && !IsFile;
    public bool IsSearchOpen => _searchOpen;
    public bool IsCompact => _width < 980;
    public bool IsSmall => _width < 600;
    public double PanelWidth => Math.Min(300, Math.Max(180, _width - 60));
    public bool ToolsVisible => _toolsOpen && (!IsCompact || _toolsExplicit) && (!IsCompact || !_pagesOpen || !_lastWasPages);
    public bool PagesVisible => _pagesOpen && (!IsCompact || !_toolsOpen || !_toolsExplicit || _lastWasPages);
    public ICommand ShowSectionCommand { get; }
    public ICommand CloseToolsCommand { get; }
    public ICommand TogglePagesCommand { get; }
    public ICommand ToggleSearchCommand { get; }
    public void SetAvailableWidth(double width)
    {
        if (!double.IsFinite(width) || width <= 0) throw new ArgumentOutOfRangeException(nameof(width));
        if (_width == width) return;
        _width = width; Notify();
    }
    public void ShowSection(PdfShellSection section)
    {
        if (!Enum.IsDefined(section)) throw new ArgumentOutOfRangeException(nameof(section));
        if (_section == section && ToolsVisible) return;
        _section = section; _toolsOpen = true; _toolsExplicit = true; _lastWasPages = false; Notify();
    }
    public void CloseTools() { if (!_toolsOpen) return; _toolsOpen = false; _toolsExplicit = true; Notify(); }
    public void TogglePages() { _pagesOpen = !PagesVisible; if (_pagesOpen) _lastWasPages = true; Notify(); }
    public void SetSearchOpen(bool open) { if (_searchOpen == open) return; _searchOpen = open; Notify(); }
    /// <summary>Dismiss search first, then the front compact drawer. Does not mutate the document or active PDF tool.</summary>
    public bool Dismiss()
    {
        if (_searchOpen) { SetSearchOpen(false); return true; }
        if (IsCompact && PagesVisible) { _pagesOpen = false; Notify(); return true; }
        if (IsCompact && ToolsVisible) { CloseTools(); return true; }
        return false;
    }
    private static bool TryParse(object? value, out PdfShellSection section) => Enum.TryParse(value?.ToString(), out section) && Enum.IsDefined(section);
    private static PdfShellSection Parse(object? value) => TryParse(value, out var section) ? section : throw new ArgumentException("Unknown workspace section.", nameof(value));
    private static PdfShellCommand Action(Action action) => new(_ => action());
    private void Notify() => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(null));
}

/// <summary>Parameter-aware UI navigation command; unlike document commands this does not perform asynchronous edits.</summary>
public sealed class PdfShellCommand(Action<object?> execute, Func<object?, bool>? canExecute = null) : ICommand
{
    public event EventHandler? CanExecuteChanged;
    public bool CanExecute(object? parameter) => canExecute?.Invoke(parameter) ?? true;
    public void Execute(object? parameter) { if (CanExecute(parameter)) execute(parameter); }
    internal void Refresh() => CanExecuteChanged?.Invoke(this, EventArgs.Empty);
}

public sealed partial class PdfWorkspace
{
    public PdfShellState Shell { get; } = new();
    private PdfShellCommand? _activateTool;
    public ICommand ActivateToolCommand => _activateTool ??= new PdfShellCommand(value =>
    {
        var tool = Enum.Parse<PdfTool>(value!.ToString()!);
        SelectedTool = Tools.First(item => item.Tool == tool);
    }, value => Enum.TryParse<PdfTool>(value?.ToString(), out var tool) && Tools.Any(item => item.Tool == tool) && !_disposed && Document is not null && !IsBusy);
}
