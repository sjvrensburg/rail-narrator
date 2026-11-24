using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;
using RailNarrator.Domain;

namespace RailNarrator.Speech;

/// <summary>Where Phonon is installed and how it should speak.</summary>
/// <param name="LibraryPath">Gradium's C library, <see cref="PhononTtsProvider.LibraryFileName"/> (tools/setup-phonon.sh builds it).</param>
/// <param name="ModelDirectory">The checkpoint folder: config.json, tokenizer.json, model.q8.gguf, voices/.</param>
/// <param name="DefaultVoice">Spoken when the voice asked for is not installed.</param>
/// <param name="Threads">CPU threads. 4 was as fast as 8 on the i7-12700H (ADR-0009).</param>
/// <param name="Seed">Sampling seed, the runtime's own default. Phonon samples noise per frame.</param>
public sealed record PhononProviderSettings(
    string LibraryPath,
    string ModelDirectory,
    string DefaultVoice = PhononTtsProvider.DefaultVoice,
    int Threads = 4,
    ulong Seed = 4242424242424242,
    bool TrimLeadingSilence = true,
    float SilenceThreshold = 0.005f);

/// <summary>
/// Phonon (Gradium's phonon-31m-en), in process through Gradium's own runtime, as a second engine
/// beside Kokoro (ADR-0009).
/// </summary>
/// <remarks>
/// <para>
/// Measured on the i7-12700H over 40 passages of a real paper: real-time factor 0.061 (Kokoro 0.224
/// on the CPU, 0.258 on WebGPU), model loaded in 57 ms, first audio after 32 ms. MFA timed every
/// word of 34 of the 40 passages (Kokoro 35), so word highlighting works as with Kokoro.
/// </para>
/// <para>
/// The model returns audio only: no word timings (MFA supplies them) and no phonemes (the
/// planner's pronunciation dictionary works on text, so it still applies). Its runtime splits a
/// long text into sentences itself. Audio is not repeatable byte for byte between runs, even with
/// the same seed; that is harmless, because a clip is synthesised once and cached, and its word
/// timing is keyed by the clip's own hash.
/// </para>
/// <para>
/// The C library's calls block and must come from one thread at a time, so every call goes through
/// one gate, off the caller's thread. Text normalisation is the runtime's English rules: on the
/// application's already-spoken scripts it changed punctuation only.
/// </para>
/// </remarks>
public sealed class PhononTtsProvider : ITtsProvider
{
    public const string Id = "phonon";

    /// <summary>The voice a first run speaks in: Maeve, a British English adult voice.</summary>
    public const string DefaultVoice = "Maeve";

    /// <summary>
    /// The file cargo builds for Gradium's <c>ptts-ffi</c> crate on this platform: no <c>lib</c>
    /// prefix on Windows.
    /// </summary>
    public static string LibraryFileName
        => OperatingSystem.IsWindows() ? "ptts_ffi.dll" : OperatingSystem.IsMacOS() ? "libptts_ffi.dylib" : "libptts_ffi.so";

    public const int OutputSampleRate = 24000;

    /// <summary>The runtime splits text into sentences itself; this only bounds one request.</summary>
    public const int MaxRequestChars = 8000;

    private readonly PhononProviderSettings _settings;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private Native? _native;
    private nint _handle;
    private string? _initError;
    private IReadOnlyList<TtsVoice> _voices = [];
    private string? _currentVoice;
    private string _revision = "phonon";
    private bool _disposed;
    // The attention head that follows the text in the installed model, when it is one whose head is
    // known and the library can report it (tools/phonon-word-timing.patch); null otherwise.
    private (uint Layer, uint Head)? _alignmentHead;

    /// <summary>
    /// The head that follows the text, by the model's signature in its config.json. Found by
    /// scoring every head (ADR-0013); another model, or another revision of this one, has its own.
    /// </summary>
    private static readonly Dictionary<string, (uint Layer, uint Head)> AlignmentHeads = new(StringComparer.Ordinal)
    {
        ["5f0a2aab"] = (3, 0),   // phonon-31m-en
    };

    /// <summary>Samples of audio in one frame the model generates: 80 ms at 24 kHz.</summary>
    public const int FrameSamples = 1920;

