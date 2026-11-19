using Avalonia.Controls;
using Avalonia.Interactivity;

namespace RailNarrator.App.Views;

/// <summary>F1, as RailReader2: every keyboard shortcut, grouped. Kept beside the key handling it describes.</summary>
public partial class ShortcutsDialog : Window
{
    private static readonly (string Group, (string Keys, string What)[] Items)[] Shortcuts =
    [
        ("Reading", [
            ("Space", "Play or pause; while a passage is being prepared, cancel the start"),
            ("Ctrl+P", "Play or pause"),
            ("Ctrl+Right", "Next passage"),
            ("Ctrl+Left", "Previous passage"),
            ("Ctrl+R", "Replay the passage"),
            ("Escape", "Pause"),
            ("Click a passage", "Read from there"),
        ]),
        ("Following", [
            ("Ctrl+F", "Follow the reading on or off"),
            ("F9", "Resume following after browsing"),
        ]),
        ("Page", [
            ("Page Down / Page Up", "Next or previous page (pauses following)"),
            ("Ctrl+G", "Go to a page"),
            ("Ctrl++ / Ctrl+-", "Zoom in or out (also Ctrl+wheel)"),
            ("C", "Next colour filter"),
        ]),
        ("Window", [
            ("Ctrl+O", "Open a PDF (or drop one on the window)"),
            ("Ctrl+W", "Close the document"),
            ("F6", "Sidebar: outline, script, maths review, notes"),
            ("Ctrl+,", "Settings"),
            ("F1", "This list"),
        ]),
    ];

    public ShortcutsDialog()
    {
        InitializeComponent();
        foreach (var (group, items) in Shortcuts)
        {
            Groups.Children.Add(new TextBlock { Text = group, Classes = { "group" } });
            var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*") };
            for (int i = 0; i < items.Length; i++)
            {
                grid.RowDefinitions.Add(new RowDefinition(GridLength.Auto));
                var keys = new TextBlock { Text = items[i].Keys, Classes = { "key" } };
                var what = new TextBlock { Text = items[i].What, Classes = { "what" } };
                Grid.SetRow(keys, i);
                Grid.SetRow(what, i);
                Grid.SetColumn(what, 1);
                grid.Children.Add(keys);
                grid.Children.Add(what);
            }
            Groups.Children.Add(grid);
        }
        DialogKeyboard.EnableEscEnterClose<object?>(this, null, null);
    }

    private void OnClose(object? sender, RoutedEventArgs e) => Close();
}
