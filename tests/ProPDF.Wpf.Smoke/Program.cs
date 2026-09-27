using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using ProPDF.SampleSupport;
using ProPDF.Core;
using SkiaSharp;

namespace ProPDF.Wpf.Smoke;

internal static class Program
{
    [STAThread]
    public static int Main(string[] args)
    {
        try
        {
            RenderOptions.ProcessRenderMode = System.Windows.Interop.RenderMode.SoftwareOnly;
            var application = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
            var runtime = new DemoWorkspace(action => application.Dispatcher.BeginInvoke(action));
            var editor = new PdfEditor { Context = runtime.Context };
            var window = new Window { Width = 1450, Height = 960, Content = editor, ShowInTaskbar = false };
            window.Show();
            Pump(runtime.LoadWelcomeAsync());
            Pump(runtime.Viewport.WaitForRenderingAsync());
            var workspace = editor.Workspace ?? throw new InvalidOperationException("WPF editor workspace was not constructed.");
            if (runtime.Viewport.PageCount != 3) throw new InvalidOperationException("The sample PDF did not load.");
            Pump(workspace.RotateCommand.ExecuteAsync());
            if (runtime.Session.Current!.Pages[0].Size.Width < runtime.Session.Current.Pages[0].Size.Height)
                throw new InvalidOperationException("WPF rotate command did not update the document.");
            Pump(workspace.UndoCommand.ExecuteAsync());
            var tabs = Descendants(editor).OfType<TabControl>().Single();
            tabs.SelectedItem = tabs.Items.OfType<TabItem>().Single(tab => tab.Content is PdfNavigationPanel);
            Pump(workspace.LoadNavigationAsync());
            if (workspace.NavigationBookmarks.Count != 3) throw new InvalidOperationException("Native bookmark inspector did not load.");
            workspace.SelectedBookmark = workspace.NavigationBookmarks[1];
            Pump(workspace.FollowBookmarkCommand.ExecuteAsync());
            if (runtime.Viewport.CurrentPage != 2) throw new InvalidOperationException("WPF bookmark navigation did not reach page two.");
            Pump(workspace.BackCommand.ExecuteAsync());
            if (runtime.Viewport.CurrentPage != 1) throw new InvalidOperationException("WPF navigation history failed.");
            application.Dispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);
            var follow = Descendants(editor).OfType<Button>().Single(button => button.Name == "FollowBookmarkButton");
            if (!ReferenceEquals(follow.Command, workspace.FollowBookmarkCommand)) throw new InvalidOperationException("WPF navigation command binding is missing.");
            var export = Descendants(editor).OfType<Button>().Single(button => button.Name == "ExportPngButton");
            if (!ReferenceEquals(export.Command, workspace.ExportPngCommand)) throw new InvalidOperationException("WPF output command binding is missing.");
            tabs.SelectedItem = tabs.Items.OfType<TabItem>().Single(tab => tab.Content is PdfContentPanel);
            Pump(workspace.EditObjectsCommand.ExecuteAsync());
            application.Dispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);
            var editButton = Descendants(editor).OfType<Button>().Single(button => button.Name == "ApplyObjectBoundsButton");
            if (!ReferenceEquals(editButton.Command, workspace.ApplyObjectBoundsCommand)) throw new InvalidOperationException("Native object-edit command binding is missing.");
            var originalObject = workspace.ContentObjects.Single(item => item.Kind == PdfContentObjectKind.Text && item.Text == "Your documents.");
            workspace.SelectedContentObject = originalObject;
            workspace.ContentX = (originalObject.Bounds.X + 12).ToString(System.Globalization.CultureInfo.InvariantCulture);
            Pump(workspace.ApplyObjectBoundsCommand.ExecuteAsync());
            var inspection = runtime.Editor.ReadPageContentAsync(runtime.Session.Current!, 1);
            Pump(inspection);
            var movedObject = inspection.Result.Objects.Single(item => item.Kind == PdfContentObjectKind.Text && item.Text == "Your documents.");
            if (Math.Abs(movedObject.Bounds.X - originalObject.Bounds.X - 12) > .01) throw new InvalidOperationException("Bound content-edit command did not move native PDF text.");
            Pump(workspace.UndoCommand.ExecuteAsync());
            Pump(runtime.Viewport.LoadContentAsync());
            workspace.SelectedContentObject = workspace.ContentObjects.Single(item => item.Kind == PdfContentObjectKind.Text && item.Text == "Your documents.");
            workspace.SearchQuery = "workspace";
            Pump(workspace.SearchCommand.ExecuteAsync());
            if (runtime.Viewport.SearchHits.Count == 0) throw new InvalidOperationException("WPF search command failed.");
            workspace.SearchQuery = "";
            Pump(workspace.SearchCommand.ExecuteAsync());
            runtime.Viewport.GoToPage(1);
            runtime.Viewport.FitWidth();
            Pump(runtime.Viewport.WaitForRenderingAsync());
            application.Dispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);
            window.UpdateLayout();
            if (runtime.Viewport.LastError is { } error) throw new InvalidOperationException(error);
            using (var scene = runtime.Viewport.CaptureScene())
                if (scene.TileCount == 0) throw new InvalidOperationException("No PDF tiles rendered.");
            var bitmap = new RenderTargetBitmap((int)editor.ActualWidth, (int)editor.ActualHeight, 96, 96, PixelFormats.Pbgra32);
            bitmap.Render(editor);
            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(bitmap));
            var path = args.Length > 0 ? args[0] : "artifacts/headless/wpf.png";
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
            using (var output = File.Create(path)) encoder.Save(output);
            using (var pixels = SKBitmap.Decode(path))
            {
                var colored = 0;
                for (var y = 160; y < pixels.Height - 40; y += 4)
                    for (var x = 220; x < pixels.Width - 300; x += 4)
                    {
                        var color = pixels.GetPixel(x, y);
                        if (color.Blue > color.Red + 25 && color.Blue > color.Green + 15 && color.Red < 80) colored++;
                    }
                if (colored < 200) throw new InvalidOperationException("WPF screenshot is missing its expected PDF content.");
            }
            window.Content = null;
            window.Close();
            Pump(runtime.DisposeAsync().AsTask());
            application.Shutdown();
            Console.WriteLine($"PASS: WPF software-Skia editor, native content-edit/output/navigation bindings, bookmarks/history, search, undo and teardown. Screenshot: {path}");
            return 0;
        }
        catch (Exception error) { Console.Error.WriteLine(error); return 1; }
    }
    private static IEnumerable<DependencyObject> Descendants(DependencyObject root)
    {
        var stack = new Stack<DependencyObject>(); stack.Push(root);
        while (stack.TryPop(out var current))
            foreach (var child in LogicalTreeHelper.GetChildren(current).OfType<DependencyObject>())
            {
                yield return child;
                stack.Push(child);
            }
    }
    private static void Pump(Task task)
    {
        var watch = Stopwatch.StartNew();
        while (!task.IsCompleted)
        {
            var frame = new DispatcherFrame();
            Dispatcher.CurrentDispatcher.BeginInvoke(DispatcherPriority.Background, new Action(() => frame.Continue = false));
            Dispatcher.PushFrame(frame);
            if (watch.Elapsed > TimeSpan.FromSeconds(45)) throw new TimeoutException("WPF UI operation exceeded 45 seconds.");
            Thread.Sleep(1);
        }
        task.GetAwaiter().GetResult();
        Dispatcher.CurrentDispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);
    }
}
