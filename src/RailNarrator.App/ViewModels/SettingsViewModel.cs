using System.Collections.ObjectModel;
using System.Text.Json;
using Avalonia.Threading;
using RailNarrator.App.Composition;
using RailNarrator.Domain;
using RailNarrator.Inference;
using RailNarrator.Infrastructure;
using RailNarrator.Speech;

namespace RailNarrator.App.ViewModels;

/// <summary>A choice in a settings list: the value saved, its name, and what it does.</summary>
public sealed record SettingOption<T>(T Value, string Name, string Description = "")
{
    public override string ToString() => Name;
}

/// <summary>Where an endpoint's API key comes from.</summary>
public enum ApiKeySource
{
    /// <summary>Read from an environment variable when Rail Narrator starts. Not stored.</summary>
    EnvironmentVariable,

    /// <summary>Typed into the dialogue and stored in the settings file (owner-only).</summary>
    Stored,
}

/// <summary>
/// One model endpoint's settings in the dialogue: which API it speaks (OpenAI-compatible, or Claude
/// through Anthropic's), address, model and key, and a test that checks them as typed, before they
/// are used.
/// </summary>
public sealed class EndpointSettingsViewModel : ViewModelBase
{
    private readonly Action<Func<RailNarratorSettings, RailNarratorSettings>> _save;
    private readonly Func<RailNarratorSettings, LlmProvider, RailNarratorSettings> _withProvider;
    private readonly Func<RailNarratorSettings, string?, RailNarratorSettings> _withUrl, _withModel, _withVariable, _withKey;
    private LlmProvider _provider;
    private string _url, _model, _variable, _key;
    private ApiKeySource _source;
    private string _testResult = "";
    private bool _testing;
    private bool? _testPassed;

    public EndpointSettingsViewModel(
        LlmProvider provider, string? url, string? model, string? variable, string? key,
        Action<Func<RailNarratorSettings, RailNarratorSettings>> save,
        Func<RailNarratorSettings, LlmProvider, RailNarratorSettings> withProvider,
        Func<RailNarratorSettings, string?, RailNarratorSettings> withUrl,
        Func<RailNarratorSettings, string?, RailNarratorSettings> withModel,
        Func<RailNarratorSettings, string?, RailNarratorSettings> withVariable,
        Func<RailNarratorSettings, string?, RailNarratorSettings> withKey)
    {
        _provider = provider;
        _withProvider = withProvider;
        _url = url ?? "";
        _model = model ?? "";
        _variable = variable ?? "";
        _key = key ?? "";
        _source = string.IsNullOrWhiteSpace(key) ? ApiKeySource.EnvironmentVariable : ApiKeySource.Stored;
        _save = save;
        (_withUrl, _withModel, _withVariable, _withKey) = (withUrl, withModel, withVariable, withKey);
        Test = new RelayCommand(TestAsync, () => !_testing);
    }

    public IReadOnlyList<SettingOption<LlmProvider>> Providers { get; } =
    [
        new(LlmProvider.OpenAiCompatible, "OpenAI-compatible endpoint",
            "A local server (llama.cpp, Ollama, LM Studio) or a hosted one that speaks the OpenAI chat API."),
        new(LlmProvider.Anthropic, "Claude (Anthropic)",
            "Claude, through Anthropic's API. Each passage is sent to Anthropic: its page image and the text around it. " +
            $"Leave the address empty unless you use a gateway; an empty model means {ClaudeEndpointSettings.DefaultModel}."),
    ];

    public SettingOption<LlmProvider>? Provider
    {
        get => Providers.First(o => o.Value == _provider);
        set
        {
            if (value is null || !Set(ref _provider, value.Value)) return;
            _save(s => _withProvider(s, value.Value));
            OnPropertyChanged(nameof(UsesClaude));
            OnPropertyChanged(nameof(UrlPlaceholder));
            OnPropertyChanged(nameof(ModelPlaceholder));
            OnPropertyChanged(nameof(VariablePlaceholder));
            OnPropertyChanged(nameof(KeyStatus));
            // What the other API offered and answered says nothing about this one.
            OfferedModels.Clear();
            TestResult = "";
            TestPassed = null;
        }
    }

    public bool UsesClaude => _provider == LlmProvider.Anthropic;

