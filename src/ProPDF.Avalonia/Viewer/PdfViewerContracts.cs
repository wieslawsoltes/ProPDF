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
    private bool _executing;
    public event EventHandler? CanExecuteChanged;
    public bool CanExecute(object? parameter) => !_executing && canExecute(parameter);
    public async Task ExecuteAsync(object? parameter = null)
    {
        if (!CanExecute(parameter)) return;
        _executing = true;
        Refresh();
        try { await execute(parameter); }
        catch (OperationCanceledException) { }
        catch (Exception error) { reportError(error); }
        finally { _executing = false; Refresh(); }
    }
    public async void Execute(object? parameter) => await ExecuteAsync(parameter);
    internal void Refresh() => CanExecuteChanged?.Invoke(this, EventArgs.Empty);
}
