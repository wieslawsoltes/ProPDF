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
    public App() { InitializeComponent(); RequestedTheme = ApplicationTheme.Light; }
    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        _window = new Window { Title = "ProPDF · Uno PDF editor" };
        _editor = new PdfEditor();
        _runtime = new DemoWorkspace(action => _editor.DispatcherQueue.TryEnqueue(() => action()));
        _window.Content = _editor;
        var loaded = false;
        _editor.Loaded += async (_, _) =>
        {
            if (loaded) return; loaded = true;
            try
            {
                await PlatformHost.InitializeAsync();
                _files = PlatformHost.CreateFiles();
                _editor.Files = _files; _editor.Context = _runtime.Context;
                _runtime.Viewport.Invalidated += (_, _) => _editor.DispatcherQueue.TryEnqueue(() => PlatformHost.SetDirty(_runtime.Session.IsDirty || _runtime.Viewport.PendingRedactions != 0));
                await _runtime.LoadWelcomeAsync();
                _runtime.Viewport.FitWidth();
                await _runtime.Viewport.WaitForRenderingAsync();
                await PlatformHost.ReadyAsync(_editor, _runtime);
            }
            catch (Exception error) { _runtime.Viewport.ReportError(error); PlatformHost.Failed(error.ToString()); }
        };
        _window.Closed += async (_, _) =>
        {
            _editor.Dispose(); await _runtime.DisposeAsync(); _files?.Dispose();
        };
        _window.Activate();
    }
}
