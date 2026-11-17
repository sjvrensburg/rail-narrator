using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace RailNarrator.Infrastructure;

/// <summary>One downloadable or expected model asset.</summary>
/// <param name="Id">Stable identifier used on disk and in settings.</param>
/// <param name="Description">What it is for, in user-facing terms.</param>
/// <param name="Url">Where to fetch it. Never fetched without explicit user approval.</param>
/// <param name="Revision">The exact upstream release or commit the URL points at.</param>
/// <param name="Bytes">Size, so the UI can show what a download will cost before starting it.</param>
/// <param name="Sha256">Expected digest, or null when not yet verified on this machine.</param>
/// <param name="Licence">The asset's own licence. Weights, code and voice data are licensed separately.</param>
/// <param name="Required">True when the application cannot narrate without it.</param>
/// <param name="FileName">Name to store it under.</param>
public sealed record ModelAsset(
    string Id,
    string Description,
    string Url,
    string Revision,
    long Bytes,
    string? Sha256,
    string Licence,
    bool Required,
    string FileName)
{
    public string HumanSize => Bytes switch
    {
        >= 1_000_000_000 => $"{Bytes / 1_000_000_000.0:F2} GB",
        >= 1_000_000 => $"{Bytes / 1_000_000.0:F1} MB",
        >= 1_000 => $"{Bytes / 1_000.0:F1} kB",
        _ => $"{Bytes} B",
    };
};

/// <summary>
/// The model manifest: exact revisions, checksums, sizes and licences for every asset this build
/// expects.
/// </summary>
/// <remarks>
/// <para>
/// Models are never bundled in the application package and never fetched implicitly. The UI shows
/// <see cref="ModelAsset.HumanSize"/> and the licence before a download starts, and a user approves
/// each one. Downloading a whole model repository when one file is needed is exactly the kind of
/// surprise this avoids.
/// </para>
/// <para>
/// Sizes and digests here were measured on the reference machine, not copied from a web page, so a
/// corrupt or truncated download is detectable rather than silently producing bad audio.
/// </para>
/// </remarks>
public static class ModelManifest
{
    /// <summary>Kokoro ONNX weights used by the default in-process provider.</summary>
    public static ModelAsset KokoroFp16 { get; } = new(
        Id: "kokoro-fp16",
        Description: "Kokoro-82M text-to-speech model, fp16 ONNX export. Default speech engine.",
        Url: "https://github.com/Lyrcaxis/KokoroSharpBinaries/releases/download/v2.0.0/kokoro-fp16.onnx",
        Revision: "KokoroSharpBinaries v2.0.0",
        Bytes: 163_636_560,
        Sha256: "027a25b14aef7d3ae57fd09301ebefbec868e79d55213d07e4f3af442f5ba352",
        Licence: "Apache-2.0 (model weights); KokoroSharp runtime MIT",
        Required: true,
        FileName: "kokoro-fp16.onnx");

    /// <summary>
    /// Kokoro voices. Shipped inside the KokoroSharp NuGet package rather than downloaded, because the
    /// package already carries them and fetching a separate copy would be redundant.
    /// </summary>
    public static ModelAsset KokoroVoices { get; } = new(
        Id: "kokoro-voices",
        Description: "61 Kokoro voice vectors. Supplied by the KokoroSharp NuGet package, not downloaded separately.",
        Url: "nuget://KokoroSharp/0.8.4/content/voices",
        Revision: "KokoroSharp 0.8.4",
        Bytes: 82_837_504,
        Sha256: null,
        Licence: "Apache-2.0",
        Required: true,
        FileName: "voices/");

