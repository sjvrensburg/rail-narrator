using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Styling;
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
            // As RailReader2: a splash while the services are put together (the speech model, the
            // aligner and the GPU take seconds), off the UI thread so the splash is drawn, and a
            // window that says why if they cannot be. Until the main window exists, closing the
            // splash must not end the application.
            desktop.ShutdownMode = ShutdownMode.OnExplicitShutdown;
            var splash = new SplashWindow();
            splash.Show();
            _ = StartAsync(desktop, splash);
        }
        base.OnFrameworkInitializationCompleted();
    }

    private async Task StartAsync(IClassicDesktopStyleApplicationLifetime desktop, SplashWindow splash)
    {
        ServiceRegistry services;
        try
        {
            services = await Task.Run(ServiceRegistry.Create);
        }
        catch (Exception ex)
        {
            var error = new StartupErrorWindow(ex);
            error.Closed += (_, _) => desktop.Shutdown(1);
            error.Show();
            splash.Close();
            return;
        }

        RequestedThemeVariant = services.CurrentSettings.DarkMode ? ThemeVariant.Dark : ThemeVariant.Light;
        var window = new MainWindow(services) { FontSize = FontSizeFor(services.CurrentSettings.UiFontScale) };
        desktop.MainWindow = window;
        desktop.ShutdownMode = ShutdownMode.OnMainWindowClose;
        desktop.Exit += (_, _) => DisposeServices(services);
        HandleTerminationSignals(window);
        window.Show();
        splash.Close();
        Views.StallProbe.Start();
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

    /// <summary>Text size at a scale of 1, as RailReader2.</summary>
    public const double BaseFontSize = 14;

    /// <summary>The window font size for a text-size setting, clamped to RailReader2's 0.75 to 2.</summary>
    public static double FontSizeFor(double scale) => BaseFontSize * Math.Clamp(double.IsFinite(scale) ? scale : 1, 0.75, 2.0);

    /// <summary>
    /// Applies the theme and the text size to every open window at once. Icons follow the text
    /// size (they are sized from the inherited font size), and menus and pop-ups inherit it.
    /// </summary>
    public static void ApplyAppearance(bool dark, double fontScale)
    {
        if (Current is not { } app) return;
        app.RequestedThemeVariant = dark ? ThemeVariant.Dark : ThemeVariant.Light;
        if (app.ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
            foreach (Window window in desktop.Windows) window.FontSize = FontSizeFor(fontScale);
    }

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
