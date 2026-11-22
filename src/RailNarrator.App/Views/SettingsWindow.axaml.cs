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
        // Every change is saved as it is made, so Escape only closes. Not Enter: there are text boxes.
        DialogKeyboard.EnableEscEnterClose<object?>(this, null, null);
    }
}
