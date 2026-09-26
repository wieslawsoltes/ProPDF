using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Microsoft.Win32;
using ProPDF.Presentation;

namespace ProPDF.Wpf;

public sealed partial class PdfEditor : UserControl
{
    public static readonly DependencyProperty ContextProperty = DependencyProperty.Register(nameof(Context), typeof(PdfEditorContext), typeof(PdfEditor),
        new PropertyMetadata(null, (target, _) => ((PdfEditor)target).ResetWorkspace()));
    public PdfEditorContext? Context { get => (PdfEditorContext?)GetValue(ContextProperty); set => SetValue(ContextProperty, value); }
    public PdfWorkspace? Workspace { get; private set; }
    public PdfEditor()
    {
        InitializeComponent();
        Loaded += (_, _) => { if (Workspace is null && Context is not null) ResetWorkspace(); };
        Unloaded += (_, _) => { Workspace?.Dispose(); Workspace = null; DataContext = null; };
    }
    private void ResetWorkspace()
    {
        Workspace?.Dispose();
        Workspace = Context is { } context ? new PdfWorkspace(context, new Dialogs(this), action => Dispatcher.BeginInvoke(action)) : null;
        DataContext = Workspace;
    }

    private sealed class Dialogs(PdfEditor owner) : IPdfWorkspaceDialogs
    {
        public Task<string?> PickOpenPathAsync(PdfFileKind kind, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var dialog = new OpenFileDialog
            {
                Title = "Open " + kind, Multiselect = false, CheckFileExists = true,
                Filter = kind switch
                {
                    PdfFileKind.Pdf => "PDF documents|*.pdf",
                    PdfFileKind.Image => "Images|*.png;*.jpg;*.jpeg;*.tif;*.tiff;*.bmp",
                    _ => "All files|*.*"
                }
            };
            var result = dialog.ShowDialog(Window.GetWindow(owner)) == true ? dialog.FileName : null;
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(result);
        }
        public Task<string?> PickSavePathAsync(string suggestedName, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var dialog = new SaveFileDialog { Title = "Save a copy", FileName = suggestedName, DefaultExt = System.IO.Path.GetExtension(suggestedName), OverwritePrompt = true };
            var result = dialog.ShowDialog(Window.GetWindow(owner)) == true ? dialog.FileName : null;
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(result);
        }
        public Task<string?> RequestPasswordAsync(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var input = new PasswordBox { Margin = new Thickness(0, 12, 0, 16), Padding = new Thickness(8), MinHeight = 34 };
            var dialog = new Window
            {
                Title = "Open protected PDF", Width = 470, SizeToContent = SizeToContent.Height, ResizeMode = ResizeMode.NoResize,
                WindowStartupLocation = WindowStartupLocation.CenterOwner, Owner = Window.GetWindow(owner), Background = Brushes.White
            };
            var panel = new StackPanel { Margin = new Thickness(24) };
            panel.Children.Add(new TextBlock { Text = "Document password", FontSize = 19, FontWeight = FontWeights.SemiBold });
            panel.Children.Add(new TextBlock { Text = "Editing a protected PDF requires its owner password.", Margin = new Thickness(0, 12, 0, 0), TextWrapping = TextWrapping.Wrap });
            panel.Children.Add(input);
            var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
            var cancel = new Button { Content = "Cancel", IsCancel = true, Padding = new Thickness(16, 8), Margin = new Thickness(4) };
            var accept = new Button { Content = "Open", IsDefault = true, Padding = new Thickness(16, 8), Margin = new Thickness(4) };
            cancel.Click += (_, _) => dialog.DialogResult = false;
            accept.Click += (_, _) => dialog.DialogResult = true;
            buttons.Children.Add(cancel); buttons.Children.Add(accept); panel.Children.Add(buttons); dialog.Content = panel;
            var result = dialog.ShowDialog() == true ? input.Password : null;
            input.Clear();
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(result);
        }
        public Task<bool> ConfirmAsync(string title, string message, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var result = MessageBox.Show(Window.GetWindow(owner), message, title, MessageBoxButton.OKCancel, MessageBoxImage.Warning, MessageBoxResult.Cancel) == MessageBoxResult.OK;
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(result);
        }
        public Task CopyTextAsync(string text, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (text.Length != 0) Clipboard.SetText(text);
            return Task.CompletedTask;
        }
    }
}
