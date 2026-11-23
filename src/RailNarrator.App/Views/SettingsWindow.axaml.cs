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

    /// <summary>Shows the page with this name in the list on the left; an unknown or null name changes nothing.</summary>
    public void ShowPage(string? page)
    {
        if (page is null) return;
        foreach (var item in Pages.Items)
            if (item is TabItem tab && tab.Header as string == page) { Pages.SelectedItem = tab; return; }
    }
}
