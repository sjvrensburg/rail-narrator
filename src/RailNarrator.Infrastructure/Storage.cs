using System.Text.Json;
using System.Text.Json.Serialization;
using RailNarrator.Domain;

namespace RailNarrator.Infrastructure;

/// <summary>
/// Rail Narrator's own XDG directories.
/// </summary>
/// <remarks>
/// Deliberately separate from RailReaderCore's <c>AppConfig</c>, whose <c>ConfigDir</c> is hard-coded
/// to <c>~/.config/railreader2</c> and which creates that directory on first access. This application
/// is a companion, not a rewrite: sharing a settings directory would mean one product silently
/// reading and overwriting the other's configuration.
/// </remarks>
public static class XdgPaths
{
    public const string ApplicationId = "rail-narrator";

    private static string Base(string envVar, string fallback)
    {
        string? value = Environment.GetEnvironmentVariable(envVar);
        return string.IsNullOrWhiteSpace(value) ? fallback : value;
    }

    /// <summary>Settings and small state. <c>$XDG_CONFIG_HOME/rail-narrator</c>.</summary>
    public static string Config => Path.Combine(
        Base("XDG_CONFIG_HOME", Path.Combine(Home, ".config")), ApplicationId);

    /// <summary>Models, cached speech and extraction snapshots. <c>$XDG_DATA_HOME/rail-narrator</c>.</summary>
    public static string Data => Path.Combine(
        Base("XDG_DATA_HOME", Path.Combine(Home, ".local", "share")), ApplicationId);

    /// <summary>Regenerable caches that may be cleared freely. <c>$XDG_CACHE_HOME/rail-narrator</c>.</summary>
    public static string Cache => Path.Combine(
        Base("XDG_CACHE_HOME", Path.Combine(Home, ".cache")), ApplicationId);

    /// <summary>Logs and diagnostics. <c>$XDG_STATE_HOME/rail-narrator</c>.</summary>
    public static string State => Path.Combine(
        Base("XDG_STATE_HOME", Path.Combine(Home, ".local", "state")), ApplicationId);

    public static string Models => Path.Combine(Data, "models");

    /// <summary>
    /// Where RailReader2 keeps the layout models it downloads (<c>$XDG_CONFIG_HOME/railreader2/models</c>).
    /// Read, never written: a reader who already has Heron for RailReader2 need not download it again.
    /// </summary>
    public static string RailReader2Models => Path.Combine(
        Base("XDG_CONFIG_HOME", Path.Combine(Home, ".config")), "railreader2", "models");
    public static string SpeechCache => Path.Combine(Cache, "speech");
    public static string ExtractionCache => Path.Combine(Cache, "extraction");
    public static string Logs => Path.Combine(State, "logs");

    private static string Home
        => Environment.GetEnvironmentVariable("HOME")
           ?? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

    /// <summary>Creates every directory the application needs. Safe to call repeatedly.</summary>
    public static void EnsureCreated()
    {
        foreach (var dir in new[] { Config, Data, Cache, State, Models, SpeechCache, ExtractionCache, Logs })
            Directory.CreateDirectory(dir);
    }

    public static IReadOnlyDictionary<string, string> Describe() => new Dictionary<string, string>
    {
        ["config"] = Config,
        ["data"] = Data,
        ["cache"] = Cache,
        ["state"] = State,
        ["models"] = Models,
        ["speech-cache"] = SpeechCache,
        ["extraction-cache"] = ExtractionCache,
        ["logs"] = Logs,
    };
}

/// <summary>Persisted application settings.</summary>
/// <remarks>
/// No credential is stored here. API keys belong in the environment or a mode-0600 file the user
/// controls, so that a settings file copied for a bug report cannot leak one.
/// </remarks>
public sealed record RailNarratorSettings
{
    /// <summary>Schema version, so a future change can migrate rather than silently reset.</summary>
    public int Version { get; init; } = 1;

    public string? LastDocumentPath { get; init; }
    // Infrastructure deliberately does not reference the Speech assembly, so the default id is
    // the literal rather than SpeechProviderIds.Kokoro. Keep the two in sync.
    public string SpeechProviderId { get; init; } = "kokoro";
    public string? KokoroModelPath { get; init; }
    public string? KokoroVoicesDirectory { get; init; }
    public string? KokoroVoiceId { get; init; }

