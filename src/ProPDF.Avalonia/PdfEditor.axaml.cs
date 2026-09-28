using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Markup.Xaml;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using ProPDF.Core;
using ProPDF.Presentation;

namespace ProPDF.Avalonia;

public sealed partial class PdfEditor : UserControl
{
    public static readonly StyledProperty<PdfEditorContext?> ContextProperty = AvaloniaProperty.Register<PdfEditor, PdfEditorContext?>(nameof(Context));
    public PdfEditorContext? Context { get => GetValue(ContextProperty); set => SetValue(ContextProperty, value); }
    public PdfWorkspace? Workspace { get; private set; }
    private readonly PdfEditorChrome _chrome;
    public PdfView View { get; } = new();
    public PdfEditor()
    {
        AvaloniaXamlLoader.Load(this);
        var inspector = (TabControl)Content!;
        Content = null;
        var pages = new ListBox { Name = "PagesList", Background = Brushes.Transparent, BorderThickness = new Thickness(0),
            ItemTemplate = (global::Avalonia.Controls.Templates.IDataTemplate)Resources["PageThumbnailTemplate"]! };
        pages.Bind(ItemsControl.ItemsSourceProperty, new global::Avalonia.Data.Binding("Pages"));
        pages.Bind(ListBox.SelectedItemProperty, new global::Avalonia.Data.Binding("SelectedPage") { Mode = global::Avalonia.Data.BindingMode.TwoWay });
        global::Avalonia.Automation.AutomationProperties.SetName(pages, "Document pages");
        _chrome = new PdfEditorChrome(inspector, pages, View, index => inspector.SelectedIndex = index);
        Content = _chrome.Root;
    }
    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change)
    {
        base.OnPropertyChanged(change);
        if (change.Property == ContextProperty) ResetWorkspace();
    }
    private void ResetWorkspace()
    {
        Workspace?.Dispose();
        Workspace = Context is { } context ? new PdfWorkspace(context, new Dialogs(this), action => Dispatcher.UIThread.Post(action)) : null;
        DataContext = Workspace; View.Controller = Context?.Viewport; _chrome.Connect(Workspace);
    }
    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        if (Workspace is null && Context is not null) ResetWorkspace();
    }
    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        _chrome.Connect(null); Workspace?.Dispose(); Workspace = null; DataContext = null; View.Controller = null;
        base.OnDetachedFromVisualTree(e);
    }

    private sealed class Dialogs(PdfEditor owner) : IPdfWorkspaceDialogs, IPdfExternalNavigation
    {
        private TopLevel Top => TopLevel.GetTopLevel(owner) ?? throw new InvalidOperationException("Attach the editor to a window before opening dialogs.");
        public async Task<string?> PickOpenPathAsync(PdfFileKind kind, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var filter = kind switch
            {
                PdfFileKind.Pdf => new FilePickerFileType("PDF documents") { Patterns = ["*.pdf"], MimeTypes = ["application/pdf"] },
                PdfFileKind.Image => new FilePickerFileType("Images") { Patterns = ["*.png", "*.jpg", "*.jpeg", "*.tif", "*.tiff", "*.bmp"] },
                _ => FilePickerFileTypes.All
            };
            var files = await Top.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions { Title = "Open " + kind, AllowMultiple = false, FileTypeFilter = [filter] });
            if (files.Count == 0) return null;
            using var file = files[0];
            cancellationToken.ThrowIfCancellationRequested();
            return file.TryGetLocalPath() ?? throw new NotSupportedException("This editor requires local filesystem paths.");
        }
        public async Task<string?> PickSavePathAsync(string suggestedName, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            using var file = await Top.StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
            {
                Title = "Save a copy", SuggestedFileName = suggestedName, DefaultExtension = Path.GetExtension(suggestedName).TrimStart('.')
            });
            cancellationToken.ThrowIfCancellationRequested();
            return file is null ? null : file.TryGetLocalPath() ?? throw new NotSupportedException("Atomic saving requires a local filesystem path.");
        }
        public Task<string?> RequestPasswordAsync(CancellationToken cancellationToken) => PromptAsync("Open protected PDF", "Enter the document password. Editing requires the owner password.", true, cancellationToken);
        public async Task<bool> ConfirmAsync(string title, string message, CancellationToken cancellationToken) => await PromptAsync(title, message, false, cancellationToken) is not null;
        private async Task<string?> PromptAsync(string title, string message, bool secret, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var parent = Top as Window ?? throw new NotSupportedException("Modal confirmation requires a desktop Window host.");
            var input = new TextBox { PasswordChar = '●', Margin = new Thickness(0, 12, 0, 0) };
            var dialog = new Window { Title = title, Width = 480, SizeToContent = SizeToContent.Height, CanResize = false, WindowStartupLocation = WindowStartupLocation.CenterOwner };
            var content = new StackPanel { Margin = new Thickness(24), Spacing = 14 };
            content.Children.Add(new TextBlock { Text = title, FontSize = 19, FontWeight = FontWeight.SemiBold });
            content.Children.Add(new TextBlock { Text = message, TextWrapping = TextWrapping.Wrap, FontSize = 13 });
            if (secret) content.Children.Add(input);
            var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Spacing = 8 };
            var cancel = new Button { Content = "Cancel", IsCancel = true, Padding = new Thickness(16, 8) };
            var accept = new Button { Content = secret ? "Open" : "Continue", Padding = new Thickness(16, 8) };
            cancel.Click += (_, _) => dialog.Close(null);
            accept.Click += (_, _) => dialog.Close(secret ? input.Text ?? "" : "confirmed");
            buttons.Children.Add(cancel); buttons.Children.Add(accept); content.Children.Add(buttons);
            dialog.Content = content;
            var result = await dialog.ShowDialog<string?>(parent);
            input.Text = "";
            cancellationToken.ThrowIfCancellationRequested();
            return result;
        }
        public async Task CopyTextAsync(string text, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (Top.Clipboard is { } clipboard) await clipboard.SetTextAsync(text);
        }
        public Task OpenUriAsync(Uri uri, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!PdfUriPolicy.TryNormalize(uri.AbsoluteUri, out var safe)) throw new InvalidOperationException("URI blocked by the document navigation policy.");
            using var process = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(safe!.AbsoluteUri) { UseShellExecute = true });
            return Task.CompletedTask;
        }
    }
}
