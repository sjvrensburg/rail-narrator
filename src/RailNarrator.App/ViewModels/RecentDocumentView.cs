using Avalonia.Media.Imaging;
using RailNarrator.Infrastructure;

namespace RailNarrator.App.ViewModels;

/// <summary>A recent document as the welcome screen shows it: thumbnail, name, how far through, when.</summary>
public sealed class RecentDocumentView
{
    private readonly string _thumbnailPath;
    private Bitmap? _thumbnail;
    private bool _thumbnailTried;

    public RecentDocumentView(RecentDocument document, string thumbnailPath)
    {
        Document = document;
        _thumbnailPath = thumbnailPath;
    }

    public RecentDocument Document { get; }
    public string Path => Document.Path;
    public string Name => Document.Name;
    public bool Exists => Document.Exists;
    public double Progress => Document.Progress;
    /// <summary>Under 1 % is not shown: "Read 0 %" for a document barely begun says nothing.</summary>
    public bool HasProgress => Document.Progress >= 0.01;

    /// <summary>"Read 40 % · 12 pages · yesterday", or why it cannot be opened.</summary>
    public string Detail
    {
        get
        {
            if (!Exists) return "Not found: moved, renamed, or on a drive not mounted";
            var parts = new List<string>();
            if (HasProgress) parts.Add($"Read {Document.Progress:P0}");
            if (Document.PageCount > 0) parts.Add(Document.PageCount == 1 ? "1 page" : $"{Document.PageCount} pages");
            parts.Add(When(Document.LastOpened));
            return string.Join(" · ", parts);
        }
    }

    /// <summary>The first page, small; null until the document has been shown once.</summary>
    public Bitmap? Thumbnail
    {
        get
        {
            if (_thumbnailTried) return _thumbnail;
            _thumbnailTried = true;
            try { if (File.Exists(_thumbnailPath)) _thumbnail = new Bitmap(_thumbnailPath); }
            catch (Exception) { _thumbnail = null; }   // a damaged thumbnail is only a picture lost
            return _thumbnail;
        }
    }

    public bool HasThumbnail => Thumbnail is not null;

    private static string When(DateTime utc)
    {
        var local = utc.ToLocalTime();
        var days = (DateTime.Now.Date - local.Date).Days;
        return days switch
        {
            0 => $"today {local:HH:mm}",
            1 => "yesterday",
            < 7 => $"{days} days ago",
            _ => local.ToString("d MMM yyyy"),
        };
    }
}
