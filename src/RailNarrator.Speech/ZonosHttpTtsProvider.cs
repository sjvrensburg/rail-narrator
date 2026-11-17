using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using RailNarrator.Domain;

namespace RailNarrator.Speech;

/// <summary>Configuration for the ZONOS3-Mini sidecar provider.</summary>
/// <param name="BaseUrl">Loopback base URL of a running <c>zonos3-server</c>. Never a remote host by default.</param>
/// <param name="SpeakerEmbeddingId">A speaker id from <c>GET /v2/speakers</c>. Selecting a voice explicitly is required; the server default is not assumed.</param>
/// <param name="SpeakerMode">"embedding" or "reference".</param>
/// <param name="SpeakerCfgScale">
/// Speaker classifier-free guidance. The operator-tuned value for this voice on the reference machine
/// is 1.5: without it an audible accent and delivery shift appears at paragraph joins, and the
/// speaker-similarity score drops from 0.92–0.94 to 0.89–0.92.
/// </param>
/// <param name="LoudnessLufs">
/// Target loudness as a raw condition value. Without a loudness condition, paragraphs came out up to
/// 12 dB apart and faded within themselves (loudness range 16.5 LU); with it, about 3 LU.
/// </param>
/// <param name="Decoder">Decoder head id, e.g. <c>"full_mr"</c>. Null uses the server default.</param>
/// <param name="MaxTokens">Generation cap. The server refuses values above its own <c>--max</c>.</param>
/// <param name="RequestTimeout">Per-request timeout. Synthesis is slow relative to a web call; a short timeout would abort good audio.</param>
/// <param name="DefaultSeed">Fixed seed for reproducible takes, or null to let the server choose.</param>
/// <param name="TrimLeadingSilence">Trim silence before the first sample so clip boundaries line up with speech.</param>
public sealed record ZonosProviderSettings(
    string BaseUrl = "http://127.0.0.1:19191",
    string? SpeakerEmbeddingId = null,
    string SpeakerMode = "embedding",
    double SpeakerCfgScale = 1.5,
    double? LoudnessLufs = -18,
    string? Decoder = "full_mr",
    int MaxTokens = 2000,
    TimeSpan? RequestTimeout = null,
    long? DefaultSeed = null,
    bool TrimLeadingSilence = true)
{
    public static ZonosProviderSettings Default { get; } = new();

    public TimeSpan Timeout => RequestTimeout ?? TimeSpan.FromMinutes(5);
}