    public string UrlPlaceholder => UsesClaude ? "empty for Anthropic's own API" : "https://…/v1";

    public string ModelPlaceholder => UsesClaude ? ClaudeEndpointSettings.DefaultModel : "model name";

    public string VariablePlaceholder => UsesClaude ? "variable name, e.g. ANTHROPIC_API_KEY" : "variable name, e.g. OPENAI_API_KEY";

    public string Url
    {
        get => _url;
        set { if (Set(ref _url, value ?? "")) _save(s => _withUrl(s, Blank(value))); }
    }

    public string Model
    {
        get => _model;
        set { if (Set(ref _model, value ?? "")) _save(s => _withModel(s, Blank(value))); }
    }

    public ApiKeySource Source
    {
        get => _source;
        set
        {
            if (!Set(ref _source, value)) return;
            OnPropertyChanged(nameof(UsesVariable));
            OnPropertyChanged(nameof(UsesStoredKey));
            // Choosing the variable removes a stored key from the file rather than leaving it there
            // unused: the reader chose not to keep it on disk.
            if (value == ApiKeySource.EnvironmentVariable && _key.Length > 0)
            {
                _key = "";
                OnPropertyChanged(nameof(Key));
                _save(s => _withKey(s, null));
            }
            OnPropertyChanged(nameof(KeyStatus));
        }
    }

    public bool UsesVariable { get => _source == ApiKeySource.EnvironmentVariable; set { if (value) Source = ApiKeySource.EnvironmentVariable; } }

    public bool UsesStoredKey { get => _source == ApiKeySource.Stored; set { if (value) Source = ApiKeySource.Stored; } }

    public string Variable
    {
        get => _variable;
        set
        {
            if (!Set(ref _variable, value ?? "")) return;
            _save(s => _withVariable(s, Blank(value)));
            OnPropertyChanged(nameof(KeyStatus));
        }
    }

    public string Key
    {
        get => _key;
        set
        {
            if (!Set(ref _key, value ?? "")) return;
            _save(s => _withKey(s, Blank(value)));
            OnPropertyChanged(nameof(KeyStatus));
        }
    }

    /// <summary>What the key will be, said without showing it.</summary>
    public string KeyStatus
    {
        get
        {
            // Without a key from here, Anthropic's SDK finds its own.
            const string claudeFindsItsOwn = "so Claude's own key is looked for: ANTHROPIC_API_KEY in Rail Narrator's environment, or a profile from 'ant auth login'.";
            if (_source == ApiKeySource.Stored)
                return _key.Trim().Length == 0
                    ? UsesClaude ? "No key is stored, " + claudeFindsItsOwn : "No key is stored. Leave it empty for an endpoint that needs none."
                    : "Stored in the settings file, which only you can read.";
            if (_variable.Trim().Length == 0)
                return UsesClaude ? "No variable is named, " + claudeFindsItsOwn : "No variable is named, so no key is sent.";
            return string.IsNullOrEmpty(Environment.GetEnvironmentVariable(_variable.Trim()))
                ? $"{_variable.Trim()} is not set in Rail Narrator's environment. A variable exported in ~/.bashrc is not seen when the app is started from the desktop menu; store the key instead, or set the variable in ~/.profile."
                : $"{_variable.Trim()} is set in Rail Narrator's environment.";
        }
    }

    /// <summary>Model names the endpoint offered when last tested, for the model box's suggestions.</summary>
    public ObservableCollection<string> OfferedModels { get; } = [];

    public RelayCommand Test { get; }

    public string TestResult { get => _testResult; private set => Set(ref _testResult, value); }

    /// <summary>True when the last test passed, false when it failed, null before any test.</summary>
    public bool? TestPassed
    {
        get => _testPassed;
        private set
        {
            if (!Set(ref _testPassed, value)) return;
            OnPropertyChanged(nameof(TestSucceeded));
            OnPropertyChanged(nameof(TestFailed));
        }
    }

    public bool TestSucceeded => _testPassed == true;

    public bool TestFailed => _testPassed == false;

    public bool Testing { get => _testing; private set { if (Set(ref _testing, value)) Test.RaiseCanExecuteChanged(); } }