    /// <summary>
    /// Where Kokoro runs. <c>Auto</c> uses the GPU (WebGPU, so Vulkan on Linux) when the converted
    /// model from <c>tools/kokoro-webgpu-model.sh</c> is installed; <c>Cpu</c> never does. ADR-0007.
    /// </summary>
    public SpeechAcceleration KokoroAcceleration { get; init; } = SpeechAcceleration.Auto;

    /// <summary>The WebGPU Kokoro model; defaults to <c>kokoro-fp32-webgpu.onnx</c> in the models directory.</summary>
    public string? KokoroGpuModelPath { get; init; }
    public string? ZonosBaseUrl { get; init; } = "http://127.0.0.1:19191";
    public string? ZonosSpeakerId { get; init; }
    public double ZonosSpeakerCfgScale { get; init; } = 1.5;
    public double? ZonosLoudnessLufs { get; init; } = -18;
    public string? ZonosExecutable { get; init; }
    public string? ZonosModelDirectory { get; init; }
    public bool WordHighlighting { get; init; } = true;
    public string? MfaExecutable { get; init; }
    public string? MfaDictionaryPath { get; init; }
    public string? MfaAcousticModelPath { get; init; }
    public string? MfaG2pModelPath { get; init; }
    public string MfaRuntimeRevision { get; init; } = "3.3.9";

    public MathMode MathMode { get; init; } = MathMode.Interpret;
    public MathFallbackPolicy MathFallback { get; init; } = MathFallbackPolicy.AnnounceUnreadable;

    /// <summary>Text LLM endpoint for verbalisation. Local by default; remote is opt-in.</summary>
    public string? MathLlmBaseUrl { get; init; }
    public string? MathLlmModel { get; init; }
    /// <summary>Name of an environment variable holding the key. The key itself is never persisted.</summary>
    public string? MathLlmApiKeyEnvVar { get; init; }

    /// <summary>
    /// When a reasoning model may think before interpreting maths: <c>Off</c>, <c>EquationsOnly</c>
    /// (the default: display equations and their lead-ins, where meaning matters most; sentences with
    /// inline maths are answered directly) or <c>Always</c>. Thinking is about ten times slower per
    /// passage on Ornith-35B and more faithful; endpoints without the switch ignore it.
    /// </summary>
    public MathThinkingPolicy MathLlmThinking { get; init; } = MathThinkingPolicy.EquationsOnly;

    /// <summary>Older setting: <c>true</c> means <see cref="MathLlmThinking"/> <c>Off</c>.</summary>
    public bool MathLlmDisableThinking { get; init; }

    [JsonIgnore]
    public MathThinkingPolicy EffectiveMathThinking => MathLlmDisableThinking ? MathThinkingPolicy.Off : MathLlmThinking;

    /// <summary>Vision endpoint for equation transcription, configured separately from the text LLM.</summary>
    public string? VlmBaseUrl { get; init; }
    public string? VlmModel { get; init; }
    public string? VlmApiKeyEnvVar { get; init; }

    public NarrationFilterSettings Filters { get; init; } = NarrationFilterSettings.Default;
    public OcrPolicy Ocr { get; init; } = OcrPolicy.Full;
    /// <summary>
    /// Which layout model to use: <c>heron</c> (Docling Heron), <c>pp-doclayout-v3</c>, <c>none</c> (the
    /// model-free analyzer), or anything else for automatic: Heron if installed, else PP-DocLayoutV3,
    /// else none. <c>text-layout</c>, the old default that older settings files still carry, means
    /// automatic. See ADR-0008 and <see cref="LayoutModelResolver"/>.
    /// </summary>
    public string LayoutAnalyzer { get; init; } = "auto";
    public string? LayoutModelPath { get; init; }
    public LayoutAcceleration LayoutAcceleration { get; init; } = LayoutAcceleration.Auto;

    public string? AudioDeviceId { get; init; }
    public double PlaybackRate { get; init; } = 1.0;
    public FollowMode FollowMode { get; init; } = FollowMode.Follow;
    /// <summary>The magnification the reader last chose. Used when <see cref="FitWidth"/> is off.</summary>
    public double PreferredZoom { get; init; } = 1.0;

