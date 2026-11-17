using RailReader.Core.Services;
using RailReader.Renderer.Skia;
using RailNarrator.Audio;
using RailNarrator.Domain;
using RailNarrator.Infrastructure;
using RailNarrator.Inference;
using RailNarrator.Pdf;
using RailNarrator.Speech;

namespace RailNarrator.App.Composition;

/// <summary>
/// The application's object graph, assembled once at startup and disposed on exit.
/// </summary>
/// <remarks>
/// <para>
/// A hand-written composition root rather than a container: the graph is small, every edge is a
/// decision worth being able to read, and a container would hide the one thing that matters here —
/// which provider is real, which is optional, and what happens when a model is missing.
/// </para>
/// <para>
/// Providers are registered only when they are implemented and configured. The UI offers exactly the
/// set that <see cref="SpeechProviderRegistry"/> holds, so there is no inert selector implying a
/// backend that cannot work.
/// </para>
/// </remarks>
public sealed class ServiceRegistry : IAsyncDisposable
{
    public required SettingsStore Settings { get; init; }
    public required RailNarratorSettings CurrentSettings { get; init; }
    public required RailReaderExtractionSource Extraction { get; init; }

    /// <summary>Extraction through the on-disk snapshot cache. Use this to extract; <see cref="Extraction"/> for display.</summary>
    public required CachingExtractionSource CachedExtraction { get; init; }
    public required SpeechProviderRegistry SpeechProviders { get; init; }
    public required IAudioOutputDevice AudioDevice { get; init; }
    public required AudioPlaybackEngine AudioEngine { get; init; }
    public required NarrationCoordinator Coordinator { get; init; }
    public required DiskSpeechAssetStore AssetStore { get; init; }
    public required DiskPlanStore PlanStore { get; init; }
    public required ModelDownloader Models { get; init; }
    public required SidecarManager? Sidecar { get; init; }
    public required IMathVerbaliser? MathVerbaliser { get; init; }
    public required IMathTranscriber? MathTranscriber { get; init; }
    public required IReadOnlyList<string> StartupNotes { get; init; }

