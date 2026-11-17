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
            desktop.Exit += (_, _) => DisposeServices(services);
        }
        base.OnFrameworkInitializationCompleted();
    }

    /// <summary>
    /// Releases the object graph at exit without deadlocking the thread that is exiting.
    /// </summary>
    /// <remarks>
    /// Exit is raised on the UI thread. Blocking that thread on an async disposal whose awaits
    /// resume on the UI thread waits for itself, so File &gt; Exit left the window up and the process
    /// running forever. Disposal therefore runs on the thread pool, where there is no
    /// synchronisation context to resume on, and the wait is bounded: a speech runtime or alignment
    /// worker that will not stop must not be able to keep a closed application alive.
    /// </remarks>
    private static void DisposeServices(ServiceRegistry services)
    {
        try
        {
            Task.Run(() => services.DisposeAsync().AsTask()).Wait(ShutdownTimeout);
        }
        catch (AggregateException) { /* exiting anyway; a failed disposal must not crash the exit */ }
    }

    private static readonly TimeSpan ShutdownTimeout = TimeSpan.FromSeconds(5);
}
