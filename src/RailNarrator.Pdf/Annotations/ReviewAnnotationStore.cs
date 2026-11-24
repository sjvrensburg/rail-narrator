using System.Text.Json;
using RailReader.Core.Models;

namespace RailNarrator.Pdf.Annotations;

/// <summary>What <see cref="ReviewAnnotationStore.Load"/> found.</summary>
/// <param name="Document">The annotations: empty when none were kept, or none could be read.</param>
/// <param name="Problem">Why something kept could not be read, for the reader; null when all was well.</param>
public sealed record ReviewLoadResult(ReviewDocument Document, string? Problem);

/// <summary>
/// The reader's annotations on disk, one directory per document, keyed by the PDF's content hash
/// (ADR-0014).
/// </summary>
/// <remarks>
/// <para>
/// <c>&lt;root&gt;/&lt;sha256&gt;/annotations.json</c> is Core's annotation format, so RailReader2 can read it.
/// <c>links.json</c> beside it is Rail Narrator's own. <c>&lt;root&gt;/index.json</c> remembers which hash
/// each path last had, so notes on a PDF that has since been revised can be found again: a document
/// whose path last had another hash is given that <see cref="EarlierVersion"/>, kept in
/// <c>earlier.json</c> with the links of the notes that could not be placed, which are in
/// <c>unplaced.json</c> (Core's format). The earlier version's own files are only ever read.
/// </para>
/// <para>
/// Nothing here may lose the reader's work. Every file is written under a temporary name and renamed
/// over the old one. A file that cannot be read is renamed aside and reported, never overwritten by
/// the empty document that replaces it. Saves are serialised on the caller's thread (the model is
/// the UI thread's) and written one at a time on the thread pool. A save that an older one would
/// overtake is dropped: the newest version of each document is the one on disk.
/// </para>
/// </remarks>
public sealed class ReviewAnnotationStore
{
    private const string AnnotationsFile = "annotations.json";
    private const string LinksFile = "links.json";
    private const string IndexFile = "index.json";
    private const string EarlierFile = "earlier.json";
    private const string UnplacedFile = "unplaced.json";

    private readonly string _root;
    private readonly SemaphoreSlim _writeGate = new(1, 1);
    private readonly object _versions = new();
    private readonly Dictionary<string, long> _written = new(StringComparer.Ordinal);
    private readonly HashSet<string> _blocked = new(StringComparer.Ordinal);
    private Task _lastWrite = Task.CompletedTask;

    public ReviewAnnotationStore(string root)
    {
        ArgumentException.ThrowIfNullOrEmpty(root);
        _root = root;
    }

    /// <summary>The directory a document's annotations are kept in.</summary>
    public string DirectoryFor(string documentContentHash) => Path.Combine(_root, documentContentHash);

    /// <summary>Reads a document's annotations. Blocking file I/O: call it off the UI thread.</summary>
    public ReviewLoadResult Load(string documentContentHash, string? pdfPath)
    {
        ArgumentException.ThrowIfNullOrEmpty(documentContentHash);
        string dir = DirectoryFor(documentContentHash);
        var problems = new List<string>();
        bool blocked = false;

        AnnotationFile? file = null;
        string annotationsPath = Path.Combine(dir, AnnotationsFile);
        if (File.Exists(annotationsPath))
        {
            file = ReadOrQuarantine(annotationsPath, ReviewAnnotationJson.Deserialize, problems, "annotations", out bool unread);
            blocked |= unread;
            if (file is not null)
            {
                file.Pages ??= [];
                file.Bookmarks ??= [];
            }
        }

        IReadOnlyList<ReviewLink>? links = null;
        string linksPath = Path.Combine(dir, LinksFile);
        if (File.Exists(linksPath))
        {
            links = ReadOrQuarantine(linksPath, ReviewAnnotationJson.DeserializeLinks, problems, "links to the narration", out bool unread);
            blocked |= unread;
        }

        EarlierState? earlier = null;
        string earlierPath = Path.Combine(dir, EarlierFile);
        if (File.Exists(earlierPath))
        {
            earlier = ReadOrQuarantine(earlierPath, ReviewAnnotationJson.DeserializeEarlier, problems, "record of the earlier version", out bool unread);
            blocked |= unread;
        }
        AnnotationFile? unplaced = null;
        string unplacedPath = Path.Combine(dir, UnplacedFile);
        if (File.Exists(unplacedPath))
        {
            unplaced = ReadOrQuarantine(unplacedPath, ReviewAnnotationJson.Deserialize, problems, "notes not placed from the earlier version", out bool unread);
            blocked |= unread;
            if (unplaced is not null) unplaced.Pages ??= [];
        }

        // The same path under another hash: the PDF was revised since it was annotated. Offered only
        // to a document with nothing of its own yet; once known, the offer is kept in earlier.json.
        var found = earlier?.Earlier;
        if (found is null && pdfPath is not null && !blocked
            && (file is null || file.Pages.Values.All(p => p.Count == 0)) && (links is null || links.Count == 0)
            && LastHashFor(pdfPath) is { } last && last != documentContentHash
            && File.Exists(Path.Combine(DirectoryFor(last), AnnotationsFile)))
            found = new EarlierVersion(last);

        // A file that is there but could not be read at all is still the reader's work: nothing is
        // saved over it this session.
        lock (_versions)
        {
            if (blocked) _blocked.Add(documentContentHash);
            else _blocked.Remove(documentContentHash);
        }
        if (blocked) problems.Add("Changes to this document's annotations will not be saved until it is opened again.");
        var document = new ReviewDocument(documentContentHash, pdfPath, file, links, found, unplaced, earlier?.UnplacedLinks);
        return new ReviewLoadResult(document, problems.Count == 0 ? null : string.Join(" ", problems));
    }