    /// <summary>ZONOS3-Mini backbone. Only needed if the user selects the ZONOS3 provider.</summary>
    public static ModelAsset ZonosBackbone { get; } = new(
        Id: "zonos-backbone-ptq",
        Description: "ZONOS3-Mini language-model backbone, post-training quantised (Q4_0 experts, Q8_0 mixers).",
        Url: "https://huggingface.co/Zyphra/ZONOS3-Mini",
        Revision: "ZONOS3-Mini, converted with tools/convert_gguf.py lm",
        Bytes: 663_150_624,
        Sha256: "66fceb6d73a8c30bde80249e4112bb308380ab7f3384fa11c8e4048115e4ceec",
        Licence: "Apache-2.0 (weights); zonos3.cpp runtime MIT",
        Required: false,
        FileName: "backbone-ptq.gguf");

    /// <summary>ZONOS3 full-size waveform decoder. The small decoder has an audible edge.</summary>
    public static ModelAsset ZonosDecoderFull { get; } = new(
        Id: "zonos-zac-full",
        Description: "ZONOS3 full-size waveform decoder (head variant full_mr).",
        Url: "https://huggingface.co/Zyphra/ZONOS3-Mini",
        Revision: "ZONOS3-Mini, converted with tools/convert_gguf.py codec-decoder",
        Bytes: 2_199_903_264,
        Sha256: "9dc8e29cb681dae320bd77329c0f7a39806ad5de6c231ad111adab1a1f6774d8",
        Licence: "Apache-2.0 (weights); zonos3.cpp runtime MIT",
        Required: false,
        FileName: "zac.gguf");

    public static IReadOnlyList<ModelAsset> All { get; } = [KokoroFp16, KokoroVoices, ZonosBackbone, ZonosDecoderFull];

    public static ModelAsset? Find(string id) => All.FirstOrDefault(a => a.Id == id);

    /// <summary>Total download size of the required assets, shown before anything is fetched.</summary>
    public static long RequiredDownloadBytes => All.Where(a => a.Required && a.Url.StartsWith("http", StringComparison.Ordinal)).Sum(a => a.Bytes);
}

/// <summary>Progress of a model download.</summary>
public sealed record DownloadProgress(string AssetId, long BytesReceived, long BytesTotal, string Phase);

/// <summary>
/// Downloads model assets with size disclosure, digest verification and resumable-free simplicity.
/// </summary>
/// <remarks>
/// Verification is not optional. A truncated download produces a model that loads and then emits
/// silence or garbage, and the failure surfaces as "the reader says nothing" long after the download
/// that caused it.
/// </remarks>
public sealed class ModelDownloader
{
    private readonly HttpClient _http;
    private readonly bool _ownsClient;
    private readonly string _destination;

    public ModelDownloader(string? destinationDirectory = null, HttpClient? httpClient = null)
    {
        _destination = destinationDirectory ?? XdgPaths.Models;
        Directory.CreateDirectory(_destination);
        if (httpClient is not null) { _http = httpClient; _ownsClient = false; }
        else { _http = new HttpClient { Timeout = Timeout.InfiniteTimeSpan }; _ownsClient = true; }
    }

    public string Destination => _destination;

    /// <summary>True when the asset is present and, where a digest is known, matches it.</summary>
    public bool IsPresent(ModelAsset asset)
    {
        string path = PathFor(asset);
        if (!File.Exists(path)) return false;
        if (asset.Sha256 is null) return true;
        return Verify(path, asset.Sha256);
    }

    public string PathFor(ModelAsset asset) => Path.Combine(_destination, asset.FileName);

    /// <summary>Verifies a file's SHA-256 against the manifest.</summary>
    public static bool Verify(string path, string expectedSha256)
    {
        try
        {
            using var stream = File.OpenRead(path);
            using var sha = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            var buffer = new byte[1024 * 1024];
            int read;
            while ((read = stream.Read(buffer)) > 0) sha.AppendData(buffer.AsSpan(0, read));
            string actual = Convert.ToHexString(sha.GetHashAndReset()).ToLowerInvariant();
            return string.Equals(actual, expectedSha256, StringComparison.OrdinalIgnoreCase);
        }
        catch { return false; }
    }

