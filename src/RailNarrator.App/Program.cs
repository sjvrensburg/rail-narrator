using Avalonia;

namespace RailNarrator.App;

internal static class Program
{
    /// <summary>
    /// Entry point. Avalonia requires <c>BuildAvaloniaApp</c> to exist before the runtime starts, so
    /// nothing that touches Avalonia types may run before <c>StartWithClassicDesktopLifetime</c>.
    /// </summary>
    [STAThread]
    public static void Main(string[] args)
        => BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);

    public static AppBuilder BuildAvaloniaApp()
        => AppBuilder.Configure<App>()
            .UsePlatformDetect()
            .WithInterFont()
            .LogToTrace();
}
