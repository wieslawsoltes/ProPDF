using Uno.UI.Hosting;

namespace ProPDF.Uno.Sample;

internal static class Program
{
    public static async Task Main(string[] args)
    {
        // Skia WebAssembly needs the browser platform host. Application.Start is
        // the native-DOM entry point and does not initialize this renderer.
        Console.WriteLine("ProPDF: initializing Uno Skia browser host.");
        var host = UnoPlatformHostBuilder.Create()
            .App(() => new App())
            .UseWebAssembly()
            .Build();
        await host.RunAsync();
    }
}
