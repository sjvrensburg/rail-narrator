using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace RailNarrator.Infrastructure;

/// <summary>A document opened before, for the welcome screen and File ▸ Open Recent.</summary>
/// <param name="Path">Where it was opened from.</param>
/// <param name="LastOpened">When it was last opened (UTC).</param>
/// <param name="Progress">How far through it the reading stood, 0 to 1, when last saved.</param>
/// <param name="PageCount">Its pages, 0 when not known yet.</param>
public sealed record RecentDocument(string Path, DateTime LastOpened, double Progress = 0, int PageCount = 0)
{
    [System.Text.Json.Serialization.JsonIgnore]
    public string Name => System.IO.Path.GetFileName(Path);

    /// <summary>Asks the file system each time; not saved with the list.</summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public bool Exists => File.Exists(Path);
}

/// <summary>
/// The documents opened most recently, newest first, kept in <c>$XDG_DATA_HOME/rail-narrator/recent.json</c>
/// with a small first-page thumbnail each, as RailReader2's welcome screen shows them.
/// </summary>
/// <remarks>
/// A file that no longer exists stays in the list (it may be on a drive not mounted now) until it is
/// removed, and is shown as missing. The list holds paths only; nothing of a document's text.
/// </remarks>
public sealed class RecentDocumentsStore
{
    /// <summary>How many are remembered.</summary>
    public const int Capacity = 12;

    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };
    private readonly string _file;
    private readonly string _thumbnails;
    private readonly object _gate = new();

    public RecentDocumentsStore(string? directory = null)
    {
        string root = directory ?? XdgPaths.Data;
        _file = System.IO.Path.Combine(root, "recent.json");
        _thumbnails = System.IO.Path.Combine(directory ?? XdgPaths.Cache, "thumbnails");
    }

    /// <summary>Newest first.</summary>
    public IReadOnlyList<RecentDocument> Load()
    {
        lock (_gate)
        {
            try
            {
                if (!File.Exists(_file)) return [];
                return JsonSerializer.Deserialize<List<RecentDocument>>(File.ReadAllText(_file), Json)?
                    .Where(d => !string.IsNullOrWhiteSpace(d.Path))
                    .OrderByDescending(d => d.LastOpened).Take(Capacity).ToList() ?? [];
            }
            catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
            {
                return [];   // a damaged list is only a convenience lost, never a reason not to start
            }
        }
    }

    /// <summary>Records that <paramref name="path"/> was opened now; it moves to the front.</summary>
    public IReadOnlyList<RecentDocument> Opened(string path, int pageCount, DateTime? now = null)
        => Update(path, existing => (existing ?? new RecentDocument(path, default)) with
        {
            LastOpened = now ?? DateTime.UtcNow,
            PageCount = pageCount > 0 ? pageCount : existing?.PageCount ?? 0,
        }, moveToFront: true);

    /// <summary>Records how far through <paramref name="path"/> the reading stands. Does not reorder.</summary>
    public IReadOnlyList<RecentDocument> Progressed(string path, double progress)
        => Update(path, existing => existing is null ? null : existing with { Progress = Math.Clamp(progress, 0, 1) }, moveToFront: false);

    public IReadOnlyList<RecentDocument> Remove(string path)
    {
        var list = Update(path, _ => null, moveToFront: false);
        DeleteThumbnail(path);
        return list;
    }

    /// <summary>Forgets every document whose file is not there now (moved, renamed, or on a drive not mounted).</summary>
    public IReadOnlyList<RecentDocument> RemoveMissing()
    {
        List<RecentDocument> missing;
        IReadOnlyList<RecentDocument> kept;
        lock (_gate)
        {
            var list = Load();
            missing = list.Where(d => !d.Exists).ToList();
            if (missing.Count == 0) return list;
            kept = Save(list.Where(d => d.Exists).ToList());
        }
        foreach (var d in missing) DeleteThumbnail(d.Path);
        return kept;
    }

    /// <summary>Forgets every document, and their thumbnails.</summary>
    public IReadOnlyList<RecentDocument> Clear()
    {
        IReadOnlyList<RecentDocument> list;
        lock (_gate)
        {
            list = Load();
            Save([]);
        }
        foreach (var d in list) DeleteThumbnail(d.Path);
        return [];
    }

    private void DeleteThumbnail(string path)
    {
        try { File.Delete(ThumbnailPath(path)); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    /// <summary>Where the first-page thumbnail of <paramref name="path"/> is kept (it may not exist yet).</summary>
    public string ThumbnailPath(string path)
    {
        string full = System.IO.Path.GetFullPath(path);
        string key = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(full)))[..24].ToLowerInvariant();
        return System.IO.Path.Combine(_thumbnails, key + ".png");
    }

    private IReadOnlyList<RecentDocument> Update(string path, Func<RecentDocument?, RecentDocument?> change, bool moveToFront)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        string full = System.IO.Path.GetFullPath(path);
        lock (_gate)
        {
            var list = Load().ToList();
            int index = list.FindIndex(d => string.Equals(d.Path, full, StringComparison.Ordinal));
            var updated = change(index >= 0 ? list[index] : null);
            if (index >= 0) list.RemoveAt(index);
            if (updated is not null)
            {
                updated = updated with { Path = full };
                if (moveToFront || index < 0) list.Insert(0, updated);
                else list.Insert(index, updated);
            }
            return Save(list.Take(Capacity).ToList());
        }
    }

    /// <summary>Writes the list (the caller holds the gate).</summary>
    private IReadOnlyList<RecentDocument> Save(List<RecentDocument> list)
    {
        try
        {
            Directory.CreateDirectory(System.IO.Path.GetDirectoryName(_file)!);
            string temp = _file + ".tmp";
            File.WriteAllText(temp, JsonSerializer.Serialize(list, Json));
            File.Move(temp, _file, overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        return list;
    }
}
