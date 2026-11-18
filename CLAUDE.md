# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## What this is

Rail Narrator is a Linux desktop app (C# / .NET 10, Avalonia 12, SkiaSharp) that reads a PDF aloud, highlights the passage being spoken in the original document, and follows the reading position. PDF rendering, layout, reading order and OCR come from [RailReaderCore](https://github.com/sjvrensburg/RailReaderCore), consumed as NuGet packages pinned to 0.90.0 — Core cannot be modified here; write an application-side adapter instead of a second PDF stack.

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
```

There is no separate lint step: `TreatWarningsAsErrors` is on for every project except `RailNarrator.App` (the Avalonia XAML compiler emits advisories), so the build is the lint and it is expected to stay at 0 warnings.

Opt-in integration tests are skipped unless their environment is set:

| Variables | Enables |
|---|---|
| `RAIL_NARRATOR_KOKORO_MODEL` | real Kokoro synthesis tests (also the smoke tool's default model) |
| `RAIL_NARRATOR_KOKORO_INCOMPATIBLE_MODEL` | incompatible-ONNX-export refusal test |
| `RN_MFA_EXECUTABLE`, `RN_MFA_DICTIONARY`, `RN_MFA_MODEL` (+ Kokoro model) | `RealKokoroAudioAlignsEveryWord` |
| `RN_AUDIO_INTEGRATION=1` | `DeviceIntegrationTests` against a real output device (silent PCM) |
| `RAIL_NARRATOR_LAYOUT_MODEL` | `LayoutModelTests`: a real PP-DocLayoutV3 `.onnx` through the extraction adapter |

Runtime prerequisites: `libportaudio.so.2`, and the Kokoro model at `~/.local/share/rail-narrator/models/kokoro-fp16.onnx` (see `docs/model-manifest.json`). Models are never bundled and never fetched implicitly — keep it that way.

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
- **The transcript list seeks through its `SelectedIndex` binding only.** Do not add a `SelectionChanged` handler: the selection also changes when it follows the audio and on restore, and a seek plays.
- **Generation token.** Document change, seek, script edit and voice change advance it; every in-flight job captures its generation and stale results are dropped. UI handlers also compare `snapshot.Generation` / `cue.Generation` to `Coordinator.Generation` before acting.
- **Media position is presented frames, not wall time.** `AudioPlaybackEngine` derives it from PortAudio callback DAC timestamps; underrun silence consumes no content; `outputLatency` is reported but not subtracted again (ADR-0003). Cue samples are in the clip's own `AudioTimebase`, never device frames.
- **No invented timings.** Word highlighting requires complete, validated forced-alignment evidence; otherwise fall back to whole-sentence (and always whole-equation) cues with a stated reason. Never interpolate by character count (ADR-0004).
- **Maths is interpreted during playback, not at load** (`MathMode.Interpret`, the default). The planner leaves display equations and `ContainsMaths` sentences as `ScriptProvenance.PendingInterpretation` holding their fallback (announcement / verbatim sentence); the coordinator resolves them through `ISegmentResolver` (`MathInterpretationResolver` → `IMathInterpreter`) just before synthesis and in a background loop up to `InterpretAheadSegments` ahead, keyed by plan header so a seek neither cancels nor duplicates a model call. Interpreted text is linked to the whole passage and never word-aligned. An unfinished prose unit followed by a display equation is one passage (`NarrationPlanner.LeadsIntoEquation`). Cached answers skip validation, so change `MathInterpretationPrompt` *or* `SpokenMathsValidator` → bump `MathInterpretationPrompt.Version`. Thinking follows `MathThinking` (default `EquationsOnly`: display equations and lead-ins only); a thinking answer cut off at the token limit is retried once with thinking off. A refused interpretation goes to `IMathReader` (the same model transcribes the crop to LaTeX, `LocalLatexSpeaker` reads it ClearSpeak-style) and becomes a labelled `LocalRule` segment; phrasing changes in `LocalLatexSpeaker` → bump `LocalLatexSpeaker.Version`, transcription-prompt changes → bump `TranscriptionPromptVersion`. Play pre-rolls: the coordinator waits for `PreRollSeconds` of ready narration ahead, capped at `PreRollTimeoutSeconds`. Page crops come from PDFium as 3-byte RGB (`RenderCropPngAsync` converts to RGBA); a crop failure is a passage warning, never swallowed.
- **No LaTeX reaches a speech engine.** Model output passes `MathResponseValidator`; on failure or with no verbaliser the segment becomes an explicit "could not be read reliably" announcement, or the labelled `LocalRule` reading.
- **Offsets are UTF-16 and typed** (`TextOffsetUnit`, `TextOffsets`); anchors are single-page with disjoint rectangles and are stamped with `ExtractionIdentity`. `PageRect.UnionAll` is for camera framing only, never highlight geometry.
- **Word boundaries: the text layer proposes, geometry confirms** (ADR-0005). Neither is trusted alone — ink gaps split "2017" at a tabular 1 and hide the space after an "f"; the text layer can hold phantom whitespace inside words. Control characters in page text are not noise: they are unmapped ligature glyphs (`UnmappedGlyphResolver`) or PDFium's line-end hyphen marker (U+0002).
- **Layout roles decide when a model supplied them** (`ExtractionSnapshot.HasSemanticRoles`). The geometric display-maths and running-element heuristics are the fallback for the model-free analyzer, which labels every block Text; running them over a model's Text blocks turned prose lines into "equations". A layout model takes a raster at exactly its `Capabilities.InputSize`. Inline maths never becomes a unit of its own: it flags its sentence (`ReadingUnit.ContainsMaths`). Tables and algorithms are one unit each, planned as a `StructureAnnouncement`.
- **Caching is per stage** (`Domain/Caching.cs`), each key folding in exactly the inputs that affect that stage. When changing output-affecting logic, bump the matching version constant — `ExtractorVersions.Current`, `NarrationVersions.{MathPrompt,LocalMathReader,ProsePipeline}`, or the worker revision in `MfaForcedAligner` — or stale cached snapshots/plans/audio/timings will be reused. Extraction snapshots are cached on disk by `CachingExtractionSource` (Infrastructure); the app extracts through `ServiceRegistry.CachedExtraction` and keeps `Extraction` for rendering and hit-testing.
- **Host services are supplied, not inherited from Core.** Core's `AppConfig` hard-codes `~/.config/railreader2`; this app uses its own XDG directories (`XdgPaths`) and an inert `NullAnnotationStore`. Highlights are transient overlays and must never be written into the PDF.
- **Providers are registered only when implemented and configured**, so the UI never offers an inert selector. API keys are read from environment variables named in settings, never persisted.

### Documentation convention

`docs/status.md` and the README distinguish verified claims (with the measurement) from unverified ones and from deliberate non-features. When behaviour or verification status changes, update those documents in the same terms rather than claiming more than was measured.
