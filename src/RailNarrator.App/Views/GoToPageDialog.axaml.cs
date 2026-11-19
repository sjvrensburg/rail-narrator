using Avalonia.Controls;
using Avalonia.Interactivity;

namespace RailNarrator.App.Views;

/// <summary>Ctrl+G, as RailReader2: asks for a page and closes with its zero-based index, or null.</summary>
public partial class GoToPageDialog : Window
{
    private readonly int _pageCount;

    /// <summary>For the XAML previewer only.</summary>
    public GoToPageDialog() : this(1, 0) { }

    public GoToPageDialog(int pageCount, int currentPage)
    {
        _pageCount = Math.Max(1, pageCount);
        InitializeComponent();
        Prompt.Text = $"Page number (1 to {_pageCount})";
        PageBox.Text = (currentPage + 1).ToString();
        DialogKeyboard.EnableEscEnterClose<int?>(this, null, null);
        DialogKeyboard.FocusOnOpen(this, PageBox, selectAll: true);
    }

    private void OnGo(object? sender, RoutedEventArgs e)
    {
        // An invalid number keeps the dialog open with the reason, as RailReader2's.
        if (!int.TryParse(PageBox.Text?.Trim(), out int page) || page < 1 || page > _pageCount)
        {
            Error.Text = $"Enter a page from 1 to {_pageCount}.";
            Error.IsVisible = true;
            PageBox.Focus();
            PageBox.SelectAll();
            return;
        }
        Close((int?)(page - 1));
    }

    private void OnCancel(object? sender, RoutedEventArgs e) => Close((int?)null);
}
