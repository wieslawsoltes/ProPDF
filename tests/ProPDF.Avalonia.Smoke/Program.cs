using System.Diagnostics;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.LogicalTree;
using Avalonia.Media.Imaging;
using Avalonia.Styling;
using Avalonia.Themes.Fluent;
using Avalonia.Threading;
using Avalonia.VisualTree;
using ProPDF.SampleSupport;
using SkiaSharp;

namespace ProPDF.Avalonia.Smoke;

internal static class Program
{
    [STAThread]
    public static int Main(string[] args)
    {
        try
        {
            AppBuilder.Configure<SmokeApplication>().UseSkia()
                .UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = false }).SetupWithoutStarting();
            var runtime = new DemoWorkspace(action => Dispatcher.UIThread.Post(action));
            var editor = new PdfEditor { Context = runtime.Context };
            var window = new Window { Width = 1450, Height = 960, Content = editor };
            window.Show();
            Pump(runtime.LoadWelcomeAsync());
            Pump(runtime.Viewport.WaitForRenderingAsync());
            PumpUntil(() => editor.GetVisualDescendants().OfType<PdfView>().Any(view => ReferenceEquals(view.Controller, runtime.Viewport)));
            if (runtime.Viewport.PageCount != 3) throw new InvalidOperationException("Sample PDF did not load.");
            var workspace = editor.Workspace ?? throw new InvalidOperationException("The editor did not create its workspace.");
            Pump(workspace.RotateCommand.ExecuteAsync());
            if (runtime.Session.Current!.Pages[0].Size.Width < runtime.Session.Current.Pages[0].Size.Height)
                throw new InvalidOperationException("The editor rotate command did not reach the PDF engine.");
            Pump(workspace.UndoCommand.ExecuteAsync());
            var tabs = editor.GetLogicalDescendants().OfType<TabControl>().Single();
            var navigationTab = tabs.Items.OfType<TabItem>().Single(tab => tab.Content is PdfNavigationPanel);
            tabs.SelectedItem = navigationTab;
            Pump(workspace.LoadNavigationAsync());
            PumpUntil(() => workspace.NavigationBookmarks.Count == 3);
            workspace.SelectedBookmark = workspace.NavigationBookmarks[1];
            Pump(workspace.FollowBookmarkCommand.ExecuteAsync());
            if (runtime.Viewport.CurrentPage != 2) throw new InvalidOperationException("Bookmark navigation did not reach page two.");
            Pump(workspace.BackCommand.ExecuteAsync());
            if (runtime.Viewport.CurrentPage != 1) throw new InvalidOperationException("Navigation history did not restore page one.");
            PumpUntil(() => editor.GetVisualDescendants().OfType<PdfNavigationPanel>().Any());
            var followButton = editor.GetVisualDescendants().OfType<Button>().Single(button => button.Name == "FollowBookmarkButton");
            if (!ReferenceEquals(followButton.Command, workspace.FollowBookmarkCommand)) throw new InvalidOperationException("Native bookmark command binding is missing.");
            var exportButton = editor.GetVisualDescendants().OfType<Button>().Single(button => button.Name == "ExportPngButton");
            if (!ReferenceEquals(exportButton.Command, workspace.ExportPngCommand)) throw new InvalidOperationException("Native image export command binding is missing.");
            workspace.SearchQuery = "workspace";
            Pump(workspace.SearchCommand.ExecuteAsync());
            if (runtime.Viewport.SearchHits.Count == 0) throw new InvalidOperationException("The search command found no sample text.");
            workspace.SearchQuery = "";
            Pump(workspace.SearchCommand.ExecuteAsync());
            runtime.Viewport.GoToPage(1);
            runtime.Viewport.FitWidth();
            Pump(runtime.Viewport.WaitForRenderingAsync());
            PumpUntil(() => !runtime.Viewport.IsRendering);
            using (var scene = runtime.Viewport.CaptureScene())
                if (scene.TileCount == 0) throw new InvalidOperationException("No real Skia PDF tiles were produced.");
            if (runtime.Viewport.LastError is { } error) throw new InvalidOperationException(error);
            Dispatcher.UIThread.RunJobs();
            AvaloniaHeadlessPlatform.ForceRenderTimerTick();
            using var frame = window.CaptureRenderedFrame() ?? throw new InvalidOperationException("Avalonia produced no rendered frame.");
            var path = args.Length > 0 ? args[0] : "artifacts/headless/avalonia.png";
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
            frame.Save(path, PngBitmapEncoderOptions.Default);
            using (var bitmap = SKBitmap.Decode(path))
            {
                if (bitmap.Width < 1000 || bitmap.Height < 600) throw new InvalidOperationException("Unexpected UI screenshot dimensions.");
                var bluePixels = 0;
                for (var y = 160; y < bitmap.Height - 40; y += 4)
                    for (var x = 220; x < bitmap.Width - 300; x += 4)
                    {
                        var pixel = bitmap.GetPixel(x, y);
                        if (pixel.Blue > pixel.Red + 25 && pixel.Blue > pixel.Green + 15 && pixel.Red < 80) bluePixels++;
                    }
                if (bluePixels < 200) throw new InvalidOperationException("The PDF canvas appears blank; sample blue content was not rendered.");
            }
            window.Content = null;
            window.Close();
            Pump(runtime.DisposeAsync().AsTask());
            Console.WriteLine($"PASS: Avalonia real-Skia editor, native output/navigation bindings, bookmarks/history, search, undo and teardown. Screenshot: {path}");
            return 0;
        }
        catch (Exception error) { Console.Error.WriteLine(error); return 1; }
    }
    private static void Pump(Task task)
    {
        PumpUntil(() => task.IsCompleted);
        task.GetAwaiter().GetResult();
    }
    private static void PumpUntil(Func<bool> condition)
    {
        var watch = Stopwatch.StartNew();
        do
        {
            Dispatcher.UIThread.RunJobs();
            AvaloniaHeadlessPlatform.ForceRenderTimerTick();
            if (watch.Elapsed > TimeSpan.FromSeconds(45)) throw new TimeoutException("Headless UI operation exceeded 45 seconds.");
            if (!condition()) Thread.Sleep(1);
        } while (!condition());
        Dispatcher.UIThread.RunJobs();
        AvaloniaHeadlessPlatform.ForceRenderTimerTick();
    }
}

public sealed class SmokeApplication : Application
{
    public override void Initialize() { Styles.Add(new FluentTheme()); RequestedThemeVariant = ThemeVariant.Light; }
}
