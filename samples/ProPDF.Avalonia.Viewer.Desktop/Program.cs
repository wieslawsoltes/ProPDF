using Avalonia;
using ProPDF.Avalonia.Viewer;

namespace ProPDF.Avalonia.Viewer.Desktop;

internal static class Program
{
    [STAThread]
    public static void Main(string[] args)
    {
        ViewerApplication.CreatePrintService = () => new DesktopPdfPrintHandoff();
        BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
    }
    public static AppBuilder BuildAvaloniaApp() => AppBuilder.Configure<ViewerApplication>().UsePlatformDetect().WithInterFont().LogToTrace();
}
