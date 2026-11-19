using Avalonia.Controls;

namespace RailNarrator.App.Views;

public partial class SplashWindow : Window
{
    public SplashWindow()
    {
        InitializeComponent();
        Closing += (_, _) => Bar.IsIndeterminate = false;
    }
}
