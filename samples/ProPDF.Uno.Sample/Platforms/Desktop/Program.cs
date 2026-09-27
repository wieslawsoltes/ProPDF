using Uno.UI.Hosting;
namespace ProPDF.Uno.Sample;
internal static class Program
{
    [STAThread]
    public static void Main(string[] args)
    {
        if (!OperatingSystem.IsWindows() && !OperatingSystem.IsMacOS())
            throw new PlatformNotSupportedException("The permissive-only Uno desktop sample currently supports Windows and macOS. On Linux use the Uno browser sample or the Avalonia desktop editor; Uno's stock X11 host brings an LGPL video dependency and is intentionally not included.");
        UnoPlatformHostBuilder.Create().App(() => new App()).UseMacOS().UseWin32().Build().Run();
    }
}
