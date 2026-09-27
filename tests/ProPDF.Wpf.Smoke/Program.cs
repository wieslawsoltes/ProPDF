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
            if (runtime.Viewport.Tool != ProPDF.Presentation.PdfTool.EditObject) throw new InvalidOperationException("Native selector reset the content-edit tool.");
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
            var appearanceButton = (Button)((PdfContentPanel)tabs.SelectedContent).FindName("ApplyAppearanceButton");
            var replacementButton = (Button)((PdfContentPanel)tabs.SelectedContent).FindName("ReplaceImageButton");
            if (!ReferenceEquals(appearanceButton.Command, workspace.ApplyAppearanceCommand) ||
                !ReferenceEquals(replacementButton.Command, workspace.ReplaceImageCommand))
                throw new InvalidOperationException("Native appearance/image commands are not bound.");
            workspace.ContentFillColor = "#D040A0";
            var appearanceRevision = runtime.Session.Current!.Id;
            Pump(((ProPDF.Presentation.PdfUiCommand)appearanceButton.Command!).ExecuteAsync());
            if (runtime.Session.Current!.Id == appearanceRevision) throw new InvalidOperationException("Bound appearance command did not edit native content.");
            var appearanceImage = new ProPDF.Rendering.Skia.PdfRasterExporter(runtime.Renderer)
                .RenderPageAsync(runtime.Session.Current, 1, new(Dpi: 72));
            Pump(appearanceImage);
            using (var styled = appearanceImage.Result)
            using (var pixels = SKBitmap.FromImage(styled))
            {
                var magenta = 0;
                for (var y = 0; y < pixels.Height; y++) for (var x = 0; x < pixels.Width; x++)
                {
                    var pixel = pixels.GetPixel(x, y);
                    if (Math.Abs(pixel.Red - 208) < 3 && Math.Abs(pixel.Green - 64) < 3 && Math.Abs(pixel.Blue - 160) < 3) magenta++;
                }
                if (magenta < 100) throw new InvalidOperationException("Native appearance edit did not render the changed text color.");
            }
            Pump(workspace.UndoCommand.ExecuteAsync());
            Pump(runtime.Viewport.LoadContentAsync());
            workspace.SelectedContentObject = workspace.ContentObjects.Single(item => item.Kind == PdfContentObjectKind.Text && item.Text == "Your documents.");
            var firstMember = workspace.SelectedContentObject!;
            var secondMember = workspace.ContentObjects.Single(item => item.Kind == PdfContentObjectKind.Text && item.Text == "Your workspace.");
            var objectList = (ListBox)((PdfContentPanel)tabs.SelectedContent).FindName("ContentObjectsList");
            application.Dispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);
            objectList.SelectedItems!.Add(secondMember); // Exercise the native list SelectionChanged path.
            if (workspace.SelectedContentObjects.Count != 2) throw new InvalidOperationException("Native list multi-selection did not reach the shared viewport.");
            if (workspace.ApplyAppearanceCommand.CanExecute(null)) throw new InvalidOperationException("Single-object appearance unexpectedly accepts a group.");
            workspace.ContentX = (PdfContentSelection.Bounds(workspace.SelectedContentObjects).X + 8).ToString(System.Globalization.CultureInfo.InvariantCulture);
            Pump(((ProPDF.Presentation.PdfUiCommand)editButton.Command!).ExecuteAsync());
            var groupInspection = runtime.Editor.ReadPageContentAsync(runtime.Session.Current!, 1);
            Pump(groupInspection);
            foreach (var member in new[] { firstMember, secondMember })
            {
                var movedMember = groupInspection.Result.Objects.Single(item => item.Kind == PdfContentObjectKind.Text && item.Text == member.Text);
                if (Math.Abs(movedMember.Bounds.X - member.Bounds.X - 8) > .01) throw new InvalidOperationException("Native group move did not transform both selected text objects.");
            }
            Pump(workspace.UndoCommand.ExecuteAsync());
            Pump(runtime.Viewport.LoadContentAsync());
            workspace.SelectContentObjects(workspace.ContentObjects.Where(item => item.Kind == PdfContentObjectKind.Text && item.Text is "Your documents." or "Your workspace."));
            var contentPanel = (PdfContentPanel)tabs.SelectedContent;
            ((Expander)contentPanel.FindName("AlignmentSection")).IsExpanded = true;
            application.Dispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);
            var alignmentChoice = (ComboBox)contentPanel.FindName("ContentAlignmentReferenceChoice");
            var alignmentButton = (Button)contentPanel.FindName("AlignObjectsButton");
            alignmentChoice.SelectedItem = ProPDF.Presentation.PdfContentAlignmentReference.Page;
            if (workspace.ContentAlignmentReference != ProPDF.Presentation.PdfContentAlignmentReference.Page)
                throw new InvalidOperationException("Native page-alignment reference is not bound.");
            var alignmentRevision = runtime.Session.Current!;
            workspace.ContentAlignment = PdfSelectionAlignment.HorizontalCenter;
            Pump(((ProPDF.Presentation.PdfUiCommand)alignmentButton.Command!).ExecuteAsync());
            var alignedInspection = runtime.Editor.ReadPageContentAsync(runtime.Session.Current!, 1); Pump(alignedInspection);
            var alignedBounds = PdfContentSelection.Bounds(alignedInspection.Result.Objects.Where(item => item.Text is "Your documents." or "Your workspace."));
            if (Math.Abs(alignedBounds.X + alignedBounds.Width / 2 - alignmentRevision.GetPage(1).Size.Width / 2) > .01)
                throw new InvalidOperationException("Native page alignment did not center the PDF content.");
            Pump(workspace.UndoCommand.ExecuteAsync());
            if (!ReferenceEquals(runtime.Session.Current, alignmentRevision)) throw new InvalidOperationException("Alignment was not one undoable edit.");
            Pump(runtime.Viewport.LoadContentAsync());
            workspace.SelectContentObjects(workspace.ContentObjects.Where(item => item.Kind == PdfContentObjectKind.Text && item.Text is "Your documents." or "Your workspace."));
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
            if (runtime.Viewport.Tool != ProPDF.Presentation.PdfTool.EditObject) throw new InvalidOperationException("Native selector reset the active tool after viewport updates.");
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
            Console.WriteLine($"PASS: WPF software-Skia editor, native single/multi-object editing and output/navigation bindings, bookmarks/history, search, undo and teardown. Screenshot: {path}");
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