    /// <summary>Builds the object graph from persisted settings.</summary>
    public static ServiceRegistry Create()
    {
        XdgPaths.EnsureCreated();
        var settingsStore = new SettingsStore();
        var settings = settingsStore.Load();

        var notes = new List<string>();
        if (settingsStore.LastError is { } error) notes.Add(error);

        var extraction = new RailReaderExtractionSource();
        extraction.WithBackend(BuildBackend(settings, notes));

        var assetStore = new DiskSpeechAssetStore();
        var planStore = new DiskPlanStore();
        var models = new ModelDownloader();

        // ---- speech --------------------------------------------------------------------
        var registry = new SpeechProviderRegistry();
        SidecarManager? sidecar = null;

        string? kokoroModel = settings.KokoroModelPath ?? Path.Combine(XdgPaths.Models, ModelManifest.KokoroFp16.FileName);
        if (File.Exists(kokoroModel))
        {
            registry.Register(new KokoroTtsProvider(new KokoroProviderSettings(
                ModelPath: kokoroModel,
                VoicesDirectory: settings.KokoroVoicesDirectory,
                DefaultVoice: settings.KokoroVoiceId ?? "af_heart")));
        }
        else
        {
            notes.Add($"Kokoro model not found at '{kokoroModel}'. Download it from the Models panel " +
                      $"({ModelManifest.KokoroFp16.HumanSize}, {ModelManifest.KokoroFp16.Licence}).");
        }

        string zonosUrl = settings.ZonosBaseUrl ?? "http://127.0.0.1:19191";
        if (settings.ZonosSpeakerId is { Length: > 0 })
        {
            registry.Register(new ZonosHttpTtsProvider(new ZonosProviderSettings(
                BaseUrl: zonosUrl,
                SpeakerEmbeddingId: settings.ZonosSpeakerId,
                SpeakerCfgScale: settings.ZonosSpeakerCfgScale,
                LoudnessLufs: settings.ZonosLoudnessLufs)));
        }
        else
        {
            notes.Add("ZONOS3 is not configured. Set a speaker id to enable it; the sidecar must be running " +
                      $"and reachable at {zonosUrl}.");
        }

        if (registry.RegisteredIds.Count == 0)
            notes.Add("No speech provider is available. Narration is disabled until one is configured.");

        // ---- audio ---------------------------------------------------------------------
        IAudioOutputDevice audioDevice = new PortAudioOutputDevice();
        if (audioDevice.Enumerate().Count == 0)
            notes.Add("No audio output device was found. Playback will not be audible; " +
                      "extraction and planning still work.");

        var audioEngine = new AudioPlaybackEngine(audioDevice, new AudioEngineOptions(
            DeviceId: settings.AudioDeviceId));

        // ---- maths ---------------------------------------------------------------------
        IMathVerbaliser? verbaliser = null;
        IMathTranscriber? transcriber = null;
        if (!string.IsNullOrWhiteSpace(settings.MathLlmBaseUrl) && !string.IsNullOrWhiteSpace(settings.MathLlmModel))
        {
            verbaliser = new OpenAiCompatibleMathVerbaliser(new MathEndpointSettings(
                settings.MathLlmBaseUrl, settings.MathLlmModel, settings.ResolveMathApiKey()));
        }
        else
        {
            notes.Add("No text LLM is configured for maths narration. Equations will be announced as unreadable " +
                      "unless the local rule-based fallback is enabled in settings.");
        }

        if (!string.IsNullOrWhiteSpace(settings.VlmBaseUrl) && !string.IsNullOrWhiteSpace(settings.VlmModel))
        {
            var vlm = new RailReader.Core.Vlm.OpenAI.OpenAIVlmClient();
            transcriber = new VlmMathTranscriber(vlm,
                new VlmEndpointConfig(settings.VlmBaseUrl, settings.VlmModel, settings.ResolveVlmApiKey()),
                extraction);
        }

        string alignmentRoot = Path.Combine(XdgPaths.Data, "alignment");
        IForcedAligner? aligner = settings.WordHighlighting ? new MfaForcedAligner(new MfaAlignerSettings(
            settings.MfaExecutable ?? Path.Combine(alignmentRoot, "runtime", "bin", "mfa"),
            settings.MfaDictionaryPath ?? Path.Combine(alignmentRoot, "models", "dictionary", "english_uk_mfa.dict"),
            settings.MfaAcousticModelPath ?? Path.Combine(alignmentRoot, "models", "acoustic", "english_mfa.zip"),
            Path.Combine(XdgPaths.Cache, "alignment"), settings.MfaRuntimeRevision)) : null;
        if (aligner is not null)
            notes.Add("Word highlighting uses local alignment when installed; see docs/word-highlighting.md for setup.");

        var coordinator = new NarrationCoordinator(
            // A registry-level provider is resolved lazily below; the coordinator needs one instance,
            // so the configured provider (or a fake that reports unavailability) is chosen here.
            SelectProvider(registry, settings.SpeechProviderId, notes),
            audioEngine,
            assetStore,
            options: new NarrationCoordinatorOptions(
                LookaheadSegments: settings.LookaheadSegments,
                MaxConcurrentSynthesis: settings.MaxConcurrentSynthesis,
                VoiceId: settings.KokoroVoiceId ?? settings.ZonosSpeakerId,
                PlaybackRate: settings.PlaybackRate,
                FollowMode: settings.FollowMode,
                RequestTimings: settings.WordHighlighting),
            aligner: aligner);

        return new ServiceRegistry
        {
            Settings = settingsStore,
            CurrentSettings = settings,
            Extraction = extraction,
            CachedExtraction = new CachingExtractionSource(
                extraction,
                backendFingerprint: ExtractionBackendFingerprint(settings),
                isDegraded: () => extraction.OcrStartupError is not null),
            SpeechProviders = registry,
            AudioDevice = audioDevice,
            AudioEngine = audioEngine,
            Coordinator = coordinator,
            AssetStore = assetStore,
            PlanStore = planStore,
            Models = models,
            Sidecar = sidecar,
            MathVerbaliser = verbaliser,
            MathTranscriber = transcriber,
            StartupNotes = notes,
        };
    }

    /// <summary>
    /// Identifies what shapes extraction but <see cref="ExtractionOptions"/> does not carry — the
    /// extractor and Core assembly versions and the layout model file an analyser id resolves to —
    /// so replacing any of them does not reuse snapshots made by the old one.
    /// </summary>
    private static string ExtractionBackendFingerprint(RailNarratorSettings settings)
    {
        // The extractor and the RailReaderCore packages it runs: upgrading either can change a
        // snapshot without anyone remembering to bump ExtractorVersions.Current.
        var pdfAssembly = typeof(RailReaderExtractionSource).Assembly;
        string code = string.Join(";", new[] { pdfAssembly.GetName() }
            .Concat(pdfAssembly.GetReferencedAssemblies().Where(a => a.Name?.StartsWith("RailReader", StringComparison.Ordinal) == true))
            .Select(a => $"{a.Name}/{a.Version}")
            .OrderBy(s => s, StringComparer.Ordinal));

        if (settings.LayoutModelPath is not { Length: > 0 } path) return code;
        var info = new FileInfo(path);
        return code + "|" + (info.Exists ? $"{info.FullName}|{info.Length}|{info.LastWriteTimeUtc.Ticks}" : path);
    }