    public PhononTtsProvider(PhononProviderSettings settings)
        => _settings = settings ?? throw new ArgumentNullException(nameof(settings));

    public string ProviderId => Id;

    /// <summary>Installed: the library and the model folder both exist. Says nothing about whether they load.</summary>
    public static bool IsInstalled(string libraryPath, string modelDirectory)
        => File.Exists(libraryPath) && File.Exists(Path.Combine(modelDirectory, "config.json"));

    public async Task<TtsCapabilities> GetCapabilitiesAsync(CancellationToken ct = default)
    {
        string? error = await EnsureLoadedAsync(ct).ConfigureAwait(false);
        return new TtsCapabilities(
            Id, _revision,
            [AudioFormat.Mono24000Float32, new AudioFormat(OutputSampleRate, 1, SampleFormat.Int16LittleEndian)],
            SupportsStreaming: false,
            _alignmentHead is null ? TimingProvenance.None : TimingProvenance.ModelAttention,
            // Rate is the playback engine's (WSOLA), as for Kokoro: the C library has no rate control.
            SupportsSynthesisRate: false,
            MaxRequestChars,
            ["en-GB", "en-US"],
            Unavailable: error);
    }

    public async Task<IReadOnlyList<TtsVoice>> ListVoicesAsync(CancellationToken ct = default)
    {
        await EnsureLoadedAsync(ct).ConfigureAwait(false);
        return _voices;
    }

    public async Task<TtsSynthesisResult> SynthesizeAsync(TtsSynthesisRequest request, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (await EnsureLoadedAsync(ct).ConfigureAwait(false) is { } error) throw new InvalidOperationException(error);
        if (request.Format.SampleRate != OutputSampleRate)
            throw new InvalidOperationException(
                $"Phonon speaks at {OutputSampleRate} Hz; the caller asked for {request.Format.SampleRate} Hz. Resampling belongs to the audio engine.");
        string text = request.Text.Trim();
        if (text.Length == 0)
            return new TtsSynthesisResult(ReadOnlyMemory<byte>.Empty, request.Format, 0, [], TimingProvenance.None, null, null, ["Empty text."]);
        if (text.Length > MaxRequestChars)
            throw new ArgumentException($"Text exceeds the {MaxRequestChars}-character limit; split it before synthesis.", nameof(request));

        var warnings = new List<string>();
        // A voice of another engine (a document last read with Kokoro restores its voice) is not an
        // error: speak in the default voice and say so.
        string voice = _voices.Any(v => v.Id == request.VoiceId) ? request.VoiceId : _settings.DefaultVoice;
        if (voice != request.VoiceId) warnings.Add($"Phonon has no voice '{request.VoiceId}'; spoke in {voice}.");

        float[] samples;
        List<AttentionWordTiming.Chunk>? attention;
        bool timed = request.RequestTimings && _alignmentHead is not null;
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            (samples, attention) = await Task.Run(() => Speak(text, voice, timed, ct), ct).ConfigureAwait(false);
        }
        finally { _gate.Release(); }
        ct.ThrowIfCancellationRequested();
        if (samples.Length == 0)
            throw new InvalidOperationException("Phonon returned no samples for this text.");

        // Word times from the model's own attention, in the clip as it was generated.
        IReadOnlyList<TimedCue> cues = [];
        if (attention is not null)
        {
            var words = AttentionWordTiming.Words(text, attention, AttentionWordTiming.Envelope(samples, OutputSampleRate),
                OutputSampleRate, FrameSamples, samples.LongLength, out string? problem);
            if (words is null) warnings.Add($"No word timing from the model's attention: {problem}.");
            else cues = words;
        }

