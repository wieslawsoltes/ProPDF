using Uno.UI.Hosting;
namespace ProPDF.Uno.Sample;
internal static class Program
{
    [STAThread]
    public static void Main(string[] args) => UnoPlatformHostBuilder.Create().App(() => new App()).UseX11().UseMacOS().UseWin32().Build().Run();
}