    private async Task TestAsync()
    {
        Testing = true;
        TestResult = UsesClaude ? "Asking for Claude's models…" : "Asking the endpoint for its models…";
        TestPassed = null;
        try
        {
            string? key = RailNarratorSettings.ResolveApiKey(
                _source == ApiKeySource.Stored ? _key : null,
                _source == ApiKeySource.EnvironmentVariable ? _variable : null);
            var result = UsesClaude
                ? await ClaudeEndpointProbe.ProbeAsync(_url, _model, key)
                : await EndpointProbe.ProbeAsync(_url, _model, key);
            Dispatcher.UIThread.Post(() =>
            {
                TestResult = result.Message;
                // A server that lists no models may still serve the one named: not a failure.
                TestPassed = result.IsReady || (result.Status == EndpointProbeStatus.ModelNotListed && result.Models.Count == 0);
                OfferedModels.Clear();
                foreach (string m in result.Models) OfferedModels.Add(m);
            });
        }
        finally { Dispatcher.UIThread.Post(() => Testing = false); }
    }

    private static string? Blank(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}

/// <summary>
/// The Settings dialogue. Every change is saved as it is made, as in RailReader2. Reading and
/// highlighting preferences apply at once; the engines (speech, maths, layout, what is read) are
/// built when the application starts, so a change to them is saved and applies after a restart,
/// which the dialogue offers.
/// </summary>
public sealed class SettingsViewModel : ViewModelBase
{
    private readonly MainViewModel _main;
    private readonly ServiceRegistry _services;
    private readonly string _startupEngine;
    private RailNarratorSettings _s;
    private bool _restartPending;
    private string? _saveError;
    private bool _advanced;

    public SettingsViewModel(MainViewModel main)
    {
        _main = main ?? throw new ArgumentNullException(nameof(main));
        _services = main.Services;
        _s = _services.Settings.Load();
        _startupEngine = EngineSignature(_services.CurrentSettings);
        _restartPending = EngineSignature(_s) != _startupEngine;

        MathEndpoint = new EndpointSettingsViewModel(_s.MathLlmProvider, _s.MathLlmBaseUrl, _s.MathLlmModel, _s.MathLlmApiKeyEnvVar, _s.MathLlmApiKey, Save,
            (s, v) => s with { MathLlmProvider = v }, (s, v) => s with { MathLlmBaseUrl = v }, (s, v) => s with { MathLlmModel = v },
            (s, v) => s with { MathLlmApiKeyEnvVar = v }, (s, v) => s with { MathLlmApiKey = v });
        VisionEndpoint = new EndpointSettingsViewModel(_s.VlmProvider, _s.VlmBaseUrl, _s.VlmModel, _s.VlmApiKeyEnvVar, _s.VlmApiKey, Save,
            (s, v) => s with { VlmProvider = v }, (s, v) => s with { VlmBaseUrl = v }, (s, v) => s with { VlmModel = v },
            (s, v) => s with { VlmApiKeyEnvVar = v }, (s, v) => s with { VlmApiKey = v });
        RestartNow = new RelayCommand(() => RestartRequested?.Invoke());
    }

    /// <summary>The reader asked to restart now, to apply engine changes.</summary>
    public event Action? RestartRequested;

    public RelayCommand RestartNow { get; }

    /// <summary>Engine settings on disk differ from the ones the application started with.</summary>
    public bool RestartPending { get => _restartPending; private set => Set(ref _restartPending, value); }

    /// <summary>Set when a change could not be written; shown in the dialogue's footer.</summary>
    public string? SaveError { get => _saveError; private set => Set(ref _saveError, value); }

    public bool Advanced { get => _advanced; set => Set(ref _advanced, value); }

    public string SettingsFile => _services.Settings.FilePath;

    // ---- Appearance: applies at once ---------------------------------------------------------

    public bool DarkMode
    {
        get => _s.DarkMode;
        set
        {
            SaveLive(s => s with { DarkMode = value });
            App.ApplyAppearance(_s.DarkMode, _s.UiFontScale);
        }
    }

    /// <summary>Menu, toolbar and panel text size as a percentage, 75 to 200.</summary>
    public double UiTextSizePercent
    {
        get => Math.Round(_s.UiFontScale * 100);
        set
        {
            SaveLive(s => s with { UiFontScale = Math.Clamp(Math.Round(value / 5) * 5, 75, 200) / 100.0 });
            App.ApplyAppearance(_s.DarkMode, _s.UiFontScale);
            OnPropertyChanged();
        }
    }