        int trimmed = 0;
        if (_settings.TrimLeadingSilence)
        {
            // As for Kokoro: the clip's first frame is the start of speech, so a whole-clip cue lines
            // up with what is heard. Never the whole clip: a quiet passage must not vanish.
            int first = 0;
            while (first < samples.Length && Math.Abs(samples[first]) < _settings.SilenceThreshold) first++;
            if (first > 0 && first < samples.Length * 0.9) { samples = samples[first..]; trimmed = first; }
        }
        if (cues.Count > 0)
        {
            // The cues move with the audio, and their ranges are in the caller's text, not the trimmed copy.
            int lead = request.Text.Length - request.Text.TrimStart().Length;
            cues = cues.Select(c => c with
            {
                StartSample = Math.Max(0, c.StartSample - trimmed),
                EndSample = Math.Max(0, c.EndSample - trimmed),
                SpokenRange = c.SpokenRange is { } r ? new TextRange(r.Start + lead, r.Length) : null,
            }).ToArray();
            // A first word that began inside what was trimmed keeps a time of its own.
            if (cues[0].EndSample - cues[0].StartSample < TimedCueValidator.MinimumCueSamples)
            {
                warnings.Add("No word timing from the model's attention: the first word fell in the silence that was trimmed.");
                cues = [];
            }
        }

