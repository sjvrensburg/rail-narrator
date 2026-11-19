# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## What this is

Rail Narrator is a Linux desktop app (C# / .NET 10, Avalonia 12, SkiaSharp) that reads a PDF aloud, highlights the passage being spoken in the original document, and follows the reading position. PDF rendering, layout, reading order and OCR come from [RailReaderCore](https://github.com/sjvrensburg/RailReaderCore), consumed as NuGet packages pinned to 0.91.0 — Core cannot be modified here; write an application-side adapter instead of a second PDF stack.

## Commands

```bash
dotnet build RailNarrator.slnx -c Release
dotnet test  RailNarrator.slnx -c Release          # needs no GPU, network, model or sound card

# One project / one class / one test (xunit, VSTest filter)
dotnet test tests/RailNarrator.Domain.Tests -c Release
dotnet test tests/RailNarrator.Domain.Tests -c Release --filter "FullyQualifiedName~SentenceSplitterTests"
dotnet test tests/RailNarrator.Domain.Tests -c Release --filter "FullyQualifiedName~SentenceSplitterTests.<MethodName>"

# Run the app (a PDF argument is loaded, never auto-played)
dotnet run --project src/RailNarrator.App -c Release -- path/to/document.pdf

# Headless harness: fixture | extract | plan | audio | providers | speak | all
dotnet build tools/RailNarrator.Smoke -c Release
tools/RailNarrator.Smoke/bin/Release/net10.0/rail-narrator-smoke all --kokoro-model <path>

# Maths interpretation on a real PDF through the real model (no cache; an hour for a whole paper)
dotnet build tools/RailNarrator.MathBench -c Release
tools/RailNarrator.MathBench/bin/Release/net10.0/rail-narrator-mathbench paper.pdf PP-DocLayoutV3.onnx [--pages 2-8] [--only 12,24] [--thinking off|equations|always]

./packaging/build-release.sh                       # self-contained linux-x64 archive into dist/
bash tools/setup-word-alignment.sh                 # optional MFA runtime (needs micromamba, several GB)
bash tools/kokoro-webgpu-model.sh                  # optional: Kokoro on the GPU via WebGPU (ADR-0007)
```

There is no separate lint step: `TreatWarningsAsErrors` is on for every project except `RailNarrator.App`, whose Avalonia XAML compiler warnings (AVLN…) are not turned into errors. The App is still expected to build with 0 warnings: after XAML changes, check a `--no-incremental` build's raw output (`rtk proxy dotnet build …`), since an incremental build skips unchanged XAML and a filtered summary can hide AVLN lines. Avalonia 12 obsoleted `Watermark`; use `PlaceholderText`.

Opt-in integration tests are skipped unless their environment is set:

| Variables | Enables |
|---|---|
| `RAIL_NARRATOR_KOKORO_MODEL` | real Kokoro synthesis tests (also the smoke tool's default model) |
| `RAIL_NARRATOR_KOKORO_INCOMPATIBLE_MODEL` | incompatible-ONNX-export refusal test |
| `RN_MFA_EXECUTABLE`, `RN_MFA_DICTIONARY`, `RN_MFA_MODEL` (+ Kokoro model) | `RealKokoroAudioAlignsEveryWord` |
| …and `RN_MFA_G2P` | `RealKokoroAudioWithWordsTheDictionaryLacks_AlignsEveryWord` (generated pronunciations) |
| `RN_AUDIO_INTEGRATION=1` | `DeviceIntegrationTests` against a real output device (silent PCM) |
| `RAIL_NARRATOR_LAYOUT_MODEL` | `LayoutModelTests`: a real PP-DocLayoutV3 `.onnx` through the extraction adapter |
| `RAIL_NARRATOR_HERON_MODEL` | `HeronModelTests`: Docling Heron INT8 through the adapter (XY-cut++ order) |
| `RN_WEBGPU_INTEGRATION=1`, `RAIL_NARRATOR_HERON_GPU_MODEL` | `HeronModelTests.OnTheGpu_…`: Heron FP32 on the GPU matches the CPU |
| `RN_WEBGPU_INTEGRATION=1` (+ layout model) | `LayoutAccelerationTests.OnTheGpu_…`: GPU and CPU layout give the same reading (needs a WebGPU-capable GPU) |
| `RN_WEBGPU_INTEGRATION=1`, `RAIL_NARRATOR_KOKORO_MODEL`, `RAIL_NARRATOR_KOKORO_GPU_MODEL` | `KokoroAccelerationTests.OnTheGpu_KokoroGivesTheCpusAudio`: Kokoro on WebGPU matches the CPU |

Runtime prerequisites: `libportaudio.so.2`, and the Kokoro model at `~/.local/share/rail-narrator/models/kokoro-fp16.onnx` (see `docs/model-manifest.json`). Models are never bundled and never fetched implicitly — keep it that way. Settings live in `$XDG_CONFIG_HOME/rail-narrator/settings.json`; `RN_FOLLOW_LOG=1` writes camera/frame-gap diagnostics to `$XDG_STATE_HOME/rail-narrator/logs/follow.log`.

The SDK is pinned by `global.json` (10.0.100, `latestFeature`). Dependency versions live only in `Directory.Packages.props` (central package management); csproj files carry `PackageReference` without versions.

## Architecture

Read `docs/next-steps.md` for the current state, the work in progress and the open product decisions; `docs/testing-real-world.md` for running the real models, GUI and audio (headless compositor, XTEST input, isolated XDG dirs, checking the sink is quiet before playing). For the design read `docs/architecture.md`; ADRs in `docs/decisions/` record the measurement behind each decision, and `docs/status.md` separates what is *verified* from what is merely *implemented*. `docs/word-highlighting.md` covers the MFA setup and alignment failure modes. The original product brief is `rail-narrator-coding-agent-prompt.md` (repo root), with its research baseline in `docs/Rail Narrator implementation investigation.md`; consult them for scope questions, but the ADRs and code record where the project has since deviated.

### The central idea: three representations, two separate mappings

PDF source (text + geometry in page points), the narration script (what is actually spoken, after normalisation and maths paraphrase) and the audio timeline (samples at the provider's rate) do not share words, positions or lengths. The design never indexes one into another directly:

```
audio sample position ──► spoken script range     (CueScheduler / NarrationTimeline, TimedCue)
spoken script range   ──► original source anchors  (SourceMap, SourceLink, TransformedText mappings)
```

Any text transform must carry a `TextMapping` back to its source (`Normalisation.cs`); collapsing or dropping mappings makes highlights cover the wrong text silently.

### Pipeline

```
RailReaderExtractionSource ─► ExtractionSnapshot ─► ReadingUnitBuilder ─► ReadingUnit[]
   ─► NarrationPlanner ─► NarrationPlan (source-linked segments)
   ─► NarrationCoordinator ─► ITtsProvider clips (+ optional IForcedAligner word cues)
   ─► AudioPlaybackEngine media clock ─► ActiveCue ─► overlays + follow camera
```

`MainViewModel.LoadDocumentAsync` is the place where this chain is assembled end to end; `ServiceRegistry` (`src/RailNarrator.App/Composition/`) is the hand-written composition root — no DI container, by design.

### Layering

`RailNarrator.Domain` references nothing (no Skia, ONNX, HTTP or RailReaderCore) and holds the contracts plus all narration logic. Every external capability is a port in `Domain/Ports.cs` (and `IAudioTransport` in `Coordination.cs`), implemented outward:

- `Pdf` — RailReaderCore adapter: extraction, coordinate transforms, hit-testing, overlay rendering
- `Speech` — `ITtsProvider` (Kokoro in-process, ZONOS3 HTTP sidecar, fake), `MfaForcedAligner` driving the embedded `alignment_worker.py`, PCM/WAV codecs
- `Audio` — PortAudio callback output, resampler, WSOLA speed change, presented-frame media clock; `FakeAudioOutputDevice` for tests
- `Inference` — maths transcription (VLM) and verbalisation (LLM), `MathResponseValidator`, `LocalLatexSpeaker` fallback
- `Infrastructure` — XDG paths, settings, disk caches, model manifest/downloader, sidecar lifecycle
- `App` — Avalonia views and view-model, Skia-backed `PdfSurface`

New external dependencies go behind a port in Domain so domain tests keep running with no hardware or models. `tests/RailNarrator.TestFixtures` supplies synthetic PDFs (generated with Skia, known geometry) and synthetic narration shared by the suites.

### Invariants that span files

- **`NarrationCoordinator` is the single authority for playback state.** Camera, overlays, transcript cursor and progress read from it; nothing else runs its own reading clock.
- **Lock order: never call the transport while holding the coordinator lock, and never raise an engine event while holding the engine lock.** `NarrationCoordinator` reads `_audio.*` before `lock (_gate)`; `AudioPlaybackEngine.SetState` is called outside `lock (_gate)`. Breaking either half deadlocks the UI thread during playback (`CoordinatorLockingTests`, `EngineLockingTests`).
- **Painting never renders a page and never calls PDFium.** Core serialises all PDFium work behind one process-wide lock (`PdfiumGate`), which extraction, OCR and maths crops hold for long stretches. `PdfSurface.Render` only draws rasters that `PageRasterSource` (Pdf project, one worker thread, latest request wins, tiled and cancellable) has already produced: the sharp page, else a stand-in (another zoom, a 72 dpi preview), else blank paper. Page sizes come from the source's array, not `IPdfService.GetPageSize`. Likewise `MainViewModel.LoadDocumentAsync` runs its work on the thread pool and only awaits and reports. A render, a hash or a model start-up on the UI thread shows up as "frame gap" lines in the `RN_FOLLOW_LOG=1` log.
- **Fit-width is a mode, not a number** (`ViewZoom`, Pdf project). While it is on, the zoom is re-derived when the page shown or the reading area's width changes (pages differ in size); a zoom the reader chose turns it off and survives page turns, resizes and the next launch (`FitWidth`, `PreferredZoom` in settings). Do not re-fit from a resize or a page change unconditionally.
- **The transcript list seeks through its `SelectedIndex` binding only.** Do not add a `SelectionChanged` handler: the selection also changes when it follows the audio and on restore, and a seek plays.
- **A request to read ends a pause in following.** Browsing (scroll, drag, page turn) suspends the camera and a timer resumes it; `GoToSegmentAsync` (Next, Previous, Replay, a click, the script list), a Play from a stop and a newly loaded plan clear the suspension at once. Resuming from Pause does not: the reader may have paused to look at something.
- **Generation token.** Document change, seek, script edit and voice change advance it; every in-flight job captures its generation and stale results are dropped. UI handlers also compare `snapshot.Generation` / `cue.Generation` to `Coordinator.Generation` before acting. The coordinator's own operations obey it too: each awaits the transport's stop, and the application does not await a voice change, so an operation that finds itself overtaken after an await stops there. `StartPreparation` refuses an older generation (starting it would cancel the newer one's lookahead), a voice change discards prepared clips before it awaits, and a plan load takes a fresh generation when it installs its plan.
- **Media position is presented frames, not wall time.** `AudioPlaybackEngine` derives it from PortAudio callback DAC timestamps; underrun silence consumes no content; `outputLatency` is reported but not subtracted again (ADR-0003). Cue samples are in the clip's own `AudioTimebase`, never device frames. Gapless: the next prepared passage is queued with `IAudioTransport.QueueNextAsync` and written behind the current clip; the engine switches clock and segment only when the boundary frame is *presented* and raises `ClipAdvanced` (not `ClipCompleted`), which the coordinator treats as an advance without a load. Anything that changes a queued passage's words must call `ClearQueuedAsync`.
- **No invented timings.** Word highlighting requires complete, validated forced-alignment evidence; otherwise fall back to whole-sentence (and always whole-equation) cues with a stated reason. Never interpolate by character count (ADR-0004). Alignment is off the playback path: a passage plays once synthesised and its word timing attaches when it arrives (`AlignInBackground`), matched to the held clip by asset id rather than by generation (a Play advances the generation without changing the audio); don't make playback wait for it again. The camera obeys the same rule: a cue with no timing inside it that crosses a page break is framed on the page holding most of it (`SourceMap.FramingPage`, by highlighted area) and held; it does not switch page at a guessed moment, and it does not sit on the first page while the equation overleaf is read. A word the dictionary lacks gets a pronunciation in the worker (acronyms spelled from the dictionary's letter names, the rest from the G2P model) and is still forced-aligned; `<unk>` is never accepted. `SpokenWord.Tokenize` is the one tokeniser for script offsets and aligner words; change it, or the worker protocol, and bump the worker revision in `MfaForcedAligner`.
- **Maths is interpreted during playback, not at load** (`MathMode.Interpret`, the default). The planner leaves display equations and `ContainsMaths` sentences as `ScriptProvenance.PendingInterpretation` holding their fallback (announcement / verbatim sentence); the coordinator resolves them through `ISegmentResolver` (`MathInterpretationResolver` → `IMathInterpreter`) just before synthesis and in a background loop up to `InterpretAheadSegments` ahead, keyed by plan header so a seek neither cancels nor duplicates a model call. Interpreted text is linked to the whole passage and never word-aligned. An unfinished prose unit followed by a display equation is one passage (`NarrationPlanner.LeadsIntoEquation`). Cached answers skip validation, so change `MathInterpretationPrompt` *or* `SpokenMathsValidator` → bump `MathInterpretationPrompt.Version`. Thinking follows `MathThinking` (default `EquationsOnly`: display equations and lead-ins only); a thinking answer cut off at the token limit is retried once with thinking off. A refused interpretation goes to `IMathReader` (the same model transcribes the crop to LaTeX, `LocalLatexSpeaker` reads it ClearSpeak-style) and becomes a labelled `LocalRule` segment; phrasing changes in `LocalLatexSpeaker` → bump `LocalLatexSpeaker.Version`, transcription-prompt changes → bump `TranscriptionPromptVersion`. Play pre-rolls: the coordinator waits for `PreRollSeconds` of ready narration ahead, capped at `PreRollTimeoutSeconds`. Page crops come from PDFium as 3-byte RGB (`RenderCropPngAsync` converts to RGBA); a crop failure is a passage warning, never swallowed.
- **No LaTeX reaches a speech engine.** Model output passes `MathResponseValidator`; on failure or with no verbaliser the segment becomes an explicit "could not be read reliably" announcement, or the labelled `LocalRule` reading.
- **Offsets are UTF-16 and typed** (`TextOffsetUnit`, `TextOffsets`); anchors are single-page with disjoint rectangles and are stamped with `ExtractionIdentity`. `PageRect.UnionAll` is for camera framing only, never highlight geometry.
- **Word boundaries: the text layer proposes, geometry confirms** (ADR-0005). Neither is trusted alone — ink gaps split "2017" at a tabular 1 and hide the space after an "f"; the text layer can hold phantom whitespace inside words. Control characters in page text are not noise: they are unmapped ligature glyphs (`UnmappedGlyphResolver`) or PDFium's line-end hyphen marker (U+0002).
- **The layout model is Docling Heron when installed** (ADR-0008), chosen by `LayoutModelResolver` (setting `LayoutAnalyzer`: `heron`, `pp-doclayout-v3`, `none`, else automatic) and built with Core's `LayoutAnalyzerFactory`: FP32 on the GPU, INT8 on the CPU, XY-cut++ order. Heron has no reference class, so `ReferenceSections` labels the prose under a References heading when no block is already a reference; keep that, or a bibliography is read aloud.
- **Blocks are sorted into reading order before Core's post-processing.** `BlockPostProcessor.PostProcess` expects reading order and renumbers by list position when it removes a duplicate block; a model with its own order (PP-DocLayoutV3) returns blocks by confidence with the order in a field. Every analyzer's blocks therefore go through a resolver first (`ModelOrderResolver` or `XYCutPlusPlusResolver`), as in Core's `AnalysisWorker`. Skipping it for a model "that already has an order" read pages in confidence order.
- **Layout roles decide when a model supplied them** (`ExtractionSnapshot.HasSemanticRoles`). The geometric display-maths and running-element heuristics are the fallback for the model-free analyzer, which labels every block Text; running them over a model's Text blocks turned prose lines into "equations". A layout model takes a raster at exactly its `Capabilities.InputSize`. Inline maths never becomes a unit of its own: it flags its sentence (`ReadingUnit.ContainsMaths`). Tables and algorithms are one unit each, planned as a `StructureAnnouncement`.
- **Caching is per stage** (`Domain/Caching.cs`), each key folding in exactly the inputs that affect that stage. When changing output-affecting logic, bump the matching version constant — `ExtractorVersions.Current`, `NarrationVersions.{MathPrompt,LocalMathReader,ProsePipeline}`, or the worker revision in `MfaForcedAligner` — or stale cached snapshots/plans/audio/timings will be reused. Maths has a second, separate layer: cached model answers are keyed by the interpreter's `Revision` (`MathInterpreter.cs`: `MathInterpretationPrompt.Version`, `TranscriptionPromptVersion`, `LocalLatexSpeaker.Version`), not by `NarrationVersions` — bumping one does not invalidate the other. Extraction snapshots are cached on disk by `CachingExtractionSource` (Infrastructure); the app extracts through `ServiceRegistry.CachedExtraction` and keeps `Extraction` for rendering and hit-testing.
- **Layout on the GPU goes through `LayoutAnalyzerConstruction` only** (ADR-0006). Core's WebGPU switch is a static `LayoutAnalyzer.ConfigureSession` hook, so every `LayoutAnalyzer` construction, CPU ones included, holds `WebGpuAccelerator.ConstructionLock` for enable → construct → disable. Never `new LayoutAnalyzer(...)` elsewhere. The device (`LayoutDevice.Id`) is part of the extraction cache key: CPU and GPU give the same reading but not byte-identical geometry. A GPU that fails at start-up or mid-document hands over to the CPU with a page warning; OCR stays on the CPU.
- **Kokoro on the GPU goes through WebGPU only, never CUDA or ROCm** (ADR-0007). The GPU model is the fp32 export with int32 `tokens` (the plugin aborts on int64 inputs; fp16 gives NaN on it), run by `KokoroSessionRunner` with inputs built exactly as KokoroSharp builds them (`KokoroInputs`). Every GPU clip passes `KokoroOutputCheck` or synthesis falls back to the CPU for the run (`KokoroRunnerChain`). The CPU model revision must stay unchanged so cached audio stays valid; the GPU's names the device.
- **Host services are supplied, not inherited from Core.** Core's `AppConfig` hard-codes `~/.config/railreader2`; this app uses its own XDG directories (`XdgPaths`) and an inert `NullAnnotationStore`. Highlights are transient overlays and must never be written into the PDF.
- **Providers are registered only when implemented and configured**, so the UI never offers an inert selector. An API key is either read from an environment variable named in settings or stored in `settings.json` (`MathLlmApiKey`, `VlmApiKey`; the stored key wins); `SettingsStore.Save` writes the file mode 0600 from creation, so keep every write going through it, and never log, display or put a key in a message (`EndpointProbe` reports refusals without it).

### Settings dialogue

`SettingsWindow` / `SettingsViewModel` (File ▸ Settings…, Ctrl+,) saves each change as it is made through `MainViewModel.PersistSettings` (read, change, write, so the main window's own preference saves survive). Reading preferences apply at once by updating only those fields of `ServiceRegistry.CurrentSettings`; engines (speech, maths, layout, filters) are built once in `ServiceRegistry.Create`, so their changes apply after a restart, which the dialogue offers (it relaunches with the open document after the position is saved). A new setting: add it to `RailNarratorSettings`, to the dialogue, and, if it applies at once, to `SettingsViewModel.SaveLive` and to the fields `EngineSignature` leaves out.

### Look and feel (follows RailReader2)

Fluent, light by default; dark and the text size (`DarkMode`, `UiFontScale`) apply live through `App.ApplyAppearance`, and a window opened later takes the main window's `FontSize`. Colours come from Fluent's theme brushes or the theme dictionaries in `App.axaml` (`ReadingCanvasColor`, `OverlayCardBrush`, `OverlayVeilBrush`); don't hard-code a colour that must read in both themes. Icons are Lucide `StreamGeometry`s in `Assets/Icons.axaml` drawn by `Controls/Icon` (copied from RailReader2; sized from the inherited font size, so they scale with the text); an icon button gets `AutomationProperties.Name` and its shortcut in the tooltip. Button classes `toolBtn`, `borderless`, `active`, `primary` and `toolSep`/`overlayCard` are defined in `App.axaml`.

The sidebar (F6) is on the left, as RailReader2's: an accordion (`MainWindow.SetUpSidebar`, one section open, its row starred) of Outline, Script, Maths review and Notes; width, visibility and open section are reader preferences in settings (excluded from `EngineSignature`). The Outline is the PDF's bookmarks (`PageRasterSource.Outline`, read off the UI thread at open; pages zero-based). Choosing a heading while playing seeks there; otherwise it shows the page and sets a pending start that the next Play consumes and any other seek clears (`MainViewModel.StartReadingAsync`). Its `SelectionChanged` handler is safe because nothing syncs the tree to the reading; the script list's rule (seek only through `SelectedIndex`) still stands. Status-bar notices (`ShowNotice`) take the place of the status line with one action; raise one per event, not per snapshot. Start-up builds `ServiceRegistry` off the UI thread behind `SplashWindow` (`App.StartAsync`; keep `ServiceRegistry.Create` free of Avalonia), with `StartupErrorWindow` if it throws. `MainViewModel.CloseDocumentAsync` saves the position, stops and returns to the welcome screen; `RecentDocumentsStore` keeps the recent list and thumbnails. A PDF dropped on the window opens through `LoadDocumentAsync` (`MainWindow.SetUpFileDrop`; Avalonia 12's `e.DataTransfer.TryGetFiles()` and `TryGetLocalPath()`).

### Documentation convention

`docs/status.md` and the README distinguish verified claims (with the measurement) from unverified ones and from deliberate non-features. When behaviour or verification status changes, update those documents in the same terms rather than claiming more than was measured.
