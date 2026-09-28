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
            workspace.Shell.ShowSection(ProPDF.Presentation.PdfShellSection.Edit);
            Pump(Task.CompletedTask);
            var tabs = Descendants(editor).OfType<TabControl>().Single();
            workspace.Shell.ShowSection(ProPDF.Presentation.PdfShellSection.Navigate);
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
            workspace.Shell.ShowSection(ProPDF.Presentation.PdfShellSection.Export);
            Pump(Task.CompletedTask);
            var export = Descendants(editor).OfType<Button>().Single(button => button.Name == "ExportPngButton");
            if (!ReferenceEquals(export.Command, workspace.ExportPngCommand)) throw new InvalidOperationException("WPF output command binding is missing.");
            workspace.Shell.ShowSection(ProPDF.Presentation.PdfShellSection.Edit);
            Pump(Task.CompletedTask);
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
            // Exercise actual two-way typography controls and the bound native PDF edit command.
            workspace.SelectedContentObject = workspace.ContentObjects.Single(item => item.Text == "Your documents.");
            ((Expander)contentPanel.FindName("TextSection")).IsExpanded = true;
            application.Dispatcher.Invoke(() => { }, System.Windows.Threading.DispatcherPriority.ContextIdle);
            var faceChoice = ((ComboBox)contentPanel.FindName("ContentStandardFontChoice"));
            var lineSpacingInput = ((TextBox)contentPanel.FindName("ContentLineSpacingInput"));
            var textReplaceButton = ((Button)contentPanel.FindName("ReplaceObjectTextButton"));
            faceChoice.SelectedItem = "Courier-Bold";
            lineSpacingInput.Text = "1.6";
            application.Dispatcher.Invoke(() => { }, System.Windows.Threading.DispatcherPriority.ContextIdle);
            if (workspace.ContentStandardFont != "Courier-Bold" || workspace.ContentLineSpacing != "1.6" ||
                !ReferenceEquals(textReplaceButton.Command, workspace.ReplaceObjectTextCommand))
                throw new InvalidOperationException("Native typography controls are not bound.");
            workspace.ContentText = "First row\nSecond row";
            workspace.ContentFontSize = "18"; workspace.ContentWidth = "300"; workspace.ContentHeight = "100";
            var typographyRevision = runtime.Session.Current!;
            Pump(((ProPDF.Presentation.PdfUiCommand)textReplaceButton.Command!).ExecuteAsync());
            if (runtime.Session.Current!.Id == typographyRevision.Id) throw new InvalidOperationException("Typography replacement did not publish.");
            using (var typographyInput = runtime.Session.Current.OpenRead())
            using (var independent = UglyToad.PdfPig.PdfDocument.Open(typographyInput))
            {
                var letters = independent.GetPage(1).Letters.Where(l => l.FontName == "Courier-Bold").ToArray();
                if (string.Concat(letters.Select(l => l.Value)) != "First rowSecond row" ||
                    Math.Abs(Math.Abs(letters[0].StartBaseLine.Y - letters[9].StartBaseLine.Y) - 28.8) > .01)
                    throw new InvalidOperationException("Native typography controls did not write the requested face and line spacing.");
            }
            Pump(workspace.UndoCommand.ExecuteAsync());
            if (!ReferenceEquals(typographyRevision, runtime.Session.Current)) throw new InvalidOperationException("Typography undo was not atomic.");
            workspace.ContentFontSize = "14"; faceChoice.SelectedItem = "Helvetica"; lineSpacingInput.Text = "1.2";
            ((Expander)contentPanel.FindName("TextSection")).IsExpanded = false;
            Pump(runtime.Viewport.LoadContentAsync());
            workspace.SelectContentObjects(workspace.ContentObjects.Where(item => item.Text is "Your documents." or "Your workspace."));
            var chromeRevision = runtime.Session.Current!.Id;
            var closeTools = Descendants(editor).OfType<Button>().Single(b => b.Name == "CloseToolsButton");
            closeTools.Command!.Execute(closeTools.CommandParameter);
            if (workspace.Shell.ToolsVisible) throw new InvalidOperationException("Close tools is not wired.");
            var allTools = Descendants(editor).OfType<Button>().Single(b => b.Name == "AllToolsButton");
            allTools.Command!.Execute(allTools.CommandParameter);
            Pump(Task.CompletedTask);
            var formsTool = Descendants(editor).OfType<Button>().Single(b => b.Name == "ToolFormsButton");
            formsTool.Command!.Execute(formsTool.CommandParameter);
            if (workspace.Shell.Section != ProPDF.Presentation.PdfShellSection.Forms) throw new InvalidOperationException("Native task catalog is not wired.");
            var pagesToggle = Descendants(editor).OfType<Button>().Single(b => b.Name == "PagesPaneButton");
            pagesToggle.Command!.Execute(pagesToggle.CommandParameter);
            if (!workspace.Shell.PagesVisible) throw new InvalidOperationException("Native page rail is not wired.");
            pagesToggle.Command.Execute(pagesToggle.CommandParameter);
            var searchToggle = Descendants(editor).OfType<Button>().Single(b => b.Name == "FindPaneButton");
            searchToggle.Command!.Execute(searchToggle.CommandParameter);
            if (!workspace.Shell.IsSearchOpen) throw new InvalidOperationException("Native search button is not wired.");
            searchToggle.Command.Execute(searchToggle.CommandParameter);
            allTools.Command.Execute(allTools.CommandParameter);
            Pump(Task.CompletedTask);
            if (runtime.Session.Current!.Id != chromeRevision) throw new InvalidOperationException("Shell navigation edited the PDF.");
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
            // Exercise the actual native resize path. Drawers must overlay at compact sizes.
            window.Width = 720;
            closeTools.Command.Execute(closeTools.CommandParameter);
            Pump(Task.CompletedTask);
            window.UpdateLayout();
            if (!workspace.Shell.IsCompact) throw new InvalidOperationException("Native width did not reach responsive shell state.");
            var compactWidth = editor.View.ActualWidth;
            pagesToggle.Command.Execute(pagesToggle.CommandParameter);
            Pump(Task.CompletedTask);
            window.UpdateLayout();
            if (!workspace.Shell.PagesVisible || Math.Abs(editor.View.ActualWidth - compactWidth) > 1)
                throw new InvalidOperationException("Compact page drawer squeezed the document.");
            pagesToggle.Command.Execute(pagesToggle.CommandParameter);
            Pump(Task.CompletedTask);
            runtime.Viewport.FitPage(); Pump(runtime.Viewport.WaitForRenderingAsync());
            window.UpdateLayout();
            var compactBitmap = new RenderTargetBitmap((int)editor.ActualWidth, (int)editor.ActualHeight, 96, 96, PixelFormats.Pbgra32);
            compactBitmap.Render(editor);
            var compactEncoder = new PngBitmapEncoder(); compactEncoder.Frames.Add(BitmapFrame.Create(compactBitmap));
            using (var output = File.Create(Path.Combine(Path.GetDirectoryName(path)!, "wpf-compact.png"))) compactEncoder.Save(output);
            if (runtime.Session.Current!.Id != chromeRevision) throw new InvalidOperationException("Native resizing changed the PDF revision.");
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