    /// <summary>
    /// Fit the page's width to the reading area, following page turns and resizes. On until the
    /// reader chooses a magnification; then <see cref="PreferredZoom"/> is used, for the next
    /// document and the next session as well.
    /// </summary>
    public bool FitWidth { get; init; } = true;
    public bool ReduceMotion { get; init; }

    /// <summary>RailReader colour filter for the page, by name: None, HighContrast, HighVisibility, Amber or Invert.</summary>
    public string ColourEffect { get; init; } = "None";

    /// <summary>Strength of the colour filter, 0 to 1.</summary>
    public double ColourEffectIntensity { get; init; } = 1.0;

    /// <summary>
    /// Seconds without scrolling, dragging or turning pages after which following resumes by itself.
    /// 0 keeps it paused until the reader resumes it (F9). A silent, indefinite pause was easy to miss:
    /// the reading moved on while the view stayed put.
    /// </summary>
    public double FollowAutoResumeSeconds { get; init; } = 6;
    public byte HighlightAlpha { get; init; } = 72;
    public bool HighlightHighContrast { get; init; }

    /// <summary>The fewest passages prepared ahead of the one being read.</summary>
    public int LookaheadSegments { get; init; } = 4;

    /// <summary>Listening time prepared ahead of the passage being read, in seconds (at most 40 passages).</summary>
    public double LookaheadSeconds { get; init; } = 60;
    public int MaxConcurrentSynthesis { get; init; } = 1;

    public IReadOnlyDictionary<string, string> PronunciationDictionary { get; init; }
        = new Dictionary<string, string>(StringComparer.Ordinal);

    /// <summary>Resolves an API key from the named environment variable, or null.</summary>
    public string? ResolveMathApiKey()
        => string.IsNullOrEmpty(MathLlmApiKeyEnvVar) ? null : Environment.GetEnvironmentVariable(MathLlmApiKeyEnvVar);

    public string? ResolveVlmApiKey()
        => string.IsNullOrEmpty(VlmApiKeyEnvVar) ? null : Environment.GetEnvironmentVariable(VlmApiKeyEnvVar);

    public static RailNarratorSettings Default => new();
}

/// <summary>
/// Loads and saves settings as JSON in the XDG config directory.
/// </summary>
public sealed class SettingsStore
{
    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        Converters = { new FollowModeJsonConverter(), new JsonStringEnumConverter() },
        PropertyNameCaseInsensitive = true,
    };

    private readonly string _path;
    private readonly object _gate = new();

    public SettingsStore(string? path = null)
    {
        _path = path ?? Path.Combine(XdgPaths.Config, "settings.json");
        Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
    }

    /// <summary>Absolute path of the settings file.</summary>
    public string FilePath => _path;

    public RailNarratorSettings Load()
    {
        lock (_gate)
        {
            try
            {
                if (!File.Exists(_path)) return RailNarratorSettings.Default;
                var loaded = JsonSerializer.Deserialize<RailNarratorSettings>(File.ReadAllText(_path), Options);
                // A hand-edited or truncated file must not stop the application starting; the defaults
                // are usable and the corruption is reported rather than hidden.
                return loaded ?? RailNarratorSettings.Default;
            }
            catch (Exception ex)
            {
                LastError = $"Could not read settings ({ex.GetType().Name}: {ex.Message}); using defaults.";
                return RailNarratorSettings.Default;
            }
        }
    }

    /// <summary>Set when <see cref="Load"/> fell back to defaults, so the UI can say so.</summary>
    public string? LastError { get; private set; }

    public void Save(RailNarratorSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        lock (_gate)
        {
            // Write to a temporary file and rename, so a crash mid-write cannot leave a truncated
            // settings file that resets every preference on next start.
            string temp = _path + ".tmp";
            File.WriteAllText(temp, JsonSerializer.Serialize(settings, Options));
            File.Move(temp, _path, overwrite: true);
        }
    }
}

