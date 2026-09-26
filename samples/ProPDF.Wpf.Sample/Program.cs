using System.Windows;
using ProPDF.SampleSupport;

namespace ProPDF.Wpf.Sample;

internal static class Program
{
    [STAThread]
    public static void Main()
    {
        var application = new Application();
        var runtime = new DemoWorkspace(action => application.Dispatcher.BeginInvoke(action));
        var editor = new PdfEditor { Context = runtime.Context };
        var window = new Window { Title = "ProPDF — PDF editor", Width = 1450, Height = 960, MinWidth = 900, MinHeight = 640, Content = editor };
        var loaded = false;
        var closing = false;
        var approved = false;
        window.Loaded += async (_, _) =>
        {
            if (loaded) return;
            loaded = true;
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
                application.Dispatcher.BeginInvoke(new Action(window.Close));
            }
            finally { closing = false; }
        };
        application.Run(window);
    }
}
