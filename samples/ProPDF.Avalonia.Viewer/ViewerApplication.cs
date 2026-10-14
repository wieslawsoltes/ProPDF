using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml.Styling;
using Avalonia.Styling;
using Avalonia.Themes.Fluent;

namespace ProPDF.Avalonia.Viewer;

public sealed class ViewerApplication : Application
{
    public static ViewerShell? Shell { get; private set; }
    public static Func<IPdfViewerPrintService>? CreatePrintService { get; set; }
    public override void Initialize()
    {
        RequestedThemeVariant = ThemeVariant.Light;
        Styles.Add(new FluentTheme());
        Styles.Add(new StyleInclude(new Uri("avares://ProPDF.Avalonia/")) { Source = new Uri("avares://ProPDF.Avalonia/Themes/PdfViewer.axaml") });
    }
    public override void OnFrameworkInitializationCompleted()
    {
        Shell = new ViewerShell();
        Shell.Viewer.PrintService = CreatePrintService?.Invoke();
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            var window = new Window { Title = "ProPDF · Avalonia PDF Viewer", Width = 1440, Height = 980, MinWidth = 360, MinHeight = 420, Content = Shell };
            var closing = false;
            window.Closing += async (_, e) =>
            {
                if (closing) return;
                e.Cancel = true;
                try
                {
                    if (Shell.Viewer.Workspace is { } workspace && !await workspace.ConfirmDiscardAsync()) return;
                    closing = true; await Shell.DisposeAsync(); window.Close();
                }
                catch (Exception error) { Shell.Viewer.Context?.Viewport.ReportError(error); }
            };
            desktop.MainWindow = window;
        }
        else if (ApplicationLifetime is ISingleViewApplicationLifetime single) single.MainView = Shell;
        base.OnFrameworkInitializationCompleted();
    }
}