    /// <summary>
    /// Downloads an asset. Never overwrites a verified existing file.
    /// </summary>
    /// <exception cref="InvalidOperationException">The digest did not match, or the size was wrong.</exception>
    public async Task<string> DownloadAsync(ModelAsset asset, IProgress<DownloadProgress>? progress = null, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(asset);
        if (!asset.Url.StartsWith("http", StringComparison.Ordinal))
            throw new InvalidOperationException($"Asset '{asset.Id}' is not downloadable; it is supplied by {asset.Url}.");

        string path = PathFor(asset);
        if (IsPresent(asset)) return path;

        string temp = path + ".part";
        progress?.Report(new DownloadProgress(asset.Id, 0, asset.Bytes, "connecting"));

        try
        {
            using var response = await _http.GetAsync(asset.Url, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
            response.EnsureSuccessStatusCode();

            long total = response.Content.Headers.ContentLength ?? asset.Bytes;
            progress?.Report(new DownloadProgress(asset.Id, 0, total, $"downloading {asset.HumanSize}"));

            await using (var source = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false))
            await using (var sink = File.Create(temp))
            {
                var buffer = new byte[256 * 1024];
                long received = 0;
                int read;
                while ((read = await source.ReadAsync(buffer, ct).ConfigureAwait(false)) > 0)
                {
                    await sink.WriteAsync(buffer.AsMemory(0, read), ct).ConfigureAwait(false);
                    received += read;
                    progress?.Report(new DownloadProgress(asset.Id, received, total, "downloading"));
                }
            }

            progress?.Report(new DownloadProgress(asset.Id, asset.Bytes, asset.Bytes, "verifying"));
            long actual = new FileInfo(temp).Length;
            if (actual != asset.Bytes)
                throw new InvalidOperationException(
                    $"Downloaded {actual} bytes for '{asset.Id}' but the manifest expects {asset.Bytes}. The download is incomplete or the release has changed.");
            if (asset.Sha256 is { } expected && !Verify(temp, expected))
                throw new InvalidOperationException($"Checksum mismatch for '{asset.Id}'. The file was not kept.");

            File.Move(temp, path, overwrite: true);
            progress?.Report(new DownloadProgress(asset.Id, asset.Bytes, asset.Bytes, "complete"));
            return path;
        }
        finally
        {
            // A failed or cancelled download must not leave a file that later looks like a real asset.
            try { if (File.Exists(temp)) File.Delete(temp); } catch { }
        }
    }

    public void Dispose()
    {
        if (_ownsClient) _http.Dispose();
    }
}

/// <summary>Status of a managed sidecar process.</summary>
public enum SidecarState { NotStarted, Starting, Ready, Failed, Stopped }

/// <summary>
/// Lifecycle for a local sidecar server (the ZONOS3 ggml runtime).
/// </summary>
/// <remarks>
/// <para>
/// Managed separately from the GUI process on purpose. The server holds gigabytes of model weights and
/// its own thread pool; letting it inherit the UI's lifetime or its working directory makes both
/// harder to reason about.
/// </para>
/// <para>
/// Rules this enforces: bind to loopback only, pin the binary and model revisions the user selected,
/// wait for a real health check rather than a fixed sleep, keep only a bounded tail of the child's
/// output for diagnostics, and terminate only the process this instance started. A server the user
/// runs themselves is adopted through <see cref="UseExisting"/> and never killed.
/// </para>
/// <para>
/// No argument is ever interpolated from document text or model output into a command line, and the
/// process is started with an argument array rather than a shell string, so a PDF cannot inject a
/// command.
/// </para>
/// </remarks>
public sealed class SidecarManager : IAsyncDisposable
{
    private readonly SidecarOptions _options;
    private readonly HttpClient _http;
    private Process? _process;
    private readonly LinkedList<string> _log = new();
    private readonly object _gate = new();
    private bool _ownsProcess;

