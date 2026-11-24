# Developing Rail Narrator

How the project is built, tested, laid out and released. For what Rail Narrator does and how to
install it, see the [README](../README.md). What is verified, and how it was measured, is in
[status.md](status.md); the design is in [architecture.md](architecture.md) and the decisions,
each with its measurement, in [decisions/](decisions/). [CLAUDE.md](../CLAUDE.md) is the working
guide for coding agents and is as useful to a person.

Built on [RailReaderCore](https://github.com/sjvrensburg/RailReaderCore) for PDF
rendering, layout, reading order and OCR. Nothing in Core's PDF stack is replaced.

```
PDF ──► RailReaderCore extraction ──► source anchors + reading units
     ──► narration plan (source-linked script)
     ──► TTS clips with known sample counts
     ──► playback media clock (frames the device actually presented)
     ──► highlights + follow camera
```

The two mappings that matter are kept separate, because conflating them is how a
reader confidently highlights the wrong text:

```
audio sample position ──► spoken script range
spoken script range   ──► original PDF source anchors
```

## Build, test and run from source

Needs the .NET 10 SDK (pinned by `global.json`) and `libportaudio.so.2`.

```bash
dotnet build RailNarrator.slnx -c Release
dotnet test RailNarrator.slnx -c Release          # needs no GPU, network, model or sound card

# A speech engine. Models are NOT bundled and NOT fetched implicitly.
# Phonon (preferred when installed; builds its library, so needs git and a Rust toolchain; ADR-0009):
tools/setup-phonon.sh
# and/or Kokoro (or Models and endpoints ▸ download Kokoro in the app; see docs/model-manifest.json):
mkdir -p ~/.local/share/rail-narrator/models
curl -L -o ~/.local/share/rail-narrator/models/kokoro-fp16.onnx \
  https://github.com/Lyrcaxis/KokoroSharpBinaries/releases/download/v2.0.0/kokoro-fp16.onnx

# Run (a PDF on the command line is loaded, never auto-played)
dotnet run --project src/RailNarrator.App -c Release -- path/to/document.pdf
```

For word-by-word highlighting measured by a forced aligner, run `bash tools/setup-word-alignment.sh`
after installing micromamba. See [setup and timing verification](word-highlighting.md).

## Headless verification harness

`rail-narrator-smoke` supplies headless extraction and synthesis checks; tests also
cover alignment and native audio playback. It needs
no display and no sound card for most commands.

```bash
dotnet build tools/RailNarrator.Smoke -c Release
S=tools/RailNarrator.Smoke/bin/Release/net10.0/rail-narrator-smoke

$S fixture twocol /tmp/twocol.pdf    # write a synthetic PDF with known geometry
$S extract /tmp/twocol.pdf           # dump coverage, blocks, lines, char boxes
$S plan    /tmp/twocol.pdf           # extract + plan; dump the spoken script and links
$S audio                             # enumerate devices, measure the media clock
$S providers --kokoro-model ~/.local/share/rail-narrator/models/kokoro-fp16.onnx
$S speak /tmp/twocol.pdf --provider kokoro --model <path> --out /tmp/wav
$S all --kokoro-model <path>         # whole chain, printed pass/fail summary
```

## Layout

| Project | Responsibility | Depends on |
|---|---|---|
| `RailNarrator.Domain` | Anchors, reading units, plans, source maps, cue scheduling, playback state, follow policy, caching keys | *nothing* |
| `RailNarrator.Pdf` | RailReaderCore adapter: extraction snapshots, page rasters rendered off the UI thread, coordinate transforms, hit-testing, overlays | Core, SkiaSharp |
| `RailNarrator.Speech` | Phonon (Gradium's C library), Kokoro and ZONOS3 adapters, local MFA word alignment, PCM/WAV codecs | Domain, KokoroSharp |
| `RailNarrator.Audio` | Output device, resampling, pitch-preserving speed, media clock | Domain |
| `RailNarrator.Inference` | Maths interpretation (page image + context → words), transcription, verbalisation, validation, local rule reader; over an OpenAI-compatible endpoint or Claude; the formula model in-process | Domain, Core.Vlm, Anthropic SDK, ONNX Runtime, SkiaSharp |
| `RailNarrator.Infrastructure` | XDG settings, caches, model manifest and downloads, sidecar lifecycle | Domain |
| `RailNarrator.App` | Avalonia views, commands, Skia-backed PDF surface | all of the above |

`RailNarrator.Domain` has no dependencies. Its tests run without a GPU, network,
model or sound card.

## Design decisions

Recorded in `docs/decisions/`, each with the measurement that drove it:

- **[ADR-0001](decisions/ADR-0001-stack-and-core-pin.md)** — stack, and why RailReaderCore is pinned to published packages (0.94.0)
- **[ADR-0002](decisions/ADR-0002-speech-provider.md)** — Kokoro default, ZONOS3 alternative; both measured, neither inert
- **[ADR-0003](decisions/ADR-0003-audio-backend.md)** — PortAudio callback DAC timestamps and the presented-frame clock
- **[ADR-0004](decisions/ADR-0004-alignment-policy.md)** — local word alignment with validated sentence/equation fallback
- **[ADR-0005](decisions/ADR-0005-geometry-and-word-spacing.md)** — coordinate frames, and why word boundaries need both the text layer and the geometry
- **[ADR-0006](decisions/ADR-0006-layout-on-the-gpu.md)** — layout analysis on the GPU when one is usable (about twice as fast on a 24-page chapter), the CPU otherwise
- **[ADR-0007](decisions/ADR-0007-kokoro-on-the-gpu.md)** — Kokoro on the GPU through WebGPU (Vulkan), never CUDA or ROCm; fp32 with int32 tokens, every clip checked, CPU fallback
- **[ADR-0008](decisions/ADR-0008-heron-layout-model.md)** — Docling Heron as the layout model (FP32 on the GPU, INT8 on the CPU, XY-cut++ order), references found by their heading; 1.5–2.4× faster than PP-DocLayoutV3
- **[ADR-0009](decisions/ADR-0009-phonon-speech-engine.md)** — Phonon (Gradium, Apache 2.0) as a second speech engine through Gradium's C library: real-time factor 0.061 against Kokoro's 0.224 on the same CPU, word alignment 34/40 passages against 35/40; Maeve by default
- **[ADR-0010](decisions/ADR-0010-claude-for-maths.md)** — Claude as an option for both maths endpoints, beside any OpenAI-compatible one: Core's client for the vision model, Anthropic's .NET SDK for interpretation; tested against a stubbed transport and, on one drawn equation, the real API
- **[ADR-0011](decisions/ADR-0011-formula-model.md)** — PP-FormulaNet_plus-M as a formula model for display equations, in-process on the CPU: its LaTeX given to the language model with or in place of the page image, read by rule when there is no interpretation, and checked against the page's text layer; 80 equations of one paper in 72 s, 15 of them compared with the page
- **[ADR-0012](decisions/ADR-0012-saved-narration.md)** — a whole document narrated once to a file beside the PDF (script, Opus audio, timings) and played from it with no model and no engine; an M4B audiobook with chapters from it
- **[ADR-0013](decisions/ADR-0013-engine-word-timing.md)** — word timing from the speech engine itself, beside the aligner: Phonon from the attention head that follows its text (30 ms mean from the aligner), Kokoro from its phoneme durations (16 ms)
- **[ADR-0014](decisions/ADR-0014-review-annotations.md)** — review annotations: pause, note what was just read, carry on; RailReader2's tools through Core's model, kept by content hash beside (never in) the PDF, exported as Markdown, an annotated copy or RailReader2 JSON, and carried to a revised PDF by the words they are on

## Building a release

Releases are built by `.github/workflows/release.yml` when a `v*` tag is pushed: the Linux
archive and AppImage on Ubuntu 22.04 (so the bundled native libraries need no newer glibc than
that; `packaging/check-glibc.sh` enforces 2.35), the Windows zip and installer on Windows, all with
Phonon's runtime, and a GitHub release with them and their SHA-256 sums. The notes come from
`docs/releases/<tag>.md`, which must exist. Before tagging, bump `VersionPrefix` in
`Directory.Build.props` and add a `<release>` to
`packaging/io.github.sjvrensburg.RailNarrator.metainfo.xml`. `.github/workflows/packages.yml`
builds the same packages for every pull request and tries them: the AppImage is run, and the
installer is installed, run and uninstalled.

Locally:

```bash
./packaging/build-release.sh          # self-contained linux-x64 archive (COMPRESS=gz for .tar.gz)
./packaging/build-windows.sh          # self-contained win-x64 folder and zip (and the installer, with Inno Setup)
./packaging/build-appimage.sh         # the AppImage, from build-release.sh's staging tree
```

The Linux archive is an FHS-shaped tree (`usr/bin`, `usr/lib/rail-narrator`,
`usr/share/applications`) with a relocatable launcher, `rail-narrator.desktop` with
`MimeType=application/pdf`, and `NATIVE-DEPENDENCIES.txt`. On Windows (Git Bash, with CMake, MSVC
and cargo) `build-windows.sh` also builds PortAudio v19.7.0 from a pinned commit and Phonon's
`ptts_ffi.dll`; on Linux it cross-publishes the .NET part only. The Windows folder holds
`rail-narrator.exe` and `rail-narrator-cli.exe`, the console program for `narrate` and `export`,
which a GUI executable cannot serve on Windows. `.github/workflows/windows.yml` builds, tests and
packages on a Windows runner for every pull request.

Both carry Kokoro's 54 voices (27 MB, Apache-2.0), Phonon's runtime, `install-phonon.sh` (the
model and voices only, from `tools/setup-phonon.sh` with `PHONON_MODEL_ONLY=1`), the licence and
`THIRD-PARTY-NOTICES.txt`. Models are never included. ffmpeg is never bundled on any platform:
audiobook export runs the ffmpeg the reader installed, so its licence binds no one here.

## Known limitations (technical)

See `docs/status.md` for the full list. The headline items:

- Word-by-word highlighting comes from the speech engine's own timing (Phonon through the
  patched library, or Kokoro's timed model; ADR-0013) or from the optional local aligner, which
  replaces it when installed. Without either the highlight covers the whole sentence. With the
  aligner, words missing from its dictionary get a generated pronunciation (acronyms are spelled
  out); a word that cannot be pronounced (letter–digit tokens such as "ggplot2") or an invalid
  interval gives sentence highlighting for that sentence, with the reason.
- Acoustic word-boundary error and audible-to-visible latency still need corpus
  and loopback measurements; no millisecond accuracy guarantee is claimed.
- Real audible playback and the full GUI interaction loop have not been verified by
  a human; the audio path is verified by measurement and by a controllable fake device.
- Scanned-document OCR is wired and wired to real PP-OCRv5 models, but has not been
  exercised against a real scan.
- Continuous-scroll mode is not implemented; the surface shows one page at a time,
  though `PageTransform` already carries the page-offset terms it would need.
