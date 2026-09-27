using ProPDF.Core;
using ProPDF.Editing;
using ProPDF.Engine.PdfPig;
using ProPDF.Presentation;
using ProPDF.Rendering.Skia;

namespace ProPDF.SampleSupport;

/// <summary>Shared application composition and generated sample document. No confidential PDFs, fonts or keys are bundled.</summary>
public sealed class DemoWorkspace : IAsyncDisposable
{
    public DemoWorkspace(Action<Action>? dispatch = null)
    {
        Backend = new PdfPigBackend();
        Editor = new ManagedPdfEditor(Backend);
        Session = new PdfSession(Backend, Editor);
        Renderer = new SkiaPdfRenderer(Backend);
        Viewport = new PdfViewportController(Session, Renderer, Backend, dispatch);
        Context = new PdfEditorContext(Viewport, Editor, token => Editor.CreateAsync(cancellationToken: token), Backend);
    }
    public PdfPigBackend Backend { get; }
    public ManagedPdfEditor Editor { get; }
    public PdfSession Session { get; }
    public SkiaPdfRenderer Renderer { get; }
    public PdfViewportController Viewport { get; }
    public PdfEditorContext Context { get; }

    public async Task LoadWelcomeAsync(CancellationToken cancellationToken = default)
    {
        var document = await Editor.CreateAsync(3, cancellationToken: cancellationToken).ConfigureAwait(false);
        var navy = new PdfColor(24, 43, 79);
        var blue = new PdfColor(49, 92, 231);
        var muted = new PdfColor(90, 106, 132);
        var operations = new List<IPdfEditOperation>
        {
            new SetDocumentMetadata(new PdfMetadata("Welcome to ProPDF", "ProPDF", "A native PDF editing workspace", "SkiaSharp, Avalonia, WPF")),
            new AddShape(1, new PdfRect(32, 38, 530, 180), Fill: navy, Stroke: navy),
            new AddText(1, new PdfPoint(56, 80), "ProPDF", 16, "Helvetica-Bold", PdfColor.White),
            new AddText(1, new PdfPoint(56, 132), "Your documents.", 34, "Helvetica-Bold", PdfColor.White),
            new AddText(1, new PdfPoint(56, 174), "Your workspace.", 34, "Helvetica-Bold", PdfColor.White),
            new AddText(1, new PdfPoint(40, 263), "A shared engine. Two native experiences.", 21, "Helvetica-Bold", navy),
            new AddText(1, new PdfPoint(40, 295), "Edit real PDF content with Avalonia and WPF controls.", 12, Color: muted),
            new AddText(1, new PdfPoint(40, 314), "The same document, transaction and rendering libraries power both.", 12, Color: muted),
            new AddShape(1, new PdfRect(40, 355, 245, 110), Fill: new PdfColor(238, 243, 255), Stroke: new PdfColor(219, 228, 249)),
            new AddShape(1, new PdfRect(307, 355, 245, 110), Fill: new PdfColor(241, 245, 250), Stroke: new PdfColor(219, 228, 239)),
            new AddText(1, new PdfPoint(58, 384), "READ & REVIEW", 11, "Helvetica-Bold", blue),
            new AddText(1, new PdfPoint(58, 410), "Search, select and annotate.", 12, Color: navy),
            new AddText(1, new PdfPoint(58, 433), "Zoom into only the tiles you need.", 11, Color: muted),
            new AddText(1, new PdfPoint(325, 384), "EDIT & ORGANIZE", 11, "Helvetica-Bold", blue),
            new AddText(1, new PdfPoint(325, 410), "Insert content. Reorder pages.", 12, Color: navy),
            new AddText(1, new PdfPoint(325, 433), "Undo safely before saving a copy.", 11, Color: muted),
            new AddText(1, new PdfPoint(40, 521), "Start exploring", 19, "Helvetica-Bold", navy),
            new AddText(1, new PdfPoint(40, 555), "1   Choose a tool in the main toolbar.", 13, Color: navy),
            new AddText(1, new PdfPoint(40, 584), "2   Drag a region on this page to insert or review content.", 13, Color: navy),
            new AddText(1, new PdfPoint(40, 613), "3   Use the inspector for comments, forms and document details.", 13, Color: navy),
            new AddText(1, new PdfPoint(40, 642), "4   Save a copy when you are ready. No cloud upload is performed.", 13, Color: navy),
            new AddText(1, new PdfPoint(40, 728), "ALPHA SOFTWARE", 10, "Helvetica-Bold", muted),
            new AddText(1, new PdfPoint(40, 750), "Not a complete Acrobat replacement or a certified redaction tool.", 10, Color: muted),
            new AddText(1, new PdfPoint(40, 768), "See the feature matrix and licensing guide before production use.", 10, Color: muted),
            new AddText(2, new PdfPoint(40, 66), "Review and form playground", 26, "Helvetica-Bold", navy),
            new AddText(2, new PdfPoint(40, 102), "Select the Name field in the Forms inspector and change its value.", 12, Color: muted),
            new AddText(2, new PdfPoint(40, 149), "Name", 12, "Helvetica-Bold", navy),
            new AddFormField(2, new PdfRect(40, 162, 330, 38), "Name", value: "ProPDF contributor"),
            new AddText(2, new PdfPoint(40, 245), "A native comment is attached to this document.", 13, Color: navy),
            new AddAnnotation(2, new PdfRect(390, 227, 28, 28), PdfAnnotationKind.Note, "Review this sample, then add your own annotation.", "ProPDF"),
            new AddText(2, new PdfPoint(40, 301), "Redaction is a two-step operation", 18, "Helvetica-Bold", navy),
            new AddText(2, new PdfPoint(40, 333), "Mark a region, inspect the marks, then explicitly apply the removal.", 12, Color: muted),
            new AddText(2, new PdfPoint(40, 357), "Unapplied marks are never mistaken for redacted PDF content.", 12, Color: muted),
            new AddText(3, new PdfPoint(40, 66), "Built to be reused", 28, "Helvetica-Bold", navy),
            new AddText(3, new PdfPoint(40, 114), "Core / Rendering.Skia / Engine.PdfPig", 16, "Helvetica-Bold", blue),
            new AddText(3, new PdfPoint(40, 144), "Snapshots, transactions, layout, parsing, search and tiled rendering.", 12, Color: muted),
            new AddText(3, new PdfPoint(40, 204), "Presentation / Avalonia / Wpf", 16, "Helvetica-Bold", blue),
            new AddText(3, new PdfPoint(40, 234), "One workspace model, with native controls and platform dialogs.", 12, Color: muted),
            new AddText(3, new PdfPoint(40, 294), "Kernel / Editing", 16, "Helvetica-Bold", blue),
            new AddText(3, new PdfPoint(40, 324), "Owned PDF objects, native editing, encryption and signature handling.", 12, Color: muted),
            new AddText(3, new PdfPoint(40, 386), "ProPDF-authored libraries use the MIT license.", 12, Color: navy),
            new AddText(3, new PdfPoint(40, 410), "Dependencies are permissive. No commercial PDF license is required.", 12, Color: navy),
            new AddBookmark("Welcome", 1), new AddBookmark("Review and forms", 2), new AddBookmark("Architecture", 3)
        };
        document = await Editor.ApplyAsync(document, operations, cancellationToken).ConfigureAwait(false);
        using var input = document.OpenRead();
        await Session.OpenAsync(input, cancellationToken: cancellationToken).ConfigureAwait(false);
        Viewport.GoToPage(1);
        Viewport.FitWidth();
    }

    public async ValueTask DisposeAsync()
    {
        await Viewport.DisposeAsync().ConfigureAwait(false);
        await Renderer.DisposeAsync().ConfigureAwait(false);
    }
}
