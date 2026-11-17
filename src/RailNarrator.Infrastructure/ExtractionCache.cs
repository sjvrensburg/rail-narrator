using System.IO.Compression;
using System.Text.Json;
using System.Text.Json.Serialization;
using RailNarrator.Domain;

namespace RailNarrator.Infrastructure;

/// <summary>
/// Keeps extraction snapshots on disk so a document is analysed, and a scan OCRed, once rather than
/// on every open.
/// </summary>
/// <remarks>
/// <para>
/// The key is <see cref="CacheKey.ForExtraction"/> — the document's bytes, analyser, OCR policy,
/// render size, extractor version and filters — plus the request's page selection, view rotation and
/// revision, and a <c>backendFingerprint</c> for what the options do not name (which layout model
/// file an analyser id resolves to). Anything that changes the snapshot changes the key; a stale
/// entry is never matched, only orphaned.
/// </para>
/// <para>
/// A snapshot produced while OCR was requested but unavailable is not stored: it is degraded, and
/// caching it would keep a scan unreadable after the OCR models are installed.
/// </para>
/// </remarks>
public sealed class CachingExtractionSource : IExtractionSource
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = false,
        Converters = { new JsonStringEnumConverter() },
    };

    private readonly IExtractionSource _inner;
    private readonly string _root;
    private readonly string _backendFingerprint;
    private readonly Func<bool> _isDegraded;

    /// <param name="inner">The real extractor.</param>
    /// <param name="root">Cache directory; defaults to <see cref="XdgPaths.ExtractionCache"/>.</param>
    /// <param name="backendFingerprint">Inputs that shape extraction but are not in <see cref="ExtractionOptions"/>.</param>
    /// <param name="isDegraded">True when the inner extractor is running without a capability it was asked for.</param>
    public CachingExtractionSource(IExtractionSource inner, string? root = null, string backendFingerprint = "", Func<bool>? isDegraded = null)
    {
        _inner = inner ?? throw new ArgumentNullException(nameof(inner));
        _root = root ?? XdgPaths.ExtractionCache;
        _backendFingerprint = backendFingerprint ?? "";
        _isDegraded = isDegraded ?? (() => false);
        Directory.CreateDirectory(_root);
    }

    /// <summary>Whether the last <see cref="ExtractAsync"/> was answered from the cache. For diagnostics.</summary>
    public bool LastWasCacheHit { get; private set; }

    public async Task<ExtractionSnapshot> ExtractAsync(
        ExtractionRequest request, IProgress<ExtractionProgress>? progress = null, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        LastWasCacheHit = false;

        // An encrypted document's snapshot is its decrypted text: do not write it to disk.
        if (request.Password is not null)
            return await _inner.ExtractAsync(request, progress, ct).ConfigureAwait(false);

        string path = await Task.Run(() => EntryPath(request), ct).ConfigureAwait(false);
        if (await Task.Run(() => TryRead(path), ct).ConfigureAwait(false) is { } cached)
        {
            LastWasCacheHit = true;
            progress?.Report(new ExtractionProgress(cached.PageCount, cached.PageCount, "Loaded from cache"));
            return cached;
        }

        var snapshot = await _inner.ExtractAsync(request, progress, ct).ConfigureAwait(false);
        if (!_isDegraded())
        {
            try { await Task.Run(() => Write(path, snapshot), ct).ConfigureAwait(false); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { /* a cache that cannot be written is a miss next time, not a failure now */ }
        }
        return snapshot;
    }

    public Task<ReadOnlyMemory<byte>?> RenderCropPngAsync(
        ExtractionIdentity extraction, int pageIndex, PageRect region, int targetPixelWidth, CancellationToken ct = default)
        => _inner.RenderCropPngAsync(extraction, pageIndex, region, targetPixelWidth, ct);

    /// <summary>Total bytes held, for the cache indicator.</summary>
    public long TotalBytes()
    {
        try { return Directory.EnumerateFiles(_root, "*.json.gz").Sum(f => new FileInfo(f).Length); }
        catch { return 0; }
    }

    private string EntryPath(ExtractionRequest request)
    {
        string documentHash = CacheKey.Sha256HexOfFile(request.FilePath);
        string pages = request.Pages is null ? "all" : string.Join(",", request.Pages);
        string key = CacheKey.Sha256Hex(string.Join("␟",
            CacheKey.ForExtraction(documentHash, request.Options, request.Filters),
            pages, request.ViewRotation.ToString(), request.Revision.ToString(), _backendFingerprint));
        return Path.Combine(_root, key + ".json.gz");
    }

    private static ExtractionSnapshot? TryRead(string path)
    {
        try
        {
            if (!File.Exists(path)) return null;
            using var file = File.OpenRead(path);
            using var gzip = new GZipStream(file, CompressionMode.Decompress);
            return JsonSerializer.Deserialize<ExtractionSnapshot>(gzip, JsonOptions);
        }
        catch { return null; }   // a corrupt or truncated entry is a miss
    }

    private static void Write(string path, ExtractionSnapshot snapshot)
    {
        // Unique temp then rename: a reader never sees a half-written entry.
        string temp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            using (var file = File.Create(temp))
            using (var gzip = new GZipStream(file, CompressionLevel.Fastest))
                JsonSerializer.Serialize(gzip, snapshot, JsonOptions);
            File.Move(temp, path, overwrite: true);
        }
        finally
        {
            try { File.Delete(temp); } catch { }
        }
    }
}

/// <summary>
/// Small text values on disk, one file per key: maths interpretations, so a passage the model has
/// interpreted is not sent to it again on the next open.
/// </summary>
public sealed class DiskTextCache : ITextCache
{
    private readonly string _root;

    public DiskTextCache(string? root = null)
    {
        _root = root ?? Path.Combine(XdgPaths.Cache, "interpretations");
        Directory.CreateDirectory(_root);
    }

    private string PathFor(string key) => Path.Combine(_root, CacheKey.Sha256Hex(key) + ".txt");

    public async Task<string?> TryGetAsync(string key, CancellationToken ct = default)
    {
        try
        {
            string path = PathFor(key);
            return File.Exists(path) ? await File.ReadAllTextAsync(path, ct).ConfigureAwait(false) : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return null; }
    }

    public async Task PutAsync(string key, string value, CancellationToken ct = default)
    {
        string path = PathFor(key);
        string temp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            await File.WriteAllTextAsync(temp, value, ct).ConfigureAwait(false);
            File.Move(temp, path, overwrite: true);
        }
        finally
        {
            try { File.Delete(temp); } catch { }
        }
    }
}
