using System.Reflection;
using Avalonia.Controls;
using Avalonia.Interactivity;
using RailNarrator.App.ViewModels;
using RailNarrator.Infrastructure;

namespace RailNarrator.App.Views;

/// <summary>Help ▸ About, as RailReader2's: the version, what is in use, and where files are kept.</summary>
public partial class AboutDialog : Window
{
    /// <summary>For the XAML previewer only.</summary>
    public AboutDialog() => InitializeComponent();

    public AboutDialog(MainViewModel viewModel)
    {
        InitializeComponent();
        string version = typeof(AboutDialog).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
                         ?? typeof(AboutDialog).Assembly.GetName().Version?.ToString() ?? "unknown";
        VersionText.Text = $"Version {version.Split('+')[0]}";
        InUseText.Text = string.Join("\n", new[]
        {
            $"Speech: {viewModel.SpeechProviderSummary}",
            $"Layout: {viewModel.LayoutSummary}",
            $"Maths: {viewModel.MathEndpointSummary}",
            viewModel.TimingSummary,
        }.Where(s => !string.IsNullOrWhiteSpace(s)));
        PathsText.Text = string.Join("\n", new[]
        {
            $"Settings  {viewModel.Services.Settings.FilePath}",
            $"Models    {XdgPaths.Models}",
            $"Cache     {XdgPaths.Cache}",
            $"Logs      {XdgPaths.Logs}",
        });
        DialogKeyboard.EnableEscEnterClose<object?>(this, null, null);
    }

    private void OnClose(object? sender, RoutedEventArgs e) => Close();
}