    /// <summary>Builds a planner from the current settings.</summary>
    public NarrationPlanner CreatePlanner()
    {
        var settings = CurrentSettings;
        MathScriptResolver? resolver = MathTranscriber is { } t
            ? new MathScriptResolver(Extraction, t)
            : null;

        return new NarrationPlanner(
            MathVerbaliser,
            resolver,
            new NarrationPlannerOptions(
                Language: "en-GB",
                MathMode: settings.MathMode,
                MathFallback: settings.MathFallback,
                PronunciationDictionary: settings.PronunciationDictionary),
            localMathReader: LocalMathReader.Reader);
    }

    /// <summary>Builds a reading-unit builder from the current settings.</summary>
    public ReadingUnitBuilder CreateUnitBuilder(IReadOnlyList<MathsRegionOverride>? overrides = null)
        => new(new ReadingUnitBuilderOptions(
            CurrentSettings.Filters,
            PronunciationDictionary: CurrentSettings.PronunciationDictionary,
            Overrides: overrides));

    /// <summary>Builds the extraction request for a document under the current settings.</summary>
    public ExtractionRequest CreateExtractionRequest(string filePath, IReadOnlyList<int>? pages = null)
        => new(filePath, null, pages, BuildExtractionOptions(CurrentSettings), CurrentSettings.Filters);

    public static ExtractionOptions BuildExtractionOptions(RailNarratorSettings settings)
        => new(
            settings.LayoutAnalyzer,
            settings.Ocr,
            AnalysisRenderSize: settings.Ocr == OcrPolicy.Off ? 1024 : 1920,
            DeskewLines: false,
            ExtractorVersions.Current);

    private static ExtractionBackend BuildBackend(RailNarratorSettings settings, List<string> notes)
    {
        // OCR is available out of the box: RailReader.Core.Ocr.RapidOcr ships PP-OCRv5 Latin models
        // beside the application. If they cannot be located, extraction continues and the missing
        // models are reported rather than a scan silently producing nothing.
        Func<IOcrService>? ocrFactory = settings.Ocr == OcrPolicy.Off
            ? null
            : () => new RailReader.Core.Ocr.RapidOcr.RapidOcrService();

        if (settings.LayoutModelPath is { Length: > 0 } modelPath && File.Exists(modelPath))
            return ExtractionBackend.OnnxLayout(modelPath, ocrFactory);

        if (settings.LayoutAnalyzer != "text-layout")
            notes.Add($"Layout model '{settings.LayoutAnalyzer}' was requested but no model path is set; " +
                      "falling back to the model-free text analyzer, which supplies no semantic block roles.");
        return ExtractionBackend.ModelFree(ocrFactory);
    }

    private static ITtsProvider SelectProvider(SpeechProviderRegistry registry, string preferredId, List<string> notes)
    {
        var provider = registry.Get(preferredId);
        if (provider is not null) return provider;
        var fallback = registry.RegisteredIds.Select(registry.Get).FirstOrDefault(p => p is not null);
        if (fallback is not null)
        {
            notes.Add($"Speech provider '{preferredId}' is not configured; using '{fallback.ProviderId}' instead.");
            return fallback;
        }
        // Nothing is installed. Report it as an unavailable provider so the UI explains the situation
        // instead of offering a control that does nothing.
        notes.Add("No speech provider could be selected.");
        return new FakeTtsProvider(new FakeTtsOptions(
            ProviderId: preferredId, UnavailableReason: "No speech runtime is configured."));
    }

    public async ValueTask DisposeAsync()
    {
        // ConfigureAwait(false) throughout: this is awaited at exit from the UI thread, and a
        // continuation that needs that thread back would wait on the very call that is blocking it.
        await Coordinator.DisposeAsync().ConfigureAwait(false);
        await AudioEngine.DisposeAsync().ConfigureAwait(false);
        AudioDevice.Dispose();
        if (Sidecar is not null) await Sidecar.DisposeAsync().ConfigureAwait(false);
        if (MathVerbaliser is IDisposable disposableVerbaliser) disposableVerbaliser.Dispose();
        Extraction.Dispose();
        Models.Dispose();
        foreach (string id in SpeechProviders.RegisteredIds)
            if (SpeechProviders.Get(id) is { } provider) await provider.DisposeAsync().ConfigureAwait(false);
    }
}
