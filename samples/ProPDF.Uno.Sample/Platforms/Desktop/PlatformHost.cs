using ProPDF.SampleSupport;
namespace ProPDF.Uno.Sample;
internal static class PlatformHost
{
    public static Task InitializeAsync() => Task.CompletedTask;
    public static Task<ProPDF.Engine.PdfPig.PdfFontCatalog?> LoadFontsAsync() => Task.FromResult<ProPDF.Engine.PdfPig.PdfFontCatalog?>(null);
    public static IPdfUnoFiles CreateFiles() => new PdfUnoFiles();
    public static void SetDirty(bool dirty) { }
    public static Task ReadyAsync(PdfEditor editor, DemoWorkspace runtime) => Task.CompletedTask;
    public static void Failed(string error) => Console.Error.WriteLine(error);
}
