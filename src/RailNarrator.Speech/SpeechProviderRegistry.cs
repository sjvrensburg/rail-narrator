using RailNarrator.Domain;

namespace RailNarrator.Speech;

/// <summary>
/// The speech providers this build actually implements.
/// </summary>
/// <remarks>
/// A provider appears here only when it is implemented and exercised. Listing an unimplemented
/// backend in a picker implies it works and wastes the reader's time finding out otherwise, so the
/// two candidates the original brief named but this build does not ship — ZONOS3's streaming path and
/// the Audio8 patched-llama.cpp protocol, which is a different contract again (SSE frames carrying
/// base64 PCM with sample offsets, not word timestamps) — are absent rather than greyed out.
/// </remarks>
public static class SpeechProviderIds
{
    /// <summary>Phonon first when installed, else Kokoro (ADR-0009). A setting, not a provider.</summary>
    public const string Auto = "auto";

    /// <summary>In-process Phonon through Gradium's C library (ADR-0009).</summary>
    public const string Phonon = PhononTtsProvider.Id;

    /// <summary>In-process Kokoro via ONNX Runtime.</summary>
    public const string Kokoro = "kokoro";

    /// <summary>ZONOS3-Mini through its native ggml sidecar's v2 HTTP API.</summary>
    public const string Zonos = "zonos3-mini";

    /// <summary>Deterministic provider for tests and offline development.</summary>
    public const string Fake = "fake";

    public static IReadOnlyList<string> All { get; } = [Phonon, Kokoro, Zonos, Fake];

    public static bool IsKnown(string id) => All.Contains(id);
}

/// <summary>
/// Chooses a provider by id and reports why one cannot be used.
/// </summary>
/// <remarks>
/// Availability is probed, not assumed. A provider whose model file is missing, or whose sidecar is
/// not running, reports the reason through <see cref="TtsCapabilities.Unavailable"/> so the UI can
/// say "download the model" or "start the sidecar" instead of failing at the first sentence.
/// </remarks>
public sealed class SpeechProviderRegistry
{
    private readonly Dictionary<string, ITtsProvider> _providers = new(StringComparer.Ordinal);

    /// <summary>Registers a provider instance. The registry does not own its lifetime.</summary>
    public SpeechProviderRegistry Register(ITtsProvider provider)
    {
        ArgumentNullException.ThrowIfNull(provider);
        _providers[provider.ProviderId] = provider;
        return this;
    }

    public IReadOnlyList<string> RegisteredIds => _providers.Keys.OrderBy(k => k, StringComparer.Ordinal).ToList();

    public ITtsProvider? Get(string providerId) => _providers.GetValueOrDefault(providerId);

    /// <summary>
    /// The first registered provider that reports itself available, or null. Used to pick a default
    /// without hard-coding one that may not be installed.
    /// </summary>
    public async Task<ITtsProvider?> FirstAvailableAsync(CancellationToken ct = default)
    {
        foreach (var id in RegisteredIds)
        {
            var provider = _providers[id];
            var caps = await provider.GetCapabilitiesAsync(ct).ConfigureAwait(false);
            if (caps.IsAvailable) return provider;
        }
        return null;
    }

    /// <summary>
    /// Capability report for every registered provider, including the unavailable ones and why.
    /// Backs the settings panel's connection test.
    /// </summary>
    public async Task<IReadOnlyList<(string ProviderId, TtsCapabilities Capabilities)>> ProbeAllAsync(CancellationToken ct = default)
    {
        var results = new List<(string, TtsCapabilities)>();
        foreach (var id in RegisteredIds)
            results.Add((id, await _providers[id].GetCapabilitiesAsync(ct).ConfigureAwait(false)));
        return results;
    }
}