    public SidecarManager(SidecarOptions options, HttpClient? httpClient = null)
    {
        _options = options ?? throw new ArgumentNullException(nameof(options));

        if (_options.ModelPaths is null || _options.ModelPaths.Count == 0)
            throw new ArgumentException("At least the backbone model path is required.", nameof(options));
        // Binding anywhere other than loopback would expose a model endpoint that accepts arbitrary
        // text to every host on the network. This is a hard requirement, not a default.
        if (_options.BindHost is not ("127.0.0.1" or "localhost" or "::1"))
            throw new ArgumentException($"The sidecar must bind to loopback only, not '{_options.BindHost}'.", nameof(options));
        if (_options.Port is < 1 or > 65535)
            throw new ArgumentOutOfRangeException(nameof(options), "Port must be a valid TCP port.");

        _http = httpClient ?? new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
    }

    public SidecarState State { get; private set; } = SidecarState.NotStarted;

    public string? FailureReason { get; private set; }

    /// <summary>The bounded tail of the sidecar's combined output, for a diagnostics pane.</summary>
    public IReadOnlyList<string> RecentLog { get { lock (_gate) return _log.ToArray(); } }

    public string BaseUrl => $"http://{_options.BindHost}:{_options.Port}";

    /// <summary>
    /// Adopts an already-running server instead of starting one. Rail Narrator must never terminate a
    /// process it did not start.
    /// </summary>
    public async Task<bool> UseExistingAsync(CancellationToken ct = default)
    {
        if (await ProbeHealthAsync(ct).ConfigureAwait(false))
        {
            State = SidecarState.Ready;
            _ownsProcess = false;
            return true;
        }
        return false;
    }

    public async Task<bool> StartAsync(CancellationToken ct = default)
    {
        if (State == SidecarState.Ready) return true;
        if (await UseExistingAsync(ct).ConfigureAwait(false)) return true;

        if (!File.Exists(_options.Executable))
        {
            FailureReason = $"Sidecar executable not found at '{_options.Executable}'.";
            State = SidecarState.Failed;
            return false;
        }
        foreach (string model in _options.ModelPaths)
        {
            if (!File.Exists(model))
            {
                FailureReason = $"Required model not found: '{model}'.";
                State = SidecarState.Failed;
                return false;
            }
        }

        State = SidecarState.Starting;
        try
        {
            var startInfo = new ProcessStartInfo
            {
                FileName = _options.Executable,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
                WorkingDirectory = _options.WorkingDirectory ?? Path.GetDirectoryName(_options.Executable)!,
            };
            // An argument list, never a shell string: document text and model output must have no path
            // into a command line.
            foreach (string arg in BuildArguments()) startInfo.ArgumentList.Add(arg);

            _process = new Process { StartInfo = startInfo, EnableRaisingEvents = true };
            _process.OutputDataReceived += (_, e) => { if (e.Data is not null) AppendLog(e.Data); };
            _process.ErrorDataReceived += (_, e) => { if (e.Data is not null) AppendLog(e.Data); };
            _process.Exited += (_, _) =>
            {
                if (State == SidecarState.Ready || State == SidecarState.Starting)
                {
                    State = SidecarState.Failed;
                    FailureReason = $"Sidecar exited with code {_process.ExitCode}. See the diagnostics log.";
                }
            };

            _process.Start();
            _process.BeginOutputReadLine();
            _process.BeginErrorReadLine();
            _ownsProcess = true;
        }
        catch (Exception ex)
        {
            FailureReason = $"Could not start the sidecar: {ex.Message}";
            State = SidecarState.Failed;
            return false;
        }

        // Wait for a real health check. A fixed sleep either wastes time or races a slow model load,
        // and a 2.9 GB decoder takes seconds to read.
        var deadline = DateTime.UtcNow + _options.StartupTimeoutValue;
        while (DateTime.UtcNow < deadline)
        {
            ct.ThrowIfCancellationRequested();
            if (_process.HasExited)
            {
                FailureReason = $"Sidecar exited during startup with code {_process.ExitCode}. Last output: {string.Join(" | ", RecentLog.TakeLast(3))}";
                State = SidecarState.Failed;
                return false;
            }
            if (await ProbeHealthAsync(ct).ConfigureAwait(false))
            {
                State = SidecarState.Ready;
                FailureReason = null;
                return true;
            }
            await Task.Delay(250, ct).ConfigureAwait(false);
        }

        FailureReason = $"Sidecar did not become healthy within {_options.StartupTimeoutValue.TotalSeconds:F0} s.";
        await StopAsync().ConfigureAwait(false);
        State = SidecarState.Failed;
        return false;
    }

