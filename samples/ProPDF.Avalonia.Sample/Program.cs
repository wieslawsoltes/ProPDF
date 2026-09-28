using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Styling;
using Avalonia.Themes.Fluent;
using Avalonia.Threading;
using ProPDF.SampleSupport;

namespace ProPDF.Avalonia.Sample;

internal static class Program
{
    [STAThread]
    public static void Main(string[] args) => AppBuilder.Configure<SampleApplication>().UsePlatformDetect().UseSkia().LogToTrace().StartWithClassicDesktopLifetime(args);
}

public sealed class SampleApplication : Application
{
    public override void Initialize()
    {
        Styles.Add(new FluentTheme());
        RequestedThemeVariant = ThemeVariant.Light;
    }
    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            var runtime = new DemoWorkspace(action => Dispatcher.UIThread.Post(action));
            var editor = new PdfEditor { Context = runtime.Context };
            var window = new Window { Title = "ProPDF — PDF editor", Width = 1450, Height = 960, MinWidth = 640, MinHeight = 480, Content = editor };
            var closing = false;
            var approved = false;
            window.Opened += async (_, _) =>
            {
                try { await runtime.LoadWelcomeAsync(); }
                catch (Exception error) { runtime.Viewport.ReportError(error); }
            };
            window.Closing += async (_, args) =>
            {
                if (approved) return;
                args.Cancel = true;
                if (closing) return;
                closing = true;
                try
                {
                    if (editor.Workspace is { } workspace && !await workspace.ConfirmDiscardAsync()) return;
                    window.Content = null;
                    await runtime.DisposeAsync();
                    approved = true;
                    Dispatcher.UIThread.Post(window.Close);
                }
                finally { closing = false; }
            };
            desktop.MainWindow = window;
        }
        base.OnFrameworkInitializationCompleted();
    }
}