    // ---- Reading: applies at once ------------------------------------------------------------

    public double FollowAutoResumeSeconds
    {
        get => _s.FollowAutoResumeSeconds;
        set => SaveLive(s => s with { FollowAutoResumeSeconds = Math.Clamp(Math.Round(value), 0, 120) });
    }

    public bool ReduceMotion
    {
        get => _s.ReduceMotion;
        set => SaveLive(s => s with { ReduceMotion = value });
    }

    /// <summary>Highlight opacity as a percentage, 10 to 80.</summary>
    public double HighlightOpacityPercent
    {
        get => Math.Round(_s.HighlightAlpha / 2.55);
        set
        {
            byte alpha = (byte)Math.Clamp(Math.Round(value * 2.55), 25, 205);
            SaveLive(s => s with { HighlightAlpha = alpha });
            _main.HighlightAlpha = alpha;
        }
    }

    public bool HighlightHighContrast
    {
        get => _s.HighlightHighContrast;
        set
        {
            SaveLive(s => s with { HighlightHighContrast = value });
            _main.HighlightHighContrast = value;
        }
    }

    public bool WordHighlighting
    {
        get => _s.WordHighlighting;
        set => Save(s => s with { WordHighlighting = value });
    }

    // ---- Speech: after a restart -------------------------------------------------------------

    public IReadOnlyList<SettingOption<string>> SpeechProviders { get; } =
    [
        new(SpeechProviderIds.Auto, "Automatic", "Phonon when it is installed, else Kokoro."),
        new(SpeechProviderIds.Phonon, "Phonon (on this computer)", "Gradium's phonon-31m-en, run in Rail Narrator itself on the processor: about 16 times faster than real time, British and American voices. Install it with tools/setup-phonon.sh."),
        new(SpeechProviderIds.Kokoro, "Kokoro (on this computer)", "Kokoro-82M, run in Rail Narrator itself. Needs the Kokoro model (Models and endpoints menu)."),
        new(SpeechProviderIds.Zonos, "ZONOS3 (local server)", "A ZONOS3 sidecar on this computer, reached over HTTP. Set its address and speaker below (advanced)."),
    ];

    public SettingOption<string>? SpeechProvider
    {
        // "zonos3" is what this dialogue saved for ZONOS3 before it used the provider's own id.
        get => SpeechProviders.FirstOrDefault(o => o.Value == (_s.SpeechProviderId == "zonos3" ? SpeechProviderIds.Zonos : _s.SpeechProviderId))
               ?? SpeechProviders[0];
        set { if (value is not null) Save(s => s with { SpeechProviderId = value.Value }); }
    }

    public IReadOnlyList<SettingOption<SpeechAcceleration>> SpeechAccelerations { get; } =
    [
        new(SpeechAcceleration.Auto, "GPU when available", "Kokoro on the graphics card (WebGPU) when its GPU model is installed, else the processor."),
        new(SpeechAcceleration.Cpu, "Processor only", "Never use the graphics card for speech."),
    ];

    public SettingOption<SpeechAcceleration>? KokoroAcceleration
    {
        get => SpeechAccelerations.First(o => o.Value == _s.KokoroAcceleration);
        set { if (value is not null) Save(s => s with { KokoroAcceleration = value.Value }); }
    }

    /// <summary>Seconds of listening prepared ahead of the passage being read.</summary>
    public double LookaheadSeconds
    {
        get => _s.LookaheadSeconds;
        set => Save(s => s with { LookaheadSeconds = Math.Clamp(Math.Round(value), 10, 600) });
    }

    public string KokoroModelPath { get => _s.KokoroModelPath ?? ""; set => Save(s => s with { KokoroModelPath = Blank(value) }); }

    public string KokoroGpuModelPath { get => _s.KokoroGpuModelPath ?? ""; set => Save(s => s with { KokoroGpuModelPath = Blank(value) }); }

    public string ZonosBaseUrl { get => _s.ZonosBaseUrl ?? ""; set => Save(s => s with { ZonosBaseUrl = Blank(value) }); }

