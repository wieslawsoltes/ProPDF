using System.Windows.Input;
using Avalonia.Interactivity;
using ProPDF.Core;

namespace ProPDF.Avalonia;

public enum PdfViewerZoomMode { Custom, FitWidth, FitPage }
public enum PdfViewerSidebarSection { Thumbnails, Outline, Search, Comments, Forms, Attachments, Information, Export }

/// <summary>Host-specific print handoff. Successful completion is not proof that a printer produced output.</summary>
public interface IPdfViewerPrintService
{
    Task PrintAsync(PdfSnapshot document, CancellationToken cancellationToken = default);
}

public sealed class PdfViewerPageChangedEventArgs(RoutedEvent routedEvent, int previousPage, int pageNumber) : RoutedEventArgs(routedEvent)
{
    public int PreviousPage { get; } = previousPage;
    public int PageNumber { get; } = pageNumber;
}

public sealed class PdfViewerErrorEventArgs(RoutedEvent routedEvent, string message) : RoutedEventArgs(routedEvent)
{
    public string Message { get; } = message;
}

/// <summary>Awaitable UI command with a single-invocation gate and an explicit error sink.</summary>
public sealed class PdfViewerCommand(Func<object?, Task> execute, Func<object?, bool> canExecute, Action<Exception> reportError) : ICommand
{
    private bool _executing, _pending;
    public event EventHandler? CanExecuteChanged;
    // Synchronous navigation must not disable the focused toolbar button. A disabled
    // button loses keyboard focus, breaking the next Find/zoom/navigation shortcut.
    // Reentrancy is still rejected independently at the ExecuteAsync boundary.
    public bool CanExecute(object? parameter) => !_pending && canExecute(parameter);
    public async Task ExecuteAsync(object? parameter = null)
    {
        if (_executing || !CanExecute(parameter)) return;
        _executing = true;
        try
        {
            var operation = execute(parameter);
            if (!operation.IsCompleted) { _pending = true; Refresh(); }
            await operation;
        }
        catch (OperationCanceledException) { }
        catch (Exception error) { reportError(error); }
        finally { _pending = false; _executing = false; Refresh(); }
    }
    public async void Execute(object? parameter) => await ExecuteAsync(parameter);
    internal void Refresh() => CanExecuteChanged?.Invoke(this, EventArgs.Empty);
}
