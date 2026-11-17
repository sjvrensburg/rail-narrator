using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Avalonia.Threading;
using System.Runtime.InteropServices;
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
            var window = new MainWindow(services);
            desktop.MainWindow = window;
            desktop.Exit += (_, _) => DisposeServices(services);
            HandleTerminationSignals(window);
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

    /// <summary>
    /// Turns SIGTERM, SIGINT and SIGHUP — a session logout, <c>kill</c>, Ctrl+C in a terminal —
    /// into an ordinary window close, so the reading position is saved and the speech runtime and
    /// alignment worker are stopped. Without this the runtime's default handling ended the process
    /// with neither.
    /// </summary>
    private static void HandleTerminationSignals(MainWindow window)
    {
        foreach (var signal in new[] { PosixSignal.SIGTERM, PosixSignal.SIGINT, PosixSignal.SIGHUP })
        {
            SignalRegistrations.Add(PosixSignalRegistration.Create(signal, context =>
            {
                context.Cancel = true;   // the close below ends the process once disposal is done
                Dispatcher.UIThread.Post(window.Close);
            }));
        }
    }

    // Kept alive for the life of the process: a collected registration stops handling its signal.
    private static readonly List<PosixSignalRegistration> SignalRegistrations = [];
}
