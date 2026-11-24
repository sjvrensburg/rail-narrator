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
            ("Escape", "Back to the reading after browsing; otherwise pause"),
            ("Click a passage", "Read from there"),
        ]),
        ("Review notes", [
            ("N", "Pause and write a note on the sentence just read"),
            ("Shift+N", "The same, on the whole passage"),
            ("H", "Highlight the sentence just read, without stopping"),
            ("Shift+H", "Highlight the whole passage"),
            ("Enter", "In a note: save it and stay paused (Shift+Enter: a new line)"),
            ("Ctrl+Enter", "In a note: save it and read on"),
            ("Escape", "In a note: give it up"),
            ("Ctrl+Z / Ctrl+Shift+Z", "Undo or redo an annotation (also Ctrl+Y)"),
            ("Delete", "Delete the selected annotation"),
            ("Click a note's mark", "Show the note (double-click to edit it)"),
        ]),
        ("Following", [
            ("Ctrl+F", "Follow the reading on or off"),
            ("F9", "Resume following after browsing (also Escape)"),
        ]),
        ("Page", [
            ("Page Down / Page Up", "Next or previous page (pauses following)"),
            ("Arrow keys", "Scroll the page, when it has focus (pauses following)"),
            ("Home / End", "Top or bottom of the page"),
            ("Shift+wheel", "Scroll sideways"),
            ("Ctrl+G", "Go to a page"),
            ("Ctrl+= / Ctrl+-", "Zoom in or out (also the keypad's + and -, and Ctrl+wheel about the pointer)"),
            ("C", "Next colour filter"),
        ]),
        ("Window", [
            ("Ctrl+O", "Open a PDF (or drop one on the window)"),
            ("Ctrl+Shift+R", "Reload the document (applies maths corrections)"),
            ("Ctrl+W", "Close the document"),
            ("F6", "Sidebar: outline, script, maths review, review notes, diagnostics"),
            ("Ctrl+,", "Settings"),
            ("F1", "This list"),
            ("Ctrl+Q", "Quit"),
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
