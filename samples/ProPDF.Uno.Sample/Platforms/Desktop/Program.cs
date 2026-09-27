using Uno.UI.Hosting;
namespace ProPDF.Uno.Sample;
internal static class Program
{
    [STAThread]
    public static void Main(string[] args)
    {
        if (!OperatingSystem.IsMacOS())
            throw new PlatformNotSupportedException("This permissive-only native Uno sample currently uses the macOS host. Use the Uno browser app on Windows/Linux, or the existing WPF/Avalonia native editors. Stock Uno Win32/X11 packages introduce non-permissive SDK metadata/video dependencies and are not included.");
        UnoPlatformHostBuilder.Create().App(() => new App()).UseMacOS().Build().Run();
    }
}