/// <summary>
/// Speech assets on disk, keyed by the content-addressed cache key.
/// </summary>
/// <remarks>
/// Audio and its timings are written together and deleted together. Splitting them is how a clip ends
/// up paired with another clip's word boundaries — a highlight that is confidently, smoothly wrong.
/// </remarks>
public sealed class DiskSpeechAssetStore : ISpeechAssetStore
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = false,
        Converters = { new FollowModeJsonConverter(), new JsonStringEnumConverter() },
    };

    private readonly string _root;

    public DiskSpeechAssetStore(string? root = null)
    {
        _root = root ?? XdgPaths.SpeechCache;
        Directory.CreateDirectory(_root);
    }

    public string Root => _root;

    private string AssetDirectory(string assetId) => Path.Combine(_root, Bucket(assetId), assetId);
    private string MetaPath(string assetId) => Path.Combine(AssetDirectory(assetId), "asset.json");
    private string PcmPath(string assetId) => Path.Combine(AssetDirectory(assetId), "audio.pcm");

    /// <summary>Two-character bucket so one directory does not accumulate hundreds of thousands of entries.</summary>
    private static string Bucket(string assetId)
        => assetId.Length >= 2 ? assetId[..2] : "00";

    public Task<SpeechAsset?> TryGetAsync(string assetId, CancellationToken ct = default)
        => Task.Run(() =>
        {
            try
            {
                string meta = MetaPath(assetId);
                string pcm = PcmPath(assetId);
                // Both halves must exist. A metadata file without audio, or audio without metadata, is
                // a partial write and must be treated as a miss.
                if (!File.Exists(meta) || !File.Exists(pcm)) return null;
                var asset = JsonSerializer.Deserialize<SpeechAsset>(File.ReadAllText(meta), JsonOptions);
                if (asset is null) return null;
                long frames = new FileInfo(pcm).Length / Math.Max(1, asset.Format.BytesPerFrame);
                // The stored length must still agree with the file. If it does not, the cache is stale
                // or corrupt and must not be trusted to time a highlight.
                return frames == asset.FrameCount ? asset : null;
            }
            catch { return null; }
        }, ct);

    public Task StoreAsync(SpeechAsset asset, ReadOnlyMemory<byte> pcm, CancellationToken ct = default)
        => Task.Run(() =>
        {
            string dir = AssetDirectory(asset.Id);
            Directory.CreateDirectory(dir);
            // PCM first via a unique temp + rename, metadata last: the meta file is the commit
            // point, and TryGet's length check rejects anything torn. Unique temps keep concurrent
            // stores of the same asset from colliding mid-write.
            string pcmTemp = Path.Combine(dir, $"audio.{Guid.NewGuid():N}.tmp");
            string metaTemp = Path.Combine(dir, $"asset.{Guid.NewGuid():N}.tmp");
            try
            {
                File.WriteAllBytes(pcmTemp, pcm.ToArray());
                File.Move(pcmTemp, PcmPath(asset.Id), overwrite: true);
                File.WriteAllText(metaTemp, JsonSerializer.Serialize(asset, JsonOptions));
                File.Move(metaTemp, MetaPath(asset.Id), overwrite: true);
            }
            finally
            {
                try { File.Delete(pcmTemp); } catch { }
                try { File.Delete(metaTemp); } catch { }
            }
        }, ct);

    public Task<ReadOnlyMemory<byte>?> TryLoadPcmAsync(string assetId, CancellationToken ct = default)
        => Task.Run<ReadOnlyMemory<byte>?>(() =>
        {
            try
            {
                // Validate against the metadata, as TryGet does: raw PCM whose length no longer
                // matches the asset must not be paired with that asset's timings.
                string meta = MetaPath(assetId);
                string path = PcmPath(assetId);
                if (!File.Exists(meta) || !File.Exists(path)) return null;
                var asset = JsonSerializer.Deserialize<SpeechAsset>(File.ReadAllText(meta), JsonOptions);
                if (asset is null) return null;
                var bytes = File.ReadAllBytes(path);
                long frames = bytes.Length / Math.Max(1, asset.Format.BytesPerFrame);
                // Not a ternary: byte[] converts implicitly to ReadOnlyMemory<byte>, which would
                // turn the null arm into a default (empty) memory instead of a null result.
                if (frames != asset.FrameCount || bytes.Length % Math.Max(1, asset.Format.BytesPerFrame) != 0) return null;
                if (asset.PcmHash is { } hash && CacheKey.Sha256Hex(bytes) != hash) return null;
                return bytes;
            }
            catch { return null; }
        }, ct);

    public Task InvalidateAsync(string documentContentHash, CancellationToken ct = default)
    {
        // Deliberately a no-op. Cache keys are content-addressed and fold in the segment's script
        // revision and spoken text, so an edited segment simply addresses a new asset and the old
        // one is orphaned, never reused. Assets carry no document hash to scope a deletion by, and
        // the previous behaviour — deleting every cached asset of every document on any edit —
        // forced a full re-synthesis for unrelated books. Orphan reclamation is a cache-GC concern,
        // not correctness. The coordinator still calls this (its contract test asserts the call).
        _ = documentContentHash;
        ct.ThrowIfCancellationRequested();
        return Task.CompletedTask;
    }

    /// <summary>Total bytes held, for the settings panel's cache indicator.</summary>
    public long TotalBytes()
    {
        try
        {
            return Directory.Exists(_root)
                ? Directory.EnumerateFiles(_root, "*", SearchOption.AllDirectories).Sum(f => new FileInfo(f).Length)
                : 0;
        }
        catch { return 0; }
    }
}

