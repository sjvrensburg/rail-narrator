using RailReader.Core.Models;

namespace RailNarrator.App.ViewModels;

/// <summary>A heading in the document's own outline (its PDF bookmarks), for the sidebar.</summary>
public sealed class OutlineItem
{
    private OutlineItem(string title, int? page, OutlineItem? parent)
    {
        Title = string.IsNullOrWhiteSpace(title) ? "(untitled)" : title.Trim();
        Page = page;
        Parent = parent;
    }

    public string Title { get; }

    /// <summary>Zero-based page the heading points to, when the PDF says.</summary>
    public int? Page { get; }

    public OutlineItem? Parent { get; }

    public IReadOnlyList<OutlineItem> Children { get; private set; } = [];

    public string PageLabel => Page is { } p ? $"Page {p + 1}" : "No page given";

    /// <summary>The heading with its ancestors, as the status bar shows it: "2 Method › 2.1 Data".</summary>
    public string Path => Parent is null ? Title : $"{Parent.Path} › {Title}";

    /// <summary>Builds the tree; <paramref name="inOrder"/> receives every heading in document order.</summary>
    public static IReadOnlyList<OutlineItem> Build(IEnumerable<OutlineEntry> entries, List<OutlineItem> inOrder, OutlineItem? parent = null, int depth = 0)
    {
        var items = new List<OutlineItem>();
        if (depth > 32) return items;   // a malformed outline that loops must not recurse forever
        foreach (var entry in entries)
        {
            var item = new OutlineItem(entry.Title, entry.Page, parent);
            inOrder.Add(item);
            item.Children = Build(entry.Children, inOrder, item, depth + 1);
            items.Add(item);
        }
        return items;
    }
}

/// <summary>A notice in the status bar with an optional action, as RailReader2's status toasts.</summary>
public sealed record StatusNotice(string Text, string? ActionLabel = null, Action? Action = null)
{
    public bool HasAction => ActionLabel is not null && Action is not null;
}
