using RailReader.Core;
using RailReader.Core.Models;
using RailReader.Core.Services;

namespace RailNarrator.Pdf;

/// <summary>
/// Marshals callbacks onto a single thread that the host pumps.
/// </summary>
/// <remarks>
/// Core's marshalled callbacks mutate caches and per-view state and are written against the
/// guarantee a UI dispatcher gives: a callback never runs concurrently with the host's own calls.
/// Core ships <c>SynchronousThreadMarshaller</c> (runs inline, so no such guarantee) and
/// <c>QueuedThreadMarshaller</c> (queues until pumped). This wraps the queued marshaller and pumps
/// from the Avalonia dispatcher, which gives the real guarantee on the real UI thread.
/// </remarks>
public sealed class DispatcherThreadMarshaller : IThreadMarshaller
{
    private readonly QueuedThreadMarshaller _inner = new();
    private readonly Action<Action> _postToUi;

    /// <param name="postToUi">Schedules work on the UI thread. In Avalonia: <c>Dispatcher.UIThread.Post</c>.</param>
    public DispatcherThreadMarshaller(Action<Action> postToUi)
        => _postToUi = postToUi ?? throw new ArgumentNullException(nameof(postToUi));

    public void Post(Action action)
    {
        _inner.Post(action);
        _postToUi(() => Pump());
    }

    /// <summary>Runs queued callbacks on the thread that constructed this marshaller.</summary>
    public int Pump() => _inner.Pump();

    public void AssertUIThread() => _inner.AssertUIThread();
}

/// <summary>
/// An annotation store that stores nothing.
/// </summary>
/// <remarks>
/// Two reasons. Narration highlights are transient overlays drawn above the page and must never be
/// written into the PDF as annotations — a reader's document should not accumulate another
/// application's markup. And Core.Pdfium's <c>AnnotationService.Default</c> persists under
/// <c>~/.config/railreader2/annotations</c>, so wiring it up here would write into RailReader2's
/// own store. Rail Narrator keeps its state in its own XDG directories.
/// </remarks>
public sealed class NullAnnotationStore : IAnnotationStore
{
    public static NullAnnotationStore Instance { get; } = new();

    public AnnotationFile? Load(string pdfPath, string? password = null) => null;

    public bool Save(string pdfPath, AnnotationFile annotations, string? password = null) => true;

    public bool Delete(string pdfPath, string? password = null) => true;
}

/// <summary>
/// Recent files and reading positions, stored in Rail Narrator's own XDG directory.
/// </summary>
/// <remarks>
/// Deliberately not Core.Pdfium's <c>AppConfig</c>: its <c>ConfigDir</c> is hard-coded to
/// <c>~/.config/railreader2</c> and it creates that directory on first access. Using it would read
/// and write another product's settings, which the brief explicitly forbids.
/// </remarks>
public sealed class RecentFilesStore : IRecentFilesStore
{
    /// <summary>
    /// Rail Narrator's own persisted entry. Core's <see cref="RecentFileEntry"/> is a mutable class
    /// with no timestamp, so recency is kept here and the Core type is produced on demand.
    /// </summary>
    private sealed record Entry(string FilePath, int Page, double Zoom, double OffsetX, double OffsetY, DateTime LastOpenedUtc);

    private readonly string _path;
    private readonly object _gate = new();
    private Dictionary<string, Entry> _entries;

    public RecentFilesStore(string stateDirectory)
    {
        Directory.CreateDirectory(stateDirectory);
        _path = Path.Combine(stateDirectory, "recent-files.json");
        _entries = Load();
    }

    public RecentFileEntry? GetReadingPosition(string filePath)
    {
        lock (_gate)
        {
            if (!_entries.TryGetValue(Normalise(filePath), out var e)) return null;
            return new RecentFileEntry
            {
                FilePath = e.FilePath,
                Page = e.Page,
                Zoom = e.Zoom,
                OffsetX = e.OffsetX,
                OffsetY = e.OffsetY,
                // Only claim a saved camera when one was really written. Restoring a default zoom of
                // 1.0 over a document's own fit would visibly jump the view on reopen.
                HasSavedCamera = e.Zoom > 0,
            };
        }
    }

    public void AddRecentFile(string filePath)
    {
        lock (_gate)
        {
            var key = Normalise(filePath);
            var existing = _entries.GetValueOrDefault(key);
            _entries[key] = existing is null
                ? new Entry(filePath, 0, 0, 0, 0, DateTime.UtcNow)
                : existing with { FilePath = filePath, LastOpenedUtc = DateTime.UtcNow };
            Save();
        }
    }

    public void SaveReadingPosition(string filePath, int page, double zoom, double offsetX, double offsetY, ColourEffect? colourEffect = null)
    {
        lock (_gate)
        {
            var key = Normalise(filePath);
            _entries[key] = new Entry(filePath, page, zoom, offsetX, offsetY, DateTime.UtcNow);
            Save();
        }
    }

    /// <summary>Most recently opened files, for the open-file menu.</summary>
    public IReadOnlyList<string> RecentPaths()
    {
        lock (_gate)
            return _entries.Values.OrderByDescending(e => e.LastOpenedUtc).Select(e => e.FilePath).ToList();
    }

    private static string Normalise(string path)
    {
        try { return Path.GetFullPath(path); }
        catch { return path; }
    }

    private Dictionary<string, Entry> Load()
    {
        try
        {
            if (!File.Exists(_path)) return new(StringComparer.Ordinal);
            var list = System.Text.Json.JsonSerializer.Deserialize<List<Entry>>(File.ReadAllText(_path));
            return list?.Where(e => !string.IsNullOrEmpty(e.FilePath))
                        .GroupBy(e => Normalise(e.FilePath))
                        .ToDictionary(g => g.Key, g => g.First(), StringComparer.Ordinal)
                   ?? new(StringComparer.Ordinal);
        }
        catch
        {
            // A corrupt recent-files list must not stop the application opening a document.
            return new(StringComparer.Ordinal);
        }
    }

    private void Save()
    {
        try
        {
            var list = _entries.Values.OrderByDescending(e => e.LastOpenedUtc).Take(50).ToList();
            File.WriteAllText(_path, System.Text.Json.JsonSerializer.Serialize(list));
        }
        catch
        {
            // Failing to remember a position is not worth failing a reading session over.
        }
    }
}

/// <summary>
/// Forwards Core's logging to a callback. Bounded and redacting.
/// </summary>
/// <remarks>
/// Document passages and credentials must not reach a log by default, so this forwards the level
/// and message and leaves redaction to the host's sink. It never logs extracted text.
/// </remarks>
public sealed class CallbackLogger : ILogger
{
    private readonly Action<string, string> _sink;

    public CallbackLogger(Action<string, string>? sink = null)
        => _sink = sink ?? ((_, _) => { });

    public void Debug(string message) => _sink("debug", message);
    public void Info(string message) => _sink("info", message);
    public void Warn(string message) => _sink("warn", message);
    public void Error(string message, Exception? ex = null)
        => _sink("error", ex is null ? message : $"{message}: {ex.GetType().Name}: {ex.Message}");
}
