using System.Diagnostics;
using System.Text.Json;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Headless;
using Avalonia.Input;
using Avalonia.Markup.Xaml.Styling;
using Avalonia.Media.Imaging;
using Avalonia.Themes.Fluent;
using Avalonia.Threading;
using Avalonia.VisualTree;
using ProPDF.Core;
using ProPDF.Presentation;

namespace ProPDF.Avalonia.Viewer.Smoke;

internal static class Program
{
    private static readonly List<string> Checks = [];
    [STAThread]
    public static int Main(string[] args)
    {
        var output = args.Length > 0 ? args[0] : "artifacts/avalonia-viewer/native";
        Directory.CreateDirectory(output);
        try
        {
            AppBuilder.Configure<SmokeApplication>().UseSkia().WithInterFont()
                .UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = false }).SetupWithoutStarting();
            var empty = new PdfViewer { Zoom = double.NaN, SidebarWidth = double.PositiveInfinity, PageNumber = -5 };
            Check(empty.IsReadOnly, "Read-only defaults to true");
            Check(empty.Zoom == 1 && empty.SidebarWidth == 264 && empty.PageNumber == 1, "Invalid numeric inputs coerce to safe values");
            Check(!empty.NextPageCommand.CanExecute(null) && !empty.PrintCommand.CanExecute(null), "Detached viewer commands are disabled");
            foreach (var type in new[] { typeof(PdfViewer), typeof(PdfViewerToolbar), typeof(PdfViewerSidebar), typeof(PdfViewerViewport), typeof(PdfViewerThumbnail) })
                Check(typeof(TemplatedControl).IsAssignableFrom(type), type.Name + " is a TemplatedControl");
            var shell = new ViewerShell();
            var hostContext = new object();
            shell.Viewer.DataContext = hostContext;
            var window = new Window { Width = 1440, Height = 980, Content = shell };
            window.Show();
            Until(() => shell.Ready || shell.StartupError is not null);
            Check(shell.StartupError is null, "Generated PDF starts without errors");
            var viewer = shell.Viewer;
            var runtime = shell.Runtime;
            Until(() => viewer.PageCount == 3 && viewer.Workspace?.NavigationBookmarks.Count == 3);
            Pump(runtime.Viewport.WaitForRenderingAsync());
            Check(ReferenceEquals(viewer.DataContext, hostContext), "Template preserves host DataContext");
            Check(viewer.GetVisualDescendants().OfType<PdfViewerViewport>().Any(), "Default viewport template is instantiated");
            Check(viewer.GetVisualDescendants().OfType<PdfViewerToolbar>().Any(), "Toolbar is an independently templated part");
            Check(viewer.GetVisualDescendants().OfType<PdfViewerSidebar>().Any(), "Sidebar is an independently templated part");
            using (var scene = runtime.Viewport.CaptureScene()) Check(scene.TileCount > 0 && scene.RasterPixelCount > 0, "Native Skia renders real PDF tiles");
            Check(shell.FontFaceCount >= 2, "Explicit regular and bold fallback faces are present");
            var fitButton = viewer.GetVisualDescendants().OfType<Button>().Single(b => b.Name == "FitWidthButton");
            Check(fitButton.Focus(), "Toolbar button can receive keyboard focus");
            window.KeyPress(Key.F, RawInputModifiers.Control); Pump(Task.CompletedTask);
            Check(viewer.IsSearchBarVisible, "Ctrl+F from a toolbar button reveals the search bar");
            Check((window.FocusManager?.GetFocusedElement() as Control)?.Name == "PART_SearchBox", "Find moves keyboard focus to the search box");
            window.KeyPress(Key.Escape, RawInputModifiers.None); Pump(Task.CompletedTask);
            Check(!viewer.IsSearchBarVisible, "Escape closes keyboard find");
            Pump(viewer.NextPageCommand.ExecuteAsync()); Until(() => viewer.PageNumber == 2);
            Check(runtime.Viewport.CurrentPage == 2, "Viewer command navigates the actual controller");
            Pump(viewer.PreviousPageCommand.ExecuteAsync()); Until(() => viewer.PageNumber == 1);
            Check(!viewer.FirstPageCommand.CanExecute(null), "First-page command is disabled at the boundary");
            viewer.PageText = "9999"; Pump(viewer.GoToPageCommand.ExecuteAsync());
            Check(viewer.HasError && viewer.PageNumber == 1, "Invalid page entry reports an error without changing the page");
            Pump(viewer.ClearErrorCommand.ExecuteAsync());
            Check(!viewer.HasError, "Error dismissal clears viewer errors");
            viewer.SearchText = "PDF"; Pump(viewer.SearchAsync());
            Check(viewer.SearchResults.Count >= 3 && !viewer.IsSearching, "Document-wide search completes with results");
            viewer.SearchResultIndex = viewer.SearchResults.Count - 1; Pump(Task.CompletedTask);
            Check(runtime.Viewport.SearchIndex == viewer.SearchResults.Count - 1, "Result selection navigates directly to the requested hit");
            viewer.UseRegularExpression = true; viewer.SearchText = "["; Pump(viewer.SearchAsync());
            Check(viewer.HasError && !viewer.IsSearching, "Invalid regex is a recoverable error");
            viewer.UseRegularExpression = false; viewer.SearchText = ""; Pump(viewer.SearchAsync()); Pump(viewer.ClearErrorCommand.ExecuteAsync());
            Check(viewer.SearchResults.Count == 0, "Empty query clears search results");
            viewer.SearchText = "document"; viewer.CancelSearch(); Pump(Task.CompletedTask);
            Check(!viewer.IsSearching, "Explicit cancellation clears search busy state");
            Check(!viewer.SetToolCommand.CanExecute("Highlight") && !viewer.UndoCommand.CanExecute(null), "Read-only viewer blocks editing and undo");
            viewer.IsReadOnly = false;
            Pump(viewer.SetToolCommand.ExecuteAsync("Highlight"));
            Check(runtime.Viewport.Tool == PdfTool.Highlight, "Host may explicitly enable annotation tools");
            viewer.IsReadOnly = true;
            Check(runtime.Viewport.Tool == PdfTool.Pan, "Switching to read-only cancels an editing tool");
            var revision = runtime.Session.Current!.Id;
            using (var malformed = new MemoryStream("not a pdf"u8.ToArray()))
            {
                try { Pump(viewer.LoadAsync(malformed)); throw new InvalidOperationException("Malformed load unexpectedly succeeded."); }
                catch (Exception error) when (error.Message != "Malformed load unexpectedly succeeded.") { }
            }
            Check(runtime.Session.Current!.Id == revision && viewer.HasError, "Failed stream loading preserves the current revision");
            Pump(viewer.ClearErrorCommand.ExecuteAsync());
            viewer.SearchText = ""; Pump(viewer.SearchAsync());
            Pump(viewer.FirstPageCommand.ExecuteAsync());
            shell.SetMinimalTemplate(true); Pump(Task.CompletedTask);
            Check(viewer.GetVisualDescendants().OfType<Button>().Any(b => b.Name == "MinimalNextButton"), "Complete custom template replacement applies");
            Pump(viewer.NextPageCommand.ExecuteAsync()); Until(() => viewer.PageNumber == 2);
            Check(viewer.PageNumber == 2, "Commands survive retemplating");
            shell.SetMinimalTemplate(false); Pump(Task.CompletedTask);
            Check(viewer.GetVisualDescendants().OfType<Button>().Any(b => b.Name == "NextPageButton"), "Default template restores without reconstructing the viewer");
            var pageEvents = 0;
            viewer.PageChanged += (_, _) => pageEvents++;
            window.Content = null; Pump(Task.CompletedTask);
            Check(viewer.Workspace is null, "Detach releases the viewer workspace");
            Check(runtime.Session.Current!.Id == revision, "Detach does not dispose or replace the host document");
            window.Content = shell; Pump(Task.CompletedTask);
            Until(() => viewer.Workspace is not null && viewer.PageNumber == 2);
            pageEvents = 0;
            Pump(viewer.NextPageCommand.ExecuteAsync()); Until(() => viewer.PageNumber == 3);
            Check(pageEvents == 1, "Reattach does not duplicate page-change subscriptions");
            shell.ApplyTheme(true); Pump(Task.CompletedTask);
            Check(Application.Current!.ActualThemeVariant.ToString() == "Dark", "Runtime theme switching works");
            window.Width = 650; window.Height = 880; Pump(Task.CompletedTask);
            Until(() => viewer.IsCompact);
            Check(viewer.ViewportMargin.Left == 0, "Narrow layout overlays the sidebar");
            Pump(runtime.Viewport.WaitForRenderingAsync());
            using (var frame = window.CaptureRenderedFrame())
            {
                Check(frame is not null, "Native viewer screenshot is available");
                frame!.Save(Path.Combine(output, "native-viewer.png"), PngBitmapEncoderOptions.Default);
            }
            File.WriteAllText(Path.Combine(output, "state.json"), shell.Snapshot());
            Pump(shell.DisposeAsync().AsTask()); window.Close();
            File.WriteAllText(Path.Combine(output, "report.json"), JsonSerializer.Serialize(new { passed = true, count = Checks.Count, checks = Checks }, new JsonSerializerOptions { WriteIndented = true }));
            Console.WriteLine($"{Checks.Count} Avalonia viewer contract checks passed.");
            return 0;
        }
        catch (Exception error)
        {
            File.WriteAllText(Path.Combine(output, "report.json"), JsonSerializer.Serialize(new { passed = false, checks = Checks, error = error.ToString() }, new JsonSerializerOptions { WriteIndented = true }));
            Console.Error.WriteLine(error); return 1;
        }
    }
    private static void Check(bool condition, string name)
    {
        if (!condition) throw new InvalidOperationException(name);
        Checks.Add(name); Console.WriteLine("PASS " + name);
    }
    private static void Tick()
    {
        Dispatcher.UIThread.RunJobs();
        AvaloniaHeadlessPlatform.ForceRenderTimerTick();
        Dispatcher.UIThread.RunJobs();
    }
    private static void Until(Func<bool> condition)
    {
        var watch = Stopwatch.StartNew();
        while (!condition()) { Tick(); if (watch.Elapsed > TimeSpan.FromSeconds(30)) throw new TimeoutException("Viewer did not reach expected state."); Thread.Sleep(8); }
        Tick();
    }
    private static void Pump(Task task)
    {
        Until(() => task.IsCompleted); task.GetAwaiter().GetResult();
        for (var i = 0; i < 4; i++) { Tick(); Thread.Sleep(5); }
    }
    private sealed class SmokeApplication : Application
    {
        public override void Initialize()
        {
            Styles.Add(new FluentTheme());
            Styles.Add(new StyleInclude(new Uri("avares://ProPDF.Avalonia/")) { Source = new Uri("avares://ProPDF.Avalonia/Themes/PdfViewer.axaml") });
        }
    }
}
