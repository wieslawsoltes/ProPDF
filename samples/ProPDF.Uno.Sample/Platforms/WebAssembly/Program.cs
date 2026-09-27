using Uno.UI.Hosting;

namespace ProPDF.Uno.Sample;

internal static class Program
{
    public static async Task Main(string[] args)
    {
        try
        {
            // Install the browser error boundary before constructing XAML controls.
            await PlatformHost.InitializeAsync();
            Console.WriteLine("ProPDF: initializing Uno Skia browser host.");
            var host = UnoPlatformHostBuilder.Create()
                .App(() => new App())
                .UseWebAssembly()
                .Build();
            Console.WriteLine("ProPDF: running Uno Skia browser host.");
            await host.RunAsync();
        }
        catch (Exception error) { PlatformHost.Failed(error.ToString()); }
    }
}