    public string ZonosSpeakerId { get => _s.ZonosSpeakerId ?? ""; set => Save(s => s with { ZonosSpeakerId = Blank(value) }); }

    // ---- Maths: after a restart --------------------------------------------------------------

    public IReadOnlyList<SettingOption<MathMode>> MathModes { get; } =
    [
        new(MathMode.Interpret, "Say what it means", "A language model says what each equation and each sentence with maths means, from the page image and the text around it. Needs a language model below."),
        new(MathMode.NaturalFaithful, "Read it faithfully", "Read the expression as a mathematician would say it, keeping every operator, bound and index."),
        new(MathMode.DetailedSymbolic, "Read every symbol", "Speak every component, in natural syntax."),
        new(MathMode.Explain, "Read it, then explain", "A faithful reading followed by a short interpretation, marked as generated."),
    ];

    public SettingOption<MathMode>? SelectedMathMode
    {
        get => MathModes.FirstOrDefault(o => o.Value == _s.MathMode) ?? MathModes[0];
        set { if (value is not null) Save(s => s with { MathMode = value.Value }); }
    }

    public IReadOnlyList<SettingOption<MathThinkingPolicy>> ThinkingPolicies { get; } =
    [
        new(MathThinkingPolicy.EquationsOnly, "For equations", "A reasoning model thinks before interpreting display equations and the sentences that lead into them; other sentences are answered directly. Claude is asked for medium effort on those and low on the rest."),
        new(MathThinkingPolicy.Always, "Always", "Think before every passage: most faithful, about ten times slower. Claude: medium effort throughout."),
        new(MathThinkingPolicy.Off, "Never", "Answer directly: fastest. Endpoints without a thinking switch ignore this. Claude: low effort throughout."),
    ];

    public SettingOption<MathThinkingPolicy>? Thinking
    {
        get => ThinkingPolicies.First(o => o.Value == _s.EffectiveMathThinking);
        // The older switch would override the choice, so it is cleared when a choice is made.
        set { if (value is not null) Save(s => s with { MathLlmThinking = value.Value, MathLlmDisableThinking = false }); }
    }

    public EndpointSettingsViewModel MathEndpoint { get; }

    public EndpointSettingsViewModel VisionEndpoint { get; }

    // ---- Document: after a restart -----------------------------------------------------------

    public bool ExcludeRunningHeaders { get => _s.Filters.ExcludeRunningHeaders; set => SaveFilters(f => f with { ExcludeRunningHeaders = value }); }
    public bool ExcludeFooters { get => _s.Filters.ExcludeFooters; set => SaveFilters(f => f with { ExcludeFooters = value }); }
    public bool ExcludePageNumbers { get => _s.Filters.ExcludePageNumbers; set => SaveFilters(f => f with { ExcludePageNumbers = value }); }
    public bool IncludeCitations { get => _s.Filters.IncludeCitations; set => SaveFilters(f => f with { IncludeCitations = value }); }
    public bool IncludeFootnotes { get => _s.Filters.IncludeFootnotes; set => SaveFilters(f => f with { IncludeFootnotes = value }); }
    public bool IncludeCaptions { get => _s.Filters.IncludeCaptions; set => SaveFilters(f => f with { IncludeCaptions = value }); }
    public bool IncludeReferences { get => _s.Filters.IncludeReferences; set => SaveFilters(f => f with { IncludeReferences = value }); }
    public bool IncludeEquations { get => _s.Filters.IncludeEquations; set => SaveFilters(f => f with { IncludeEquations = value }); }
    public bool IncludeTables { get => _s.Filters.IncludeTables; set => SaveFilters(f => f with { IncludeTables = value }); }

    public IReadOnlyList<SettingOption<string>> LayoutAnalyzers { get; } =
    [
        new("auto", "Automatic", "Docling Heron if installed, else PP-DocLayoutV3, else no model."),
        new(LayoutModelResolver.Heron, "Docling Heron", "The layout model RailReader2 uses. Download it from the Models and endpoints menu."),
        new(LayoutModelResolver.PPDocLayoutV3, "PP-DocLayoutV3", "The older layout model, with its own reading order."),
        new("none", "No model", "Geometry only: fast, but tables, captions and headings are not recognised."),
    ];

