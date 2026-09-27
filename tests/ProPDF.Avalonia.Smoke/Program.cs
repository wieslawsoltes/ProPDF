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
using ProPDF.Core;
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
            tabs.SelectedItem = tabs.Items.OfType<TabItem>().Single(tab => tab.Content is PdfContentPanel);
            Pump(workspace.EditObjectsCommand.ExecuteAsync());
            PumpUntil(() => editor.GetVisualDescendants().OfType<PdfContentPanel>().Any());
            var editButton = editor.GetVisualDescendants().OfType<Button>().Single(button => button.Name == "ApplyObjectBoundsButton");
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
            var contentPanel = (PdfContentPanel)((TabItem)tabs.SelectedItem!).Content!;
            // Resolve within this control's namescope. Expander content can occur more than once in the logical walk.
            var appearanceButton = contentPanel.FindControl<Button>("ApplyAppearanceButton")
                ?? throw new InvalidOperationException("Appearance button is missing from the content panel.");
            var replacementButton = contentPanel.FindControl<Button>("ReplaceImageButton")
                ?? throw new InvalidOperationException("Image replacement button is missing from the content panel.");
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
            var objectList = contentPanel.FindControl<ListBox>("ContentObjectsList")!;
            PumpUntil(() => objectList.SelectedItems!.Count == 1);
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
            contentPanel.FindControl<Expander>("AlignmentSection")!.IsExpanded = true;
            Dispatcher.UIThread.RunJobs();
            var alignmentChoice = contentPanel.FindControl<ComboBox>("ContentAlignmentReferenceChoice")!;
            var alignmentButton = contentPanel.FindControl<Button>("AlignObjectsButton")!;
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
            if (runtime.Viewport.SearchHits.Count == 0) throw new InvalidOperationException("The search command found no sample text.");
            workspace.SearchQuery = "";
            Pump(workspace.SearchCommand.ExecuteAsync());
            runtime.Viewport.GoToPage(1);
            runtime.Viewport.FitWidth();
            Pump(runtime.Viewport.WaitForRenderingAsync());
            PumpUntil(() => !runtime.Viewport.IsRendering);
            using (var scene = runtime.Viewport.CaptureScene())
                if (scene.TileCount == 0) throw new InvalidOperationException("No real Skia PDF tiles were produced.");
            if (runtime.Viewport.Tool != ProPDF.Presentation.PdfTool.EditObject) throw new InvalidOperationException("Native selector reset the active tool after viewport updates.");
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
            Console.WriteLine($"PASS: Avalonia real-Skia editor, native single/multi-object editing and output/navigation bindings, bookmarks/history, search, undo and teardown. Screenshot: {path}");
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
