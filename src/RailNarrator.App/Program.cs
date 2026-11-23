using Avalonia;

namespace RailNarrator.App;

internal static class Program
{
    /// <summary>
    /// Entry point. Avalonia requires <c>BuildAvaloniaApp</c> to exist before the runtime starts, so
    /// nothing that touches Avalonia types may run before <c>StartWithClassicDesktopLifetime</c>.
    /// </summary>
    [STAThread]
    public static int Main(string[] args)
    {
        // "narrate" and "export" run without a window, before anything of Avalonia is touched.
        if (Composition.HeadlessNarration.IsCommand(args))
        {
            using var stop = new CancellationTokenSource();
            Console.CancelKeyPress += (_, e) => { e.Cancel = true; stop.Cancel(); };
            return Composition.HeadlessNarration.RunAsync(args, Console.Out, Console.Error, stop.Token).GetAwaiter().GetResult();
        }
        return BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
    }

    public static AppBuilder BuildAvaloniaApp()
        => AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .WithInterFont()
            .LogToTrace();
}
