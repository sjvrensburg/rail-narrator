using Avalonia.Controls;
using Avalonia.Interactivity;

namespace RailNarrator.App.Views;

/// <summary>Shown, as RailReader2's, when the application cannot be put together at start.</summary>
public partial class StartupErrorWindow : Window
{
    /// <summary>For the XAML previewer only.</summary>
    public StartupErrorWindow() => InitializeComponent();

    public StartupErrorWindow(Exception error)
    {
        InitializeComponent();
        Detail.Text = $"{error.GetType().Name}: {error.Message}";
    }

    private void OnQuit(object? sender, RoutedEventArgs e) => Close();
}