    private IEnumerable<string> BuildArguments()
    {
        yield return _options.ModelPaths[0];           // backbone
        yield return "--port";
        yield return _options.Port.ToString(System.Globalization.CultureInfo.InvariantCulture);
        yield return "--host";
        yield return _options.BindHost;                // loopback only
        foreach (string extra in _options.Arguments) yield return extra;
    }

    public async Task<bool> ProbeHealthAsync(CancellationToken ct = default)
    {
        try
        {
            using var response = await _http.GetAsync($"{BaseUrl}/v2/health", ct).ConfigureAwait(false);
            return response.IsSuccessStatusCode;
        }
        catch (OperationCanceledException) { throw; }
        catch { return false; }
    }

    /// <summary>Stops the sidecar, but only when this instance started it.</summary>
    public async Task StopAsync()
    {
        var process = _process;
        _process = null;
        if (process is null || !_ownsProcess)
        {
            State = SidecarState.Stopped;
            return;
        }
        try
        {
            if (!process.HasExited)
            {
                // Ask first: a graceful shutdown lets the runtime release its model memory and any GPU
                // allocation, which a kill can leave behind.
                process.CloseMainWindow();
                if (!process.WaitForExit((int)_options.ShutdownTimeoutValue.TotalMilliseconds))
                    process.Kill(entireProcessTree: true);
            }
        }
        catch { /* a process that is already gone is the outcome we wanted */ }
        finally
        {
            process.Dispose();
            State = SidecarState.Stopped;
        }
        await Task.CompletedTask.ConfigureAwait(false);
    }

    private void AppendLog(string line)
    {
        lock (_gate)
        {
            _log.AddLast(line.Length > 500 ? line[..500] : line);
            // Bounded: an unbounded log of a chatty server grows without limit for the life of the app.
            while (_log.Count > _options.MaxLogLines) _log.RemoveFirst();
        }
    }

    public async ValueTask DisposeAsync()
    {
        await StopAsync().ConfigureAwait(false);
        _http.Dispose();
    }
}

/// <summary>Sidecar launch configuration.</summary>
/// <param name="Executable">Absolute path to the server binary.</param>
/// <param name="ModelPaths">Backbone first, then any extra model files to pass through.</param>
/// <param name="ExtraArguments">Additional flags, e.g. a decoder or a speaker-encoder path.</param>
/// <param name="Port">Loopback port.</param>
/// <param name="BindHost">Must stay loopback. Binding elsewhere would expose the endpoint to the network.</param>
/// <param name="StartupTimeout">How long to wait for the health check.</param>
/// <param name="ShutdownTimeout">How long to wait for a graceful exit before killing.</param>
/// <param name="MaxLogLines">Bounded diagnostic tail.</param>
/// <param name="WorkingDirectory">Defaults to the executable's directory.</param>
public sealed record SidecarOptions(
    string Executable,
    IReadOnlyList<string> ModelPaths,
    IReadOnlyList<string>? ExtraArguments = null,
    int Port = 19191,
    string BindHost = "127.0.0.1",
    TimeSpan? StartupTimeout = null,
    TimeSpan? ShutdownTimeout = null,
    int MaxLogLines = 400,
    string? WorkingDirectory = null)
{
    public IReadOnlyList<string> ExtraArgumentsSafe => ExtraArguments ?? [];
    public TimeSpan StartupTimeoutValue => StartupTimeout ?? TimeSpan.FromSeconds(120);
    public TimeSpan ShutdownTimeoutValue => ShutdownTimeout ?? TimeSpan.FromSeconds(5);

    public IReadOnlyList<string> Arguments => ExtraArguments ?? [];
}