/// <summary>Plans and reading positions on disk, keyed by document content hash.</summary>
public sealed class DiskPlanStore : IPlanStore
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = false,
        Converters = { new FollowModeJsonConverter(), new JsonStringEnumConverter() },
    };

    private readonly string _root;

    public DiskPlanStore(string? root = null)
    {
        _root = root ?? Path.Combine(XdgPaths.Data, "plans");
        Directory.CreateDirectory(_root);
    }

    private string Dir(string hash) => Path.Combine(_root, hash);

    public Task<NarrationPlan?> TryGetPlanAsync(string documentContentHash, CancellationToken ct = default)
        => Task.Run(() => Read<NarrationPlan>(Path.Combine(Dir(documentContentHash), "plan.json")), ct);

    public Task StorePlanAsync(NarrationPlan plan, CancellationToken ct = default)
        => Task.Run(() => Write(Path.Combine(Dir(plan.Header.Extraction.DocumentContentHash), "plan.json"), plan), ct);

    public Task<ReaderPosition?> TryGetPositionAsync(string documentContentHash, CancellationToken ct = default)
        => Task.Run(() => Read<ReaderPosition>(Path.Combine(Dir(documentContentHash), "position.json")), ct);

    public Task SavePositionAsync(ReaderPosition position, CancellationToken ct = default)
        => Task.Run(() => Write(Path.Combine(Dir(position.DocumentContentHash), "position.json"), position), ct);

    private T? Read<T>(string path) where T : class
    {
        try
        {
            if (!File.Exists(path)) return null;
            return JsonSerializer.Deserialize<T>(File.ReadAllText(path), JsonOptions);
        }
        catch { return null; }   // a corrupt plan is a cache miss, not a startup failure
    }

    private void Write<T>(string path, T value)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        // Unique temp: concurrent plan/position writes must not collide on a shared temp name.
        string temp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            File.WriteAllText(temp, JsonSerializer.Serialize(value, JsonOptions));
            File.Move(temp, path, overwrite: true);
        }
        finally
        {
            try { File.Delete(temp); } catch { }
        }
    }
}

/// <summary>Settings-side mirror of the interpreter's thinking policy (Infrastructure does not reference Inference).</summary>
public enum MathThinkingPolicy
{
    Off,
    EquationsOnly,
    Always,
}

/// <summary>
/// Reads <see cref="FollowMode"/> tolerantly. The two old following modes, <c>KeepVisible</c> and
/// <c>ReadingRail</c>, became one; a settings file or saved position naming either must still load
/// (as <see cref="FollowMode.Follow"/>) rather than fail and reset every preference to its default.
/// </summary>
public sealed class FollowModeJsonConverter : JsonConverter<FollowMode>
{
    public override FollowMode Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType == JsonTokenType.Number)
            return reader.TryGetInt32(out int n) && n == 0 ? FollowMode.Off : FollowMode.Follow;
        string? name = reader.GetString();
        return string.Equals(name, nameof(FollowMode.Off), StringComparison.OrdinalIgnoreCase) ? FollowMode.Off : FollowMode.Follow;
    }

    public override void Write(Utf8JsonWriter writer, FollowMode value, JsonSerializerOptions options)
        => writer.WriteStringValue(value.ToString());
}