        var format = new AudioFormat(OutputSampleRate, 1, request.Format.SampleFormat);
        int stride = Pcm.BytesPerSample(format.SampleFormat);
        var pcm = new byte[samples.Length * stride];
        for (int i = 0; i < samples.Length; i++) Pcm.WriteSample(pcm, i * stride, format.SampleFormat, samples[i]);
        return new TtsSynthesisResult(pcm, format, samples.LongLength, cues,
            cues.Count > 0 ? TimingProvenance.ModelAttention : TimingProvenance.None, (long)_settings.Seed, "phonon-ptts", warnings);
    }

    public async Task<ProviderSelfTestResult> SelfTestAsync(string voiceId, CancellationToken ct = default)
    {
        long start = Environment.TickCount64;
        try
        {
            var caps = await GetCapabilitiesAsync(ct).ConfigureAwait(false);
            if (!caps.IsAvailable) return new ProviderSelfTestResult(false, 0, 0, Environment.TickCount64 - start, caps.Unavailable);
            var result = await SynthesizeAsync(new TtsSynthesisRequest("Testing one, two, three.", voiceId, "en-GB", AudioFormat.Mono24000Float32), ct).ConfigureAwait(false);
            float peak = Pcm.Peak(result.Pcm.Span, result.Format);
            string? detail = peak < ProviderSelfTestResult.SilencePeakThreshold ? $"Audio is effectively silent (peak {peak:F5})." : null;
            return new ProviderSelfTestResult(detail is null, result.DurationSeconds, peak, Environment.TickCount64 - start, detail);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex) { return new ProviderSelfTestResult(false, 0, 0, Environment.TickCount64 - start, ex.Message); }
    }

    public async ValueTask DisposeAsync()
    {
        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            if (_disposed) return;
            _disposed = true;
            if (_handle != 0 && _native is { } n) n.Free(_handle);
            _handle = 0;
        }
        finally { _gate.Release(); }
    }

    // ---- loading -----------------------------------------------------------------------------------

    /// <summary>Loads the library and the model once; returns why not, or null.</summary>
    private async Task<string?> EnsureLoadedAsync(CancellationToken ct)
    {
        if (_handle != 0) return null;
        if (_initError is not null) return _initError;
        await _gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (_handle != 0 || _initError is not null) return _initError;
            ObjectDisposedException.ThrowIf(_disposed, this);
            _initError = await Task.Run(Load, ct).ConfigureAwait(false);
            return _initError;
        }
        finally { _gate.Release(); }
    }

    private string? Load()
    {
        if (!File.Exists(_settings.LibraryPath))
            return $"Phonon's runtime is not installed at '{_settings.LibraryPath}'. Run tools/setup-phonon.sh.";
        if (!File.Exists(Path.Combine(_settings.ModelDirectory, "config.json")))
            return $"Phonon's model is not installed in '{_settings.ModelDirectory}'. Run tools/setup-phonon.sh.";
        try
        {
            // The runtime reads its thread count from the environment once, when it first starts.
            if (Environment.GetEnvironmentVariable("RAYON_NUM_THREADS") is null)
                Native.SetEnv("RAYON_NUM_THREADS", Math.Max(1, _settings.Threads).ToString());
            var native = new Native(_settings.LibraryPath);
            nint handle = native.New(_settings.ModelDirectory, "en");
            if (handle == 0) return $"Phonon could not load its model: {native.LastError(0) ?? "no reason given"}.";

            var names = native.Voices(handle);
            var info = ReadVoiceInfo(Path.Combine(_settings.ModelDirectory, "voices.tsv"));
            string fallback = names.Contains(_settings.DefaultVoice) ? _settings.DefaultVoice : names.FirstOrDefault(n => n != "default") ?? "default";
            _voices = names.Where(n => n != "default" || names.Count == 1)
                .Select(n => info.TryGetValue(n, out var v)
                    ? new TtsVoice(n, $"{n} ({(v.Locale == "en-gb" ? "British" : "American")}, {v.Gender})", v.Locale == "en-gb" ? "en-GB" : "en-US", v.Gender, n == fallback)
                    : new TtsVoice(n, n, "en", null, n == fallback))
                .OrderBy(v => v.Language == "en-GB" ? 0 : 1).ThenBy(v => v.Id, StringComparer.Ordinal)
                .ToList();
            _revision = $"phonon/{Fingerprint(_settings.LibraryPath)}/{Fingerprint(Path.Combine(_settings.ModelDirectory, "model.q8.gguf"))}";
            _alignmentHead = native.CanReportAttention && ModelSignature(Path.Combine(_settings.ModelDirectory, "config.json")) is { } signature
                && AlignmentHeads.TryGetValue(signature, out var head) ? head : null;
            // Timings are stored with the audio: a change to how they are made is a new revision.
            if (_alignmentHead is not null) _revision += "/" + AttentionWordTiming.Version;
            _native = native;
            _handle = handle;
            return null;
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException or BadImageFormatException or IOException)
        {
            return $"Phonon's runtime could not be loaded: {ex.Message}";
        }
    }

    /// <summary>The model's signature, "model_id.sig" in its config.json; null when it has none.</summary>
    private static string? ModelSignature(string configPath)
    {
        try
        {
            using var config = System.Text.Json.JsonDocument.Parse(File.ReadAllText(configPath));
            return config.RootElement.TryGetProperty("model_id", out var id) && id.TryGetProperty("sig", out var sig) ? sig.GetString() : null;
        }
        catch (Exception ex) when (ex is IOException or System.Text.Json.JsonException) { return null; }
    }

    /// <summary>The audio-identifying part of the cache key: what produced it, by content.</summary>
    private static string Fingerprint(string path)
        => File.Exists(path) ? CacheKey.Sha256HexOfFile(path)[..16] : "missing";

    private static Dictionary<string, (string Locale, string Gender)> ReadVoiceInfo(string path)
    {
        var info = new Dictionary<string, (string, string)>(StringComparer.Ordinal);
        if (!File.Exists(path)) return info;
        foreach (string line in File.ReadLines(path))
        {
            var parts = line.Split('\t');
            if (parts.Length >= 3) info[parts[0]] = (parts[1], parts[2]);
        }
        return info;
    }

    // ---- speaking ----------------------------------------------------------------------------------

    private (float[] Samples, List<AttentionWordTiming.Chunk>? Attention) Speak(string text, string voice, bool timed, CancellationToken ct)
    {
        var native = _native!;
        if (_currentVoice != voice)
        {
            if (!native.SetVoice(_handle, voice)) throw new InvalidOperationException($"Phonon could not switch to {voice}: {native.LastError(_handle)}");
            _currentVoice = voice;
        }
        native.SetSeed(_handle, _settings.Seed);
        var sink = new Sink(ct);
        var handle = GCHandle.Alloc(sink);
        try
        {
            bool spoke = timed && _alignmentHead is { } head
                ? native.SpeakAligned(_handle, text, head.Layer, head.Head, GCHandle.ToIntPtr(handle))
                : native.Speak(_handle, text, GCHandle.ToIntPtr(handle));
            if (!spoke && !ct.IsCancellationRequested)
                throw new InvalidOperationException($"Phonon could not speak this passage: {native.LastError(_handle)}");
        }
        finally { handle.Free(); }
        ct.ThrowIfCancellationRequested();
        return (sink.Samples.ToArray(), timed && !sink.Failed
            ? sink.Chunks.Select(c => new AttentionWordTiming.Chunk(c.Text, c.TokenEnds, c.Frames)).ToList()
            : null);
    }

    /// <summary>Collects one utterance's audio; asks the runtime to stop when cancelled.</summary>
    private sealed class Sink(CancellationToken ct)
    {
        public readonly List<float> Samples = new(OutputSampleRate * 10);
        public readonly CancellationToken Cancellation = ct;
        /// <summary>Each chunk of the text with the alignment head's attention for each of its frames.</summary>
        public readonly List<(string Text, int[] TokenEnds, List<float[]> Frames)> Chunks = [];
        /// <summary>A report could not be taken: the audio stands, the timing does not.</summary>
        public bool Failed;
    }

    /// <summary>Called by the runtime before a chunk's frames: its text and where each token ends in it.</summary>
    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static unsafe void OnChunk(byte* text, uint* tokenEnds, nuint count, nint user)
    {
        Sink? sink = null;
        try
        {
            sink = GCHandle.FromIntPtr(user).Target as Sink;
            if (sink is null) return;
            var ends = new int[checked((int)count)];
            for (int i = 0; i < ends.Length; i++) ends[i] = checked((int)tokenEnds[i]);
            sink.Chunks.Add((Marshal.PtrToStringUTF8((nint)text) ?? "", ends, []));
        }
        catch (Exception)
        {
            if (sink is not null) sink.Failed = true;   // nothing may unwind into native code
        }
    }

    /// <summary>Called by the runtime for each generated frame: the head's attention over the chunk's tokens.</summary>
    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static unsafe void OnAttention(float* scores, nuint count, nint user)
    {
        Sink? sink = null;
        try
        {
            sink = GCHandle.FromIntPtr(user).Target as Sink;
            if (sink is null) return;
            if (sink.Chunks.Count == 0) { sink.Failed = true; return; }
            sink.Chunks[^1].Frames.Add(new ReadOnlySpan<float>(scores, checked((int)count)).ToArray());
        }
        catch (Exception)
        {
            if (sink is not null) sink.Failed = true;
        }
    }

    /// <summary>Called by the runtime, on the thread that called ptts_speak, with each frame's audio.</summary>
    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static unsafe byte OnAudio(float* pcm, nuint count, nint user)
    {
        try
        {
            if (GCHandle.FromIntPtr(user).Target is not Sink sink) return 0;
            if (sink.Cancellation.IsCancellationRequested) return 0;
            sink.Samples.AddRange(new ReadOnlySpan<float>(pcm, checked((int)count)));
            return 1;
        }
        catch (Exception)
        {
            return 0;   // nothing may unwind into native code
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct PttsResult
    {
        public uint Frames;
        public nuint Samples;
        public double TtfaMs;
        public double TotalMs;
    }

    /// <summary>The C library's entry points (ptts.h of xn-ptts v0.7.1), loaded from its path.</summary>
    private sealed unsafe class Native
    {
        private readonly delegate* unmanaged[Cdecl]<byte*, uint, byte*, nint> _new;
        private readonly delegate* unmanaged[Cdecl]<nint, byte*, delegate* unmanaged[Cdecl]<float*, nuint, nint, byte>, nint, PttsResult*, byte> _speak;
        private readonly delegate* unmanaged[Cdecl]<nint, byte*> _voices;
        private readonly delegate* unmanaged[Cdecl]<nint, byte*, byte> _setVoice;
        private readonly delegate* unmanaged[Cdecl]<nint, ulong, void> _setSeed;
        private readonly delegate* unmanaged[Cdecl]<nint, byte*> _lastError;
        private readonly delegate* unmanaged[Cdecl]<nint, void> _free;
        // Rail Narrator's addition to the library (tools/phonon-word-timing.patch); absent from a
        // library built without it, which then speaks without word timing.
        private readonly delegate* unmanaged[Cdecl]<nint, byte*, delegate* unmanaged[Cdecl]<float*, nuint, nint, byte>, uint, uint,
            delegate* unmanaged[Cdecl]<byte*, uint*, nuint, nint, void>, delegate* unmanaged[Cdecl]<float*, nuint, nint, void>, nint, PttsResult*, byte> _speakAligned;

        public bool CanReportAttention => _speakAligned is not null;

        public Native(string path)
        {
            nint lib = NativeLibrary.Load(path);
            if (NativeLibrary.TryGetExport(lib, "ptts_speak_aligned", out nint aligned))
                _speakAligned = (delegate* unmanaged[Cdecl]<nint, byte*, delegate* unmanaged[Cdecl]<float*, nuint, nint, byte>, uint, uint,
                    delegate* unmanaged[Cdecl]<byte*, uint*, nuint, nint, void>, delegate* unmanaged[Cdecl]<float*, nuint, nint, void>, nint, PttsResult*, byte>)aligned;
            _new = (delegate* unmanaged[Cdecl]<byte*, uint, byte*, nint>)NativeLibrary.GetExport(lib, "ptts_new");
            _speak = (delegate* unmanaged[Cdecl]<nint, byte*, delegate* unmanaged[Cdecl]<float*, nuint, nint, byte>, nint, PttsResult*, byte>)NativeLibrary.GetExport(lib, "ptts_speak");
            _voices = (delegate* unmanaged[Cdecl]<nint, byte*>)NativeLibrary.GetExport(lib, "ptts_voices");
            _setVoice = (delegate* unmanaged[Cdecl]<nint, byte*, byte>)NativeLibrary.GetExport(lib, "ptts_set_voice");
            _setSeed = (delegate* unmanaged[Cdecl]<nint, ulong, void>)NativeLibrary.GetExport(lib, "ptts_set_seed");
            _lastError = (delegate* unmanaged[Cdecl]<nint, byte*>)NativeLibrary.GetExport(lib, "ptts_last_error");
            _free = (delegate* unmanaged[Cdecl]<nint, void>)NativeLibrary.GetExport(lib, "ptts_free");
        }

        public nint New(string dir, string lang)
        {
            fixed (byte* d = Utf8(dir)) fixed (byte* l = Utf8(lang)) return _new(d, 0 /* PTTS_UNIT_CPU */, l);
        }

        public bool Speak(nint h, string text, nint user)
        {
            PttsResult result;
            fixed (byte* t = Utf8(text)) return _speak(h, t, &OnAudio, user, &result) != 0;
        }

        public bool SpeakAligned(nint h, string text, uint layer, uint head, nint user)
        {
            PttsResult result;
            fixed (byte* t = Utf8(text)) return _speakAligned(h, t, &OnAudio, layer, head, &OnChunk, &OnAttention, user, &result) != 0;
        }

        public bool SetVoice(nint h, string name)
        {
            fixed (byte* n = Utf8(name)) return _setVoice(h, n) != 0;
        }

        public void SetSeed(nint h, ulong seed) => _setSeed(h, seed);

        public string? LastError(nint h) => Marshal.PtrToStringUTF8((nint)_lastError(h));

        public void Free(nint h) => _free(h);

        /// <summary>The NUL-separated block ptts_voices returns, ended by a second NUL.</summary>
        public List<string> Voices(nint h)
        {
            var names = new List<string>();
            byte* p = _voices(h);
            if (p is null) return names;
            while (*p != 0)
            {
                string name = Marshal.PtrToStringUTF8((nint)p)!;
                names.Add(name);
                p += Encoding.UTF8.GetByteCount(name) + 1;
            }
            return names;
        }

        private static byte[] Utf8(string s) => Encoding.UTF8.GetBytes(s + "\0");

        [DllImport("libc", EntryPoint = "setenv")]
        private static extern int setenv(string name, string value, int overwrite);

        /// <summary>
        /// Sets a variable the native runtime will read. On Linux .NET keeps its own copy of the
        /// environment, which native <c>getenv</c> never sees, so libc's <c>setenv</c> is called; on
        /// Windows .NET writes the process environment itself, where Rust's <c>std::env</c> reads it,
        /// and there is no libc to call.
        /// </summary>
        public static void SetEnv(string name, string value)
        {
            if (OperatingSystem.IsWindows()) Environment.SetEnvironmentVariable(name, value);
            else setenv(name, value, 0);
        }
    }
}
