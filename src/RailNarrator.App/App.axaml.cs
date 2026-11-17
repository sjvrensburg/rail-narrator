using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using RailNarrator.App.Composition;
using RailNarrator.App.Views;

namespace RailNarrator.App;

public sealed class App : Application
{
    public override void Initialize() => AvaloniaXamlLoader.Load(this);

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            var services = ServiceRegistry.Create();
            desktop.MainWindow = new MainWindow(services);
            desktop.Exit += (_, _) => services.DisposeAsync().AsTask().GetAwaiter().GetResult();
        }
        base.OnFrameworkInitializationCompleted();
    }
}
