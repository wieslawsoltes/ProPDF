using Windows.ApplicationModel.DataTransfer;

namespace ProPDF.Uno;

/// <summary>Native Uno prompts, clipboard and safe external URI handoff. File ownership remains with the host.</summary>
public sealed class PdfUnoDialogs(FrameworkElement owner, IPdfUnoFiles files) : IPdfWorkspaceDialogs, IPdfWorkspaceFileTransfer, IPdfExternalNavigation
{
    private readonly SemaphoreSlim _modal = new(1, 1);
    public bool AlwaysPickSaveDestination => true;
    public Task<string?> PickOpenPathAsync(PdfFileKind kind, CancellationToken token) => files.PickOpenPathAsync(kind, token);
    public Task<string?> PickSavePathAsync(string suggestedName, CancellationToken token) => files.PickSavePathAsync(suggestedName, token);
    public Task PublishFileAsync(string path, CancellationToken token) => files.PublishFileAsync(path, token);
    public async Task<string?> RequestPasswordAsync(CancellationToken token)
    {
        var input = new PasswordBox();
        var result = await ShowAsync("Open protected PDF", input, "Open", token);
        var password = result == ContentDialogResult.Primary ? input.Password : null; input.Password = ""; return password;
    }
    public async Task<bool> ConfirmAsync(string title, string message, CancellationToken token) =>
        await ShowAsync(title, new TextBlock { Text = message, TextWrapping = TextWrapping.Wrap, MaxWidth = 440 }, "Continue", token) == ContentDialogResult.Primary;
    public Task CopyTextAsync(string text, CancellationToken token)
    {
        token.ThrowIfCancellationRequested(); var data = new DataPackage(); data.SetText(text); Clipboard.SetContent(data); return Task.CompletedTask;
    }
    public async Task OpenUriAsync(Uri uri, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (!PdfUriPolicy.TryNormalize(uri.AbsoluteUri, out _)) throw new InvalidOperationException("The URI scheme is not allowed.");
        if (!await Launcher.LaunchUriAsync(uri)) throw new InvalidOperationException("The browser or system refused to open this link.");
    }
    private async Task<ContentDialogResult> ShowAsync(string title, object content, string primary, CancellationToken token)
    {
        await _modal.WaitAsync(token);
        try
        {
            token.ThrowIfCancellationRequested();
            var dialog = new ContentDialog { XamlRoot = owner.XamlRoot, Title = title, Content = content,
                PrimaryButtonText = primary, CloseButtonText = "Cancel", DefaultButton = ContentDialogButton.Close };
            using var cancellation = token.Register(() => owner.DispatcherQueue.TryEnqueue(dialog.Hide));
            var result = await dialog.ShowAsync(); token.ThrowIfCancellationRequested(); return result;
        }
        finally { _modal.Release(); }
    }
}