/// <summary>
/// ZONOS3-Mini through its native ggml sidecar's v2 HTTP API.
/// </summary>
/// <remarks>
/// <para>
/// The contract is the one published in the runtime's <c>servers/api/openapi.yaml</c> and verified
/// against a running server on the reference machine: <c>POST /v2/generate</c> with
/// <c>{"text", "stream": false, "format": "pcm"}</c> returns float32 little-endian mono PCM, and the
/// response headers <c>X-Audio-Sample-Rate</c>, <c>X-Audio-Channels</c>, <c>X-Audio-Format</c>,
/// <c>X-Seed</c> and <c>X-Decoder</c> describe it exactly. Format metadata is read from those headers
/// rather than assumed, because a server configured with a different decoder or sample rate would
/// otherwise shift every cue in time.
/// </para>
/// <para>
/// <c>format: "wav"</c> is buffered and rejected together with <c>stream: true</c>, so streaming and
/// WAV are mutually exclusive server-side; this provider requests buffered PCM, which is the correct
/// baseline for sentence-level clips. Streaming is only worth adding after the buffered path is
/// proven, and HTTP chunk boundaries would not be frame boundaries in any case.
/// </para>
/// <para>
/// Alignment is not used. <c>/v2/edit/align</c> belongs to the speech-editing feature set and needs
/// an edit backbone, encoder companions and a Python worker with <c>fusealign-hub</c>; a
/// synthesis-only installation does not have them, and its presence in documentation is not evidence
/// that this install can use it. Sentence and equation highlighting ships without an aligner.
/// </para>
/// </remarks>
public sealed class ZonosHttpTtsProvider : ITtsProvider
{
    private const int SampleRate = 44100;
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
    };

    private readonly ZonosProviderSettings _settings;
    private readonly HttpClient _http;
    private readonly bool _ownsHttpClient;
    private CapabilitiesCache? _cached;
    private bool _disposed;

    public ZonosHttpTtsProvider(ZonosProviderSettings? settings = null, HttpClient? httpClient = null)
    {
        _settings = settings ?? ZonosProviderSettings.Default;
        if (httpClient is not null) { _http = httpClient; _ownsHttpClient = false; }
        else
        {
            _http = new HttpClient { BaseAddress = new Uri(_settings.BaseUrl), Timeout = _settings.Timeout };
            _ownsHttpClient = true;
        }
        if (_http.BaseAddress is null) _http.BaseAddress = new Uri(_settings.BaseUrl);
    }

    public string ProviderId => "zonos3-mini";

    public async Task<TtsCapabilities> GetCapabilitiesAsync(CancellationToken ct = default)
    {
        if (_cached is { } c && !c.IsStale) return c.Value;

        try
        {
            using var health = await _http.GetAsync("/v2/health", ct).ConfigureAwait(false);
            if (!health.IsSuccessStatusCode)
                return Cache(Unavailable($"Health check returned HTTP {(int)health.StatusCode}."));

            var doc = await _http.GetFromJsonAsync<CapabilitiesDocument>("/v2/capabilities", ct).ConfigureAwait(false);
            var speakers = await FetchSpeakersAsync(ct).ConfigureAwait(false);

            string revision = doc?.AudioCodec ?? "unknown";
            var caps = new TtsCapabilities(
                ProviderId,
                $"zonos3-ggml/{revision}",
                [AudioFormat.Mono44100Float32, AudioFormat.Mono44100Int16],
                // Streaming is implemented server-side, but the buffered path is the verified one and
                // chunk boundaries are not frame boundaries. Reported honestly as unsupported here.
                SupportsStreaming: false,
                // No aligner: /v2/edit/align needs an edit backbone and a Python worker this
                // synthesis-only install does not have. Clip boundaries are the timing source.
                TimingProvenance.None,
                SupportsSynthesisRate: false,
                MaxTextChars: 4000,
                speakers.Select(s => s.Language).Where(l => l is not null).Select(l => l!).Distinct().ToList(),
                Unavailable: null);
            return Cache(caps);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            return Cache(Unavailable($"Cannot reach the ZONOS3 sidecar at {_settings.BaseUrl}: {ex.Message}"));
        }

        TtsCapabilities Unavailable(string reason) => new(
            ProviderId, "zonos3-ggml/unavailable", [], false, TimingProvenance.None, false, 0, [], reason);
        TtsCapabilities Cache(TtsCapabilities value)
        {
            _cached = new CapabilitiesCache(value, Environment.TickCount64);
            return value;
        }
    }

    public async Task<IReadOnlyList<TtsVoice>> ListVoicesAsync(CancellationToken ct = default)
    {
        try { return await FetchSpeakersAsync(ct).ConfigureAwait(false); }
        catch (OperationCanceledException) { throw; }
        catch { return []; }
    }

    private async Task<IReadOnlyList<TtsVoice>> FetchSpeakersAsync(CancellationToken ct)
    {
        using var response = await _http.GetAsync("/v2/speakers", ct).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        var payload = await response.Content.ReadFromJsonAsync<SpeakerListPayload>(JsonOptions, ct).ConfigureAwait(false);
        var items = payload?.Speakers ?? payload?.Items ?? [];
        return items
            .Select(s => new TtsVoice(
                s.Id ?? "",
                s.Label ?? s.OriginalName ?? s.Id ?? "",
                LanguageOf(s),
                GenderOf(s),
                s.IsDefault))
            .Where(v => v.Id.Length > 0)
            .ToList();
    }

    private static string? LanguageOf(SpeakerDto s)
    {
        string? label = s.Label ?? s.OriginalName;
        if (label is null) return null;
        // Labels look like "Imogen en British F 1"; take the first token that parses as a language.
        foreach (var token in label.Split(' ', StringSplitOptions.RemoveEmptyEntries))
        {
            if (token.Length is < 2 or > 5) continue;
            if (System.Globalization.CultureInfo.GetCultures(System.Globalization.CultureTypes.AllCultures)
                    .Any(c => string.Equals(c.TwoLetterISOLanguageName, token, StringComparison.OrdinalIgnoreCase)))
                return token.ToLowerInvariant();
        }
        return null;
    }

    private static string? GenderOf(SpeakerDto s)
    {
        string? label = s.Label ?? s.OriginalName;
        if (label is null) return null;
        if (label.Contains("_F_") || label.Contains(" F ") || label.Contains("Female")) return "female";
        if (label.Contains("_M_") || label.Contains(" M ") || label.Contains("Male")) return "male";
        return null;
    }

    public async Task<TtsSynthesisResult> SynthesizeAsync(TtsSynthesisRequest request, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (string.IsNullOrWhiteSpace(request.Text))
            return new TtsSynthesisResult(ReadOnlyMemory<byte>.Empty, request.Format, 0, [], TimingProvenance.None, null, null, ["Empty text."]);

        string speaker = string.IsNullOrWhiteSpace(request.VoiceId)
            ? _settings.SpeakerEmbeddingId ?? throw new InvalidOperationException("No ZONOS3 speaker is configured. Choose one from the voice list.")
            : request.VoiceId;

        var body = new GenerateRequestDto
        {
            Text = request.Text,
            Stream = false,
            Format = "pcm",
            Seed = request.Seed ?? _settings.DefaultSeed,
            Language = NormaliseLanguage(request.Language),
            SpeakerEmbeddingId = speaker,
            SpeakerMode = _settings.SpeakerMode,
            SpeakerCfgScale = _settings.SpeakerCfgScale > 0 ? _settings.SpeakerCfgScale : null,
            MaxTokens = _settings.MaxTokens > 0 ? _settings.MaxTokens : null,
            Decoder = _settings.Decoder,
            ConditionValues = _settings.LoudnessLufs is { } lufs ? new Dictionary<string, double?> { ["loudness"] = lufs } : null,
        };

        using var http = new HttpRequestMessage(HttpMethod.Post, "/v2/generate")
        {
            Content = JsonContent.Create(body, options: JsonOptions),
        };
        using var response = await _http.SendAsync(http, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);

        if (!response.IsSuccessStatusCode)
        {
            string detail = await SafeReadAsync(response, ct).ConfigureAwait(false);
            throw new InvalidOperationException(DescribeHttpFailure((int)response.StatusCode, detail));
        }

        // The response headers are the authority on what came back. Trusting the request instead
        // would silently mistime every cue if the server's decoder or rate differed.
        int sampleRate = HeaderInt(response, "X-Audio-Sample-Rate", SampleRate);
        int channels = HeaderInt(response, "X-Audio-Channels", 1);
        string formatName = HeaderString(response, "X-Audio-Format") ?? "float32";
        long? seedUsed = HeaderLong(response, "X-Seed");
        string? decoder = HeaderString(response, "X-Decoder");

        var sampleFormat = formatName.ToLowerInvariant() switch
        {
            "float32" or "f32" => SampleFormat.Float32LittleEndian,
            "int16" or "s16" => SampleFormat.Int16LittleEndian,
            "int32" or "s32" => SampleFormat.Int32LittleEndian,
            _ => throw new InvalidOperationException($"ZONOS3 returned an unsupported sample format '{formatName}'."),
        };
        var actual = new AudioFormat(sampleRate, channels, sampleFormat);

        byte[] pcm = await response.Content.ReadAsByteArrayAsync(ct).ConfigureAwait(false);
        long frames = actual.FramesFromBytes(pcm.Length);
        var warnings = new List<string>();

        if (frames == 0)
            throw new InvalidOperationException("ZONOS3 returned an empty audio body.");

        if (_settings.TrimLeadingSilence)
        {
            var (trimmed, leading) = Pcm.TrimLeadingSilence(pcm, actual, 0.005f);
            if (leading > 0)
            {
                pcm = trimmed;
                frames = actual.FramesFromBytes(pcm.Length);
                warnings.Add($"Trimmed {actual.ToSeconds(leading) * 1000:F0} ms of leading silence.");
            }
        }

        if (actual.SampleRate != request.Format.SampleRate)
            warnings.Add($"Provider returned {actual.SampleRate} Hz; {request.Format.SampleRate} Hz was requested. The audio engine resamples.");

        // No timings: a synthesis-only ZONOS3 install has no aligner. The caller falls back to the
        // clip-boundary cue, which is exact for a whole sentence or equation.
        return new TtsSynthesisResult(pcm, actual, frames, [], TimingProvenance.None, seedUsed, decoder, warnings);
    }

    public async Task<ProviderSelfTestResult> SelfTestAsync(string voiceId, CancellationToken ct = default)
    {
        long start = Environment.TickCount64;
        try
        {
            var caps = await GetCapabilitiesAsync(ct).ConfigureAwait(false);
            if (!caps.IsAvailable)
                return new ProviderSelfTestResult(false, 0, 0, Environment.TickCount64 - start, caps.Unavailable);

            var result = await SynthesizeAsync(new TtsSynthesisRequest(
                "Testing one, two, three.", voiceId, "en-GB", AudioFormat.Mono44100Float32, Seed: 1), ct).ConfigureAwait(false);

            float peak = Pcm.Peak(result.Pcm.Span, result.Format);
            float rms = Pcm.Rms(result.Pcm.Span, result.Format);
            string? detail = result.IsSilentOrEmpty ? "The server returned no samples."
                : peak < ProviderSelfTestResult.SilencePeakThreshold
                    ? $"Audio is effectively silent (peak {peak:F5}, rms {rms:F5}). On this hardware a Vulkan decoder is known to output silence; use the CPU backend."
                    : null;
            return new ProviderSelfTestResult(detail is null, result.DurationSeconds, peak, Environment.TickCount64 - start, detail);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            return new ProviderSelfTestResult(false, 0, 0, Environment.TickCount64 - start, ex.Message);
        }
    }

    private static string DescribeHttpFailure(int status, string detail) => status switch
    {
        400 => $"The ZONOS3 server rejected the request: {Truncate(detail)}. A state-dependent refusal — an unknown voice, a missing feature, or an incompatible option combination.",
        422 => $"The ZONOS3 server rejected the request body as malformed: {Truncate(detail)}.",
        500 => $"The ZONOS3 server failed while generating: {Truncate(detail)}.",
        _ => $"ZONOS3 generation failed with HTTP {status}: {Truncate(detail)}.",
    };

    private static string Truncate(string s, int max = 400) => s.Length <= max ? s : s[..max] + "…";

    private static async Task<string> SafeReadAsync(HttpResponseMessage response, CancellationToken ct)
    {
        try { return await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false); }
        catch { return "(no body)"; }
    }

    private static int HeaderInt(HttpResponseMessage r, string name, int fallback)
        => int.TryParse(HeaderString(r, name), out int v) && v > 0 ? v : fallback;

    private static long? HeaderLong(HttpResponseMessage r, string name)
        => long.TryParse(HeaderString(r, name), out long v) ? v : null;

    private static string? HeaderString(HttpResponseMessage r, string name)
    {
        if (r.Headers.TryGetValues(name, out var values)) return values.FirstOrDefault();
        if (r.Content.Headers.TryGetValues(name, out var contentValues)) return contentValues.FirstOrDefault();
        return null;
    }

    /// <summary>The server expects its own language identifiers, not arbitrary BCP-47 tags.</summary>
    private static string NormaliseLanguage(string? language) => (language ?? "en").ToLowerInvariant() switch
    {
        "en" or "en-us" or "en_us" => "en_us",
        "en-gb" or "en_gb" or "en-british" => "en_us",
        var other => other.Replace('-', '_'),
    };

    private sealed record CapabilitiesCache(TtsCapabilities Value, long FetchedAt)
    {
        public bool IsStale => Environment.TickCount64 - FetchedAt > 60_000;
    }

    private sealed record CapabilitiesDocument
    {
        [JsonPropertyName("audio_codec")] public string? AudioCodec { get; init; }
        [JsonPropertyName("edit_transcribe")] public bool? EditTranscribe { get; init; }
    }

    private sealed record SpeakerListPayload
    {
        [JsonPropertyName("speakers")] public List<SpeakerDto>? Speakers { get; init; }
        [JsonPropertyName("items")] public List<SpeakerDto>? Items { get; init; }
    }

    private sealed record SpeakerDto
    {
        [JsonPropertyName("id")] public string? Id { get; init; }
        [JsonPropertyName("label")] public string? Label { get; init; }
        [JsonPropertyName("original_name")] public string? OriginalName { get; init; }
        [JsonPropertyName("is_default")] public bool IsDefault { get; init; }
        [JsonPropertyName("embedding_dim")] public int? EmbeddingDim { get; init; }
        [JsonPropertyName("has_preview")] public bool? HasPreview { get; init; }
    }

    private sealed record GenerateRequestDto
    {
        [JsonPropertyName("text")] public string Text { get; init; } = "";
        [JsonPropertyName("stream")] public bool Stream { get; init; }
        [JsonPropertyName("format")] public string Format { get; init; } = "pcm";
        [JsonPropertyName("seed")] public long? Seed { get; init; }
        [JsonPropertyName("language")] public string? Language { get; init; }
        [JsonPropertyName("speaker_embedding_id")] public string? SpeakerEmbeddingId { get; init; }
        [JsonPropertyName("speaker_mode")] public string? SpeakerMode { get; init; }
        [JsonPropertyName("speaker_cfg_scale")] public double? SpeakerCfgScale { get; init; }
        [JsonPropertyName("max_tokens")] public int? MaxTokens { get; init; }
        [JsonPropertyName("decoder")] public string? Decoder { get; init; }
        [JsonPropertyName("condition_values")] public Dictionary<string, double?>? ConditionValues { get; init; }
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        _disposed = true;
        if (_ownsHttpClient) _http.Dispose();
        await Task.CompletedTask.ConfigureAwait(false);
    }
}
