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
    public required IMathInterpreter? MathInterpreter { get; init; }
    public required IReadOnlyList<string> StartupNotes { get; init; }

    /// <summary>The Kokoro provider when one is registered, for its device in the diagnostics.</summary>
    public required KokoroTtsProvider? Kokoro { get; init; }

    /// <summary>"Docling Heron (docling-layout-heron-int8.onnx)" from a backend's analyzer id.</summary>
    private static string LayoutModelName(string analyzerId)
    {
        int colon = analyzerId.IndexOf(':');
        string id = colon < 0 ? analyzerId : analyzerId[..colon];
        string file = colon < 0 ? "" : $" ({analyzerId[(colon + 1)..]})";
        return (id == LayoutModelResolver.Heron ? "Docling Heron" : id == LayoutModelResolver.PPDocLayoutV3 ? "PP-DocLayoutV3" : id) + file;
    }

    /// <summary>
    /// Kokoro on the GPU (ADR-0007): only with <c>KokoroAcceleration</c> Auto, the converted model
    /// installed and a WebGPU device found. The session hook is Core's, so Kokoro joins the one GPU
    /// binding per process that layout analysis uses. The provider still falls back to the CPU if the
    /// session will not build or a clip is not real audio.
    /// </summary>
    private static KokoroGpuOptions? KokoroGpu(RailNarratorSettings settings, List<string> notes)
    {
        if (settings.KokoroAcceleration != SpeechAcceleration.Auto) return null;
        string model = settings.KokoroGpuModelPath ?? Path.Combine(XdgPaths.Models, ModelManifest.KokoroWebGpu.FileName);
        if (!File.Exists(model)) return null;   // not installed: the CPU, as before, without a note
        var hook = RailReader.Core.Analysis.WebGpu.WebGpuAccelerator.TryBuildSessionHook();
        if (hook is null)
        {
            notes.Add("Kokoro runs on the CPU: no WebGPU device was found for the GPU model.");
            return null;
        }
        return new KokoroGpuOptions(model, hook, RailReader.Core.Analysis.WebGpu.WebGpuAccelerator.DeviceDescription);
    }

    /// <summary>Which layout analyzer extraction uses and on which device, for the side panel.</summary>
    public required string LayoutSummary { get; init; }

    /// <summary>Builds the object graph from persisted settings.</summary>
    public static ServiceRegistry Create()
    {
        XdgPaths.EnsureCreated();
        var settingsStore = new SettingsStore();
        var settings = settingsStore.Load();

        var notes = new List<string>();
        if (settingsStore.LastError is { } error) notes.Add(error);

        var extraction = new RailReaderExtractionSource();
        var backend = BuildBackend(settings, notes);
        extraction.WithBackend(backend);

        var assetStore = new DiskSpeechAssetStore();
        var planStore = new DiskPlanStore();
        var models = new ModelDownloader();

        // ---- speech --------------------------------------------------------------------
        var registry = new SpeechProviderRegistry();
        SidecarManager? sidecar = null;

        KokoroTtsProvider? kokoro = null;
        string? kokoroModel = settings.KokoroModelPath ?? Path.Combine(XdgPaths.Models, ModelManifest.KokoroFp16.FileName);
        if (File.Exists(kokoroModel))
        {
            kokoro = new KokoroTtsProvider(new KokoroProviderSettings(
                ModelPath: kokoroModel,
                VoicesDirectory: settings.KokoroVoicesDirectory,
                DefaultVoice: settings.KokoroVoiceId ?? "af_heart"),
                KokoroGpu(settings, notes));
            registry.Register(kokoro);
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
        IMathInterpreter? interpreter = null;
        ISegmentResolver? segmentResolver = null;
        if (!string.IsNullOrWhiteSpace(settings.MathLlmBaseUrl) && !string.IsNullOrWhiteSpace(settings.MathLlmModel))
        {
            verbaliser = new OpenAiCompatibleMathVerbaliser(new MathEndpointSettings(
                settings.MathLlmBaseUrl, settings.MathLlmModel, settings.ResolveMathApiKey()));
            // Interpretation: the same endpoint, sent the page image of each passage when it accepts
            // images. A reasoning model spends tokens thinking before it answers, hence the budget.
            interpreter = new OpenAiCompatibleMathInterpreter(new MathEndpointSettings(
                settings.MathLlmBaseUrl, settings.MathLlmModel, settings.ResolveMathApiKey(),
                Temperature: 0.2, MaxTokens: 4000, MaxRetries: 1, RequestTimeout: TimeSpan.FromMinutes(3)),
                thinking: settings.EffectiveMathThinking switch
                {
                    MathThinkingPolicy.Off => MathThinking.Off,
                    MathThinkingPolicy.Always => MathThinking.Always,
                    _ => MathThinking.EquationsOnly,
                });
            // A refused interpretation is read mechanically from the same model's transcription of
            // the passage image, rather than from the garbled text layer.
            segmentResolver = new MathInterpretationResolver(interpreter, extraction, new DiskTextCache(),
                PlannerOptionsFor(settings, interpret: true), reader: (IMathReader)interpreter);
        }
        else
        {
            notes.Add("No LLM is configured for maths. Sentences with maths are read as printed and equations are " +
                      "announced as unreadable; set MathLlmBaseUrl and MathLlmModel (any OpenAI-compatible endpoint, " +
                      "ideally one that accepts images) to have them interpreted.");
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
            Path.Combine(XdgPaths.Cache, "alignment"), settings.MfaRuntimeRevision,
            G2pModelPath: settings.MfaG2pModelPath ?? Path.Combine(alignmentRoot, "models", "g2p", "english_uk_mfa.zip"))) : null;
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
                LookaheadSeconds: settings.LookaheadSeconds,
                MaxConcurrentSynthesis: settings.MaxConcurrentSynthesis,
                VoiceId: settings.KokoroVoiceId ?? settings.ZonosSpeakerId,
                PlaybackRate: settings.PlaybackRate,
                FollowMode: settings.FollowMode,
                RequestTimings: settings.WordHighlighting),
            aligner: aligner,
            resolver: segmentResolver);

        return new ServiceRegistry
        {
            Settings = settingsStore,
            CurrentSettings = settings,
            Extraction = extraction,
            CachedExtraction = new CachingExtractionSource(
                extraction,
                backendFingerprint: ExtractionBackendFingerprint(settings, backend.ResolvedDevice),
                // A snapshot made after the layout GPU fell back to the CPU is not what the cache
                // key (device=webgpu) names, so it is not stored.
                isDegraded: () => extraction.OcrStartupError is not null || extraction.LayoutDeviceFellBack),
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
            MathInterpreter = interpreter,
            StartupNotes = notes,
            Kokoro = kokoro,
            LayoutSummary = backend.Capabilities.Classes.Count > 0
                ? $"{LayoutModelName(backend.AnalyzerId)} on {backend.ResolvedDevice.Description}"
                : "model-free (no layout model)",
        };
    }

    /// <summary>
    /// Identifies what shapes extraction but <see cref="ExtractionOptions"/> does not carry — the
    /// extractor and Core assembly versions, the layout model file an analyser id resolves to and
    /// the device it runs on — so replacing any of them does not reuse snapshots made by the old one.
    /// </summary>
    /// <remarks>
    /// The device is in the key because CPU and GPU snapshots are equivalent but not identical: on a
    /// 24-page chapter, blocks, roles and reading sequence matched while geometry differed by up to
    /// 0.02 pt (ADR-0006). A cached snapshot is meant to be what this configuration produces.
    /// </remarks>
    private static string ExtractionBackendFingerprint(RailNarratorSettings settings, LayoutDevice device)
    {
        // The extractor and the RailReaderCore packages it runs: upgrading either can change a
        // snapshot without anyone remembering to bump ExtractorVersions.Current.
        var pdfAssembly = typeof(RailReaderExtractionSource).Assembly;
        string code = string.Join(";", new[] { pdfAssembly.GetName() }
            .Concat(pdfAssembly.GetReferencedAssemblies().Where(a => a.Name?.StartsWith("RailReader", StringComparison.Ordinal) == true))
            .Select(a => $"{a.Name}/{a.Version}")
            .OrderBy(s => s, StringComparer.Ordinal));

        if (ResolveLayoutModel(settings) is not { } model) return code;
        // The file that actually runs: Heron's FP32 export on a GPU, its INT8 one on the CPU.
        string path = device.IsGpu && model.GpuModelPath is { } gpu ? gpu : model.CpuModelPath;
        var info = new FileInfo(path);
        return code + "|" + model.Id + "|" + (info.Exists ? $"{info.FullName}|{info.Length}|{info.LastWriteTimeUtc.Ticks}" : path)
            + "|device=" + device.Id;
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
            PlannerOptionsFor(settings, interpret: MathInterpreter is not null),
            localMathReader: LocalMathReader.Reader);
    }

    private static NarrationPlannerOptions PlannerOptionsFor(RailNarratorSettings settings, bool interpret)
        => new(
            Language: "en-GB",
            MathMode: settings.MathMode,
            MathFallback: settings.MathFallback,
            PronunciationDictionary: settings.PronunciationDictionary,
            InterpretMaths: interpret);

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
            // The analyzer actually used, not the one named in settings: a discovered layout model
            // must change the extraction's identity, or its cached model-free snapshots are reused.
            ResolveLayoutModel(settings)?.Id ?? "text-layout",
            settings.Ocr,
            AnalysisRenderSize: settings.Ocr == OcrPolicy.Off ? 1024 : 1920,
            DeskewLines: false,
            ExtractorVersions.Current);

    /// <summary>
    /// The layout model to use and its files: Docling Heron when installed, else PP-DocLayoutV3,
    /// unless the <c>LayoutAnalyzer</c> setting names one (block roles are what let narration skip
    /// page furniture, footnotes, references, figures and table cells, and recognise formulas).
    /// Never fetched implicitly. See <see cref="LayoutModelResolver"/> and ADR-0008.
    /// </summary>
    public static LayoutModelChoice? ResolveLayoutModel(RailNarratorSettings settings)
        => LayoutModelResolver.Resolve(settings.LayoutAnalyzer, settings.LayoutModelPath);

    private static ExtractionBackend BuildBackend(RailNarratorSettings settings, List<string> notes)
    {
        // OCR is available out of the box: RailReader.Core.Ocr.RapidOcr ships PP-OCRv5 Latin models
        // beside the application. If they cannot be located, extraction continues and the missing
        // models are reported rather than a scan silently producing nothing.
        Func<IOcrService>? ocrFactory = settings.Ocr == OcrPolicy.Off
            ? null
            : () => new RailReader.Core.Ocr.RapidOcr.RapidOcrService();

        if (ResolveLayoutModel(settings) is { } model)
        {
            string modelPath = model.CpuModelPath;
            try
            {
                var architecture = model.Id == LayoutModelResolver.Heron
                    ? RailReader.Core.Models.LayoutModelArchitecture.Heron
                    : RailReader.Core.Models.LayoutModelArchitecture.PPDocLayoutV3;
                var backend = ExtractionBackend.OnnxLayout(
                    new LayoutModelFiles(architecture, model.Id, model.GpuModelPath, model.CpuModelPath),
                    ocrFactory, settings.LayoutAcceleration);
                if (backend.ResolvedDevice.FallbackReason is { } reason)
                    notes.Add($"Layout analysis runs on the CPU because {reason}. It is slower there but gives the same reading.");
                return backend;
            }
            catch (Exception ex)
            {
                notes.Add($"The layout model at '{modelPath}' could not be loaded ({ex.Message}); using the model-free analyzer.");
                return ExtractionBackend.ModelFree(ocrFactory);
            }
        }

        notes.Add("No layout model is installed, so every block is treated as plain text: headers, footnotes, " +
                  "references and tables cannot be told apart from prose. Models and endpoints > Download layout model " +
                  $"(Docling Heron, {ModelManifest.LayoutHeronInt8.HumanSize}) enables them.");
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
