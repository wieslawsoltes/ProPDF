using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Threading;
using Avalonia.VisualTree;
using ProPDF.Core;

namespace ProPDF.Avalonia;

public partial class PdfViewer
{
    private long _searchGeneration;
    private TaskCompletionSource<string?>? _prompt;
    private IInputElement? _previousFocus;

    /// <summary>Opens caller-owned input without taking ownership of the stream. Failed opens preserve the current revision.</summary>
    public async Task LoadAsync(Stream input, string? password = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(input);
        Dispatcher.UIThread.VerifyAccess();
        if (!_attached || Context is not { } context || Workspace is not { } workspace || _lifetime is null) throw new InvalidOperationException("Attach the viewer and assign Context before opening a document.");
        if (IsBusy || IsDialogOpen) throw new InvalidOperationException("Another document operation is active.");
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _lifetime.Token);
        _loading = true; Synchronize();
        try
        {
            if (!await workspace.ConfirmDiscardAsync(linked.Token)) return;
            await context.Session.OpenAsync(input, new PdfOpenOptions { Password = password }, cancellationToken: linked.Token);
            context.Viewport.GoToPage(1); QueueFit();
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception error) { ReportError(error); throw; }
        finally { _loading = false; Synchronize(); }
    }

    public Task SearchAsync(CancellationToken cancellationToken = default) => RunSearchAsync(false, cancellationToken);
    private void QueueSearch() { if (_attached && Context?.Session.Current is not null) _ = RunSearchAsync(true, CancellationToken.None); }
    public void CancelSearch()
    {
        _searchGeneration++;
        _searchCancellation?.Cancel(); _searchCancellation?.Dispose(); _searchCancellation = null;
        SetAndRaise(IsSearchingProperty, ref _isSearching, false);
        RefreshCommands();
    }
    private async Task RunSearchAsync(bool debounce, CancellationToken cancellationToken)
    {
        if (!_attached || Context?.Viewport is not { } controller || controller.Document is null || _lifetime is null) return;
        CancelSearch();
        var generation = ++_searchGeneration;
        var cancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _lifetime.Token);
        _searchCancellation = cancellation;
        var query = SearchText ?? "";
        var options = new PdfSearchOptions { MatchCase = MatchCase, WholeWord = WholeWord, RegularExpression = UseRegularExpression };
        SetAndRaise(IsSearchingProperty, ref _isSearching, true); RefreshCommands();
        try
        {
            if (debounce) await Task.Delay(220, cancellation.Token);
            await controller.SearchAsync(query, options, cancellation.Token);
            if (_attached && generation == _searchGeneration && Context?.Viewport == controller)
            {
                Synchronize(); RaiseEvent(new global::Avalonia.Interactivity.RoutedEventArgs(SearchCompletedEvent));
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception error) { if (_attached && generation == _searchGeneration) ReportError(error); }
        finally
        {
            if (generation == _searchGeneration)
            {
                _searchCancellation = null; SetAndRaise(IsSearchingProperty, ref _isSearching, false); Synchronize();
            }
            cancellation.Dispose();
        }
    }

    internal async Task<string?> PromptAsync(string title, string message, bool password, CancellationToken cancellationToken)
    {
        Dispatcher.UIThread.VerifyAccess();
        cancellationToken.ThrowIfCancellationRequested();
        if (!_attached) throw new OperationCanceledException("The viewer was detached.");
        if (_prompt is not null) throw new InvalidOperationException("Another confirmation is already open.");
        if (_dialogInput is null || _dialogAccept is null) throw new NotSupportedException("This template has no dialog parts. Supply the Dialogs host service or restore the default viewer template.");
        _previousFocus = TopLevel.GetTopLevel(this)?.FocusManager?.GetFocusedElement();
        var completion = new TaskCompletionSource<string?>(TaskCreationOptions.RunContinuationsAsynchronously);
        _prompt = completion;
        SetCurrentValue(DialogValueProperty, "");
        SetAndRaise(DialogTitleProperty, ref _dialogTitle, title);
        SetAndRaise(DialogMessageProperty, ref _dialogMessage, message);
        SetAndRaise(IsPasswordPromptProperty, ref _isPasswordPrompt, password);
        SetAndRaise(IsDialogOpenProperty, ref _isDialogOpen, true);
        RefreshCommands();
        Dispatcher.UIThread.Post(() => { if (_prompt == completion) { if (password) _dialogInput?.Focus(); else _dialogAccept?.Focus(); } });
        using var registration = cancellationToken.Register(() => Dispatcher.UIThread.Post(() => { if (_prompt == completion) CompletePrompt(false); }));
        return await completion.Task;
    }
    private void CompletePrompt(bool accept)
    {
        var completion = _prompt;
        if (completion is null) return;
        var result = accept ? (IsPasswordPrompt ? DialogValue : "accepted") : null;
        _prompt = null;
        SetAndRaise(IsDialogOpenProperty, ref _isDialogOpen, false);
        SetCurrentValue(DialogValueProperty, "");
        RefreshCommands();
        completion.TrySetResult(result);
        var focus = _previousFocus; _previousFocus = null;
        if (_attached) Dispatcher.UIThread.Post(() => focus?.Focus());
    }
    private async void HandleKey(object? sender, KeyEventArgs e)
    {
        if (e.Handled) return;
        if (IsDialogOpen)
        {
            if (e.Key == Key.Escape) { CompletePrompt(false); e.Handled = true; }
            else if (e.Key == Key.Enter) { CompletePrompt(true); e.Handled = true; }
            return;
        }
        var textBox = (e.Source as Visual)?.GetSelfAndVisualAncestors().OfType<TextBox>().FirstOrDefault();
        var modifier = (e.KeyModifiers & (KeyModifiers.Control | KeyModifiers.Meta)) != 0;
        PdfViewerCommand? command = null;
        try
        {
            if (modifier && e.Key == Key.F)
            {
                SetCurrentValue(IsSearchBarVisibleProperty, true); Dispatcher.UIThread.Post(() => { _searchBox?.Focus(); _searchBox?.SelectAll(); }); e.Handled = true; return;
            }
            if (modifier && textBox is null)
            {
                switch (e.Key)
                {
                    case Key.O: if (Workspace is not null) await Workspace.OpenCommand.ExecuteAsync(); e.Handled = true; return;
                    case Key.S: if (Workspace is not null) await Workspace.SaveAsCommand.ExecuteAsync(); e.Handled = true; return;
                    case Key.P: command = PrintCommand; break;
                    case Key.Z: command = (e.KeyModifiers & KeyModifiers.Shift) != 0 ? RedoCommand : UndoCommand; break;
                    case Key.Y: command = RedoCommand; break;
                    case Key.D0: command = FitPageCommand; break;
                    case Key.Add: case Key.OemPlus: command = ZoomInCommand; break;
                    case Key.Subtract: case Key.OemMinus: command = ZoomOutCommand; break;
                    case Key.Home: command = FirstPageCommand; break;
                    case Key.End: command = LastPageCommand; break;
                }
            }
            if (e.Key == Key.F3) command = (e.KeyModifiers & KeyModifiers.Shift) != 0 ? PreviousMatchCommand : NextMatchCommand;
            if ((e.KeyModifiers & KeyModifiers.Alt) != 0 && Workspace is not null && textBox is null)
            {
                if (e.Key == Key.Left) { await Workspace.BackCommand.ExecuteAsync(); e.Handled = true; return; }
                if (e.Key == Key.Right) { await Workspace.ForwardCommand.ExecuteAsync(); e.Handled = true; return; }
            }
            if (e.Key == Key.Enter && textBox is not null)
            {
                command = textBox.Name switch { "PageInput" => GoToPageCommand, "ZoomInput" => SetZoomCommand, "PART_SearchBox" => SearchCommand, _ => command };
            }
            if (e.Key == Key.Escape)
            {
                CancelSearch(); Context?.Viewport.CancelInteraction(); Context?.Viewport.ClearSelection();
                SetCurrentValue(IsSearchBarVisibleProperty, false); Focus(); e.Handled = true; return;
            }
            if (command is not null) { e.Handled = true; await command.ExecuteAsync(); }
        }
        catch (Exception error) { ReportError(error); }
    }
}
