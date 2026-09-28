using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using ProPDF.SampleSupport;

namespace ProPDF.Uno.Sample;

public sealed partial class App : Application
{
    private Window? _window;
    private DemoWorkspace? _runtime;
    private PdfEditor? _editor;
    private IPdfUnoFiles? _files;
    public App()
    {
        UnhandledException += (_, e) => PlatformHost.Failed(e.Exception.ToString());
        Console.WriteLine("ProPDF: initializing application resources.");
        InitializeComponent(); RequestedTheme = ApplicationTheme.Light;
    }
    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        try
        {
            Console.WriteLine("ProPDF: constructing editor controls.");
            _window = new Window { Title = "ProPDF · Uno PDF editor" };
            _editor = new PdfEditor();
            _window.Content = _editor;
            var loaded = false;
            _editor.Loaded += async (_, _) =>
            {
                if (loaded) return; loaded = true;
                try
                {
                    Console.WriteLine("ProPDF: editor loaded; initializing files and workspace.");
                    await PlatformHost.InitializeAsync();
                    var fonts = await PlatformHost.LoadFontsAsync();
                    _runtime = new DemoWorkspace(action => _editor.DispatcherQueue.TryEnqueue(() => action()), fonts);
                    _files = PlatformHost.CreateFiles();
                    _editor.Files = _files; _editor.Context = _runtime.Context;
                    _runtime.Viewport.Invalidated += (_, _) => _editor.DispatcherQueue.TryEnqueue(() => PlatformHost.SetDirty(_runtime.Session.IsDirty || _runtime.Viewport.PendingRedactions != 0));
                    Console.WriteLine("ProPDF: creating welcome PDF.");
                    await _runtime.LoadWelcomeAsync();
                    _runtime.Viewport.FitWidth();
                    Console.WriteLine("ProPDF: waiting for initial PDF tiles.");
                    await _runtime.Viewport.WaitForRenderingAsync();
                    await PlatformHost.ReadyAsync(_editor, _runtime);
                    Console.WriteLine("ProPDF: editor ready.");
                }
                catch (Exception error) { _runtime?.Viewport.ReportError(error); PlatformHost.Failed(error.ToString()); }
            };
            _window.Closed += async (_, _) =>
            {
                _editor.Dispose(); if (_runtime is not null) await _runtime.DisposeAsync(); _files?.Dispose();
            };
            Console.WriteLine("ProPDF: activating editor window.");
            _window.Activate();
        }
        catch (Exception error) { PlatformHost.Failed(error.ToString()); }
    }
}