    /// <summary>
    /// Saves a document as it is now. Serialises here, on the caller's thread, and writes on the
    /// thread pool; the returned task completes when this version (or a newer one) is on disk.
    /// </summary>
    public Task SaveAsync(ReviewDocument document)
    {
        ArgumentNullException.ThrowIfNull(document);
        long version = document.Version;
        string hash = document.DocumentContentHash;
        if (document.SourcePath is { } path)
        {
            document.File.SourcePdf = Path.GetFileName(path);
            document.File.SourcePdfPath = Path.GetFullPath(path);
        }
        string annotations = ReviewAnnotationJson.Serialize(document.File);
        string links = ReviewAnnotationJson.SerializeLinks(document.Links.Values.OrderBy(l => l.CreatedUtc));
        string? sourcePath = document.SourcePath;
        string? earlier = document.Earlier is { } e ? ReviewAnnotationJson.SerializeEarlier(e, document.UnplacedLinks.Values.OrderBy(l => l.CreatedUtc)) : null;
        string? unplaced = document.Earlier is not null ? ReviewAnnotationJson.Serialize(document.Unplaced) : null;

        lock (_versions)
        {
            if (_blocked.Contains(hash))
                return Task.FromException(new IOException("The annotations kept for this document could not be read, so nothing is saved over them."));
            var write = Task.Run(async () =>
            {
                await _writeGate.WaitAsync().ConfigureAwait(false);
                try
                {
                    lock (_versions)
                        if (_written.TryGetValue(hash, out long done) && done >= version) return;
                    string dir = DirectoryFor(hash);
                    Directory.CreateDirectory(dir);
                    WriteAtomically(Path.Combine(dir, AnnotationsFile), annotations);
                    WriteAtomically(Path.Combine(dir, LinksFile), links);
                    if (earlier is not null && unplaced is not null)
                    {
                        WriteAtomically(Path.Combine(dir, UnplacedFile), unplaced);
                        WriteAtomically(Path.Combine(dir, EarlierFile), earlier);
                    }
                    else
                    {
                        // No earlier version any more (bringing its notes over was undone).
                        File.Delete(Path.Combine(dir, EarlierFile));
                        File.Delete(Path.Combine(dir, UnplacedFile));
                    }
                    if (sourcePath is not null) Remember(sourcePath, hash);
                    lock (_versions) _written[hash] = version;
                }
                finally { _writeGate.Release(); }
            });
            _lastWrite = write;
            return write;
        }
    }

    /// <summary>Completes when every save asked for so far has been written (or has failed).</summary>
    public async Task FlushAsync()
    {
        Task last;
        lock (_versions) last = _lastWrite;
        try { await last.ConfigureAwait(false); }
        catch (Exception) { /* the save that failed reported its own failure to whoever awaited it */ }
    }

    /// <summary>The content hash a path's annotations were last saved under, if any.</summary>
    public string? LastHashFor(string pdfPath)
    {
        var index = ReadIndex();
        return index.TryGetValue(Path.GetFullPath(pdfPath), out var hash) ? hash : null;
    }

    private void Remember(string pdfPath, string hash)
    {
        var index = ReadIndex();
        string key = Path.GetFullPath(pdfPath);
        if (index.TryGetValue(key, out var known) && known == hash) return;
        index[key] = hash;
        Directory.CreateDirectory(_root);
        WriteAtomically(Path.Combine(_root, IndexFile), JsonSerializer.Serialize(index, new JsonSerializerOptions { WriteIndented = true }));
    }

    private Dictionary<string, string> ReadIndex()
    {
        string path = Path.Combine(_root, IndexFile);
        try
        {
            if (!File.Exists(path)) return new(StringComparer.Ordinal);
            var index = JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(path));
            return index is null ? new(StringComparer.Ordinal) : new(index, StringComparer.Ordinal);
        }
        catch (JsonException) { return new(StringComparer.Ordinal); }   // an index is a convenience; it is rebuilt by saving
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return new(StringComparer.Ordinal); }
    }

    private static T? ReadOrQuarantine<T>(string path, Func<string, T?> parse, List<string> problems, string what, out bool unread)
        where T : class
    {
        unread = false;
        string text;
        try { text = File.ReadAllText(path); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Not read, so not moved either: it may be readable next time.
            problems.Add($"The saved {what} could not be read ({ex.Message}).");
            unread = true;
            return null;
        }
        if (parse(text) is { } value) return value;

        string aside = Path.Combine(Path.GetDirectoryName(path)!,
            $"{Path.GetFileNameWithoutExtension(path)}.unreadable-{DateTime.UtcNow:yyyyMMddHHmmss}{Path.GetExtension(path)}");
        try
        {
            File.Move(path, aside);
            problems.Add($"The saved {what} could not be read and were kept as {Path.GetFileName(aside)}.");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            problems.Add($"The saved {what} could not be read, nor set aside ({ex.Message}).");
            unread = true;
        }
        return null;
    }

    private static void WriteAtomically(string path, string contents)
    {
        // Unique temp: two writes must never share a temporary name.
        string temp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            File.WriteAllText(temp, contents);
            File.Move(temp, path, overwrite: true);
        }
        finally
        {
            try { File.Delete(temp); } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        }
    }
}
