using Avalonia.Controls;
using RailNarrator.App.ViewModels;

namespace RailNarrator.App.Views;

public partial class SettingsWindow : Window
{
    /// <summary>For the XAML previewer only.</summary>
    public SettingsWindow() => InitializeComponent();

    public SettingsWindow(SettingsViewModel viewModel)
    {
        DataContext = viewModel;
        InitializeComponent();
    }
}