    public SettingOption<string>? LayoutAnalyzer
    {
        get => LayoutAnalyzers.FirstOrDefault(o => o.Value == _s.LayoutAnalyzer) ?? LayoutAnalyzers[0];
        set { if (value is not null) Save(s => s with { LayoutAnalyzer = value.Value }); }
    }

    public IReadOnlyList<SettingOption<LayoutAcceleration>> LayoutAccelerations { get; } =
    [
        new(LayoutAcceleration.Auto, "GPU when available", "Layout analysis on the graphics card (WebGPU) when it works, else the processor."),
        new(LayoutAcceleration.Cpu, "Processor only", "Never use the graphics card for layout."),
    ];

    public SettingOption<LayoutAcceleration>? SelectedLayoutAcceleration
    {
        get => LayoutAccelerations.First(o => o.Value == _s.LayoutAcceleration);
        set { if (value is not null) Save(s => s with { LayoutAcceleration = value.Value }); }
    }

    public IReadOnlyList<SettingOption<OcrPolicy>> OcrPolicies { get; } =
    [
        new(OcrPolicy.Full, "Scanned pages and broken text", "Recognise text on scanned pages, and on lines whose text layer is missing or garbled."),
        new(OcrPolicy.LinesOnly, "Broken text only", "Recognise only lines whose text layer is missing or garbled."),
        new(OcrPolicy.Off, "Never", "Use the PDF's text layer only. A scanned page is read as silence."),
    ];

    public SettingOption<OcrPolicy>? Ocr
    {
        get => OcrPolicies.First(o => o.Value == _s.Ocr);
        set { if (value is not null) Save(s => s with { Ocr = value.Value }); }
    }

    public string LayoutModelPath { get => _s.LayoutModelPath ?? ""; set => Save(s => s with { LayoutModelPath = Blank(value) }); }

    // ---- saving ------------------------------------------------------------------------------

    private void SaveFilters(Func<NarrationFilterSettings, NarrationFilterSettings> update)
        => Save(s => s with { Filters = update(s.Filters) });

    /// <summary>Saves a change to the engines: it applies after a restart.</summary>
    private void Save(Func<RailNarratorSettings, RailNarratorSettings> update)
    {
        if (!Write(update)) return;
        RestartPending = EngineSignature(_s) != _startupEngine;
    }

    /// <summary>Saves a reading preference and applies it now.</summary>
    private void SaveLive(Func<RailNarratorSettings, RailNarratorSettings> update)
    {
        if (!Write(update)) return;
        // Only the live fields: the engines keep the settings they were built with.
        _services.CurrentSettings = _services.CurrentSettings with
        {
            FollowAutoResumeSeconds = _s.FollowAutoResumeSeconds,
            ReduceMotion = _s.ReduceMotion,
            HighlightAlpha = _s.HighlightAlpha,
            HighlightHighContrast = _s.HighlightHighContrast,
            DarkMode = _s.DarkMode,
            UiFontScale = _s.UiFontScale,
        };
    }

    /// <summary>Read, change and write the file, so a preference the main window saved meanwhile survives.</summary>
    private bool Write(Func<RailNarratorSettings, RailNarratorSettings> update)
    {
        try
        {
            _main.PersistSettings(current => _s = update(current));
            SaveError = null;
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            SaveError = $"Could not save the settings file: {ex.Message}";
            return false;
        }
    }

    /// <summary>
    /// The settings the engines are built from, as one comparable string. The reader's own
    /// preferences, which apply at once or are only remembered (zoom, rate, colour, position),
    /// are left out.
    /// </summary>
    private static string EngineSignature(RailNarratorSettings settings)
        => JsonSerializer.Serialize(settings with
        {
            LastDocumentPath = null,
            PlaybackRate = 1,
            FollowMode = FollowMode.Follow,
            PreferredZoom = 1,
            FitWidth = true,
            ReduceMotion = false,
            ColourEffect = "None",
            ColourEffectIntensity = 1,
            FollowAutoResumeSeconds = 0,
            HighlightAlpha = 0,
            HighlightHighContrast = false,
            KokoroVoiceId = null,
            PhononVoiceId = null,
            DarkMode = false,
            UiFontScale = 1,
            SidebarVisible = false,
            SidebarWidth = 300,
            SidebarSection = "Outline",
        });

    private static string? Blank(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
