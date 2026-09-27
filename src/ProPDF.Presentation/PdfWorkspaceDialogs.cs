using System.Windows.Input;

namespace ProPDF.Presentation;

public enum PdfFileKind { Pdf, Image, Attachment }

/// <summary>Only native dialogs and clipboard access vary between the desktop frameworks.</summary>
public interface IPdfWorkspaceDialogs
{
    Task<string?> PickOpenPathAsync(PdfFileKind kind, CancellationToken cancellationToken);
    Task<string?> PickSavePathAsync(string suggestedName, CancellationToken cancellationToken);
    Task<string?> RequestPasswordAsync(CancellationToken cancellationToken);
    Task<bool> ConfirmAsync(string title, string message, CancellationToken cancellationToken);
    Task CopyTextAsync(string text, CancellationToken cancellationToken);
}

public sealed class PdfUiCommand(Func<Task> execute, Func<bool> canExecute) : ICommand
{
    public event EventHandler? CanExecuteChanged;
    public bool CanExecute(object? parameter) => canExecute();
    public Task ExecuteAsync() => CanExecute(null) ? execute() : Task.CompletedTask;
    public async void Execute(object? parameter) => await ExecuteAsync();
    internal void Refresh() => CanExecuteChanged?.Invoke(this, EventArgs.Empty);
}

public sealed record PdfToolDescriptor(PdfTool Tool, string Title);

/// <summary>Optional file-provider handoff. Desktop path-only hosts do not need to implement it.</summary>
public interface IPdfWorkspaceFileTransfer
{
    /// <summary>Imported staging paths must never become implicit save destinations.</summary>
    bool AlwaysPickSaveDestination { get; }
    /// <summary>Complete the user-selected export. Throw on failure; do not claim a temporary write was a save.</summary>
    Task PublishFileAsync(string stagedPath, CancellationToken cancellationToken);
}
