# Rail Narrator

A Linux desktop application that reads a PDF aloud, highlights the passage being
spoken in the original document, and follows the reading position — including
mathematics: a language model you point it at (any OpenAI-compatible endpoint, local
by default, or Claude) looks at each equation and each sentence with maths in it, and the
narrator says what it *means* in the paper's own terms instead of reading notation.
An optional formula model (PP-FormulaNet_plus-M, about 0.6 GB) transcribes display equations
to LaTeX on your own computer in about a second each: the language model can be given that
LaTeX with the page image or in place of it, and with no language model at all the equations
are read by rule from it.
With a layout model installed it also skips what a first reading skips: page
furniture, footnotes, references, figures and table cells.

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

## Status

Implemented, built and tested. **the regression suite passes**, the whole solution
builds with **0 warnings**, and the end-to-end path has been run for real:

```
real PDF ─► extraction (165 ms/page) ─► plan ─► Kokoro synthesis
           3 clips, 12.49 s of audio in 1.34 s wall  (aggregate RTF 0.11)
           peak 0.35–0.38, verified with ffprobe: pcm_f32le 24000 Hz mono
```

`docs/status.md` states precisely what is verified, what is implemented but not
yet verified on real hardware, and what is deliberately not implemented. Read it
before trusting any claim here.

## Quick start

```bash
# Build and test
dotnet build RailNarrator.slnx -c Release
dotnet test RailNarrator.slnx -c Release

# A speech engine. Models are NOT bundled and NOT fetched implicitly.
# Phonon (preferred when installed; needs git and a Rust toolchain; ADR-0009):
tools/setup-phonon.sh
# and/or Kokoro (see docs/model-manifest.json):
mkdir -p ~/.local/share/rail-narrator/models
curl -L -o ~/.local/share/rail-narrator/models/kokoro-fp16.onnx \
  https://github.com/Lyrcaxis/KokoroSharpBinaries/releases/download/v2.0.0/kokoro-fp16.onnx

# Run (a PDF on the command line is loaded, never auto-played)
dotnet run --project src/RailNarrator.App -c Release -- path/to/document.pdf
```

To narrate a whole document once and keep the result with it, use File ▸ Save narration with
document…, or without a window:

```bash
rail-narrator narrate paper.pdf            # writes paper.narration beside the PDF
rail-narrator export paper.pdf             # writes paper.m4b, an audiobook with chapters (needs ffmpeg)
```

A document with a `.narration` beside it is played from that file: the maths is already
interpreted and every passage already spoken, so no language model and no speech engine is used
([ADR-0012](docs/decisions/ADR-0012-saved-narration.md)).

For word-by-word English prose highlighting, run `bash tools/setup-word-alignment.sh`
after installing micromamba. See [setup and timing verification](docs/word-highlighting.md).

Native dependency: `libportaudio.so.2` (Fedora `portaudio`, Debian/Ubuntu
`libportaudio2`). Everything else — PDFium, Skia, ONNX Runtime, PP-OCRv5 OCR
models, Kokoro voices — is bundled by the NuGet packages.

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

- **[ADR-0001](docs/decisions/ADR-0001-stack-and-core-pin.md)** — stack, and why RailReaderCore is pinned to published packages (0.94.0)
- **[ADR-0002](docs/decisions/ADR-0002-speech-provider.md)** — Kokoro default, ZONOS3 alternative; both measured, neither inert
- **[ADR-0003](docs/decisions/ADR-0003-audio-backend.md)** — PortAudio callback DAC timestamps and the presented-frame clock
- **[ADR-0004](docs/decisions/ADR-0004-alignment-policy.md)** — local word alignment with validated sentence/equation fallback
- **[ADR-0005](docs/decisions/ADR-0005-geometry-and-word-spacing.md)** — coordinate frames, and why word boundaries need both the text layer and the geometry
- **[ADR-0006](docs/decisions/ADR-0006-layout-on-the-gpu.md)** — layout analysis on the GPU when one is usable (about twice as fast on a 24-page chapter), the CPU otherwise
- **[ADR-0007](docs/decisions/ADR-0007-kokoro-on-the-gpu.md)** — Kokoro on the GPU through WebGPU (Vulkan), never CUDA or ROCm; fp32 with int32 tokens, every clip checked, CPU fallback
- **[ADR-0008](docs/decisions/ADR-0008-heron-layout-model.md)** — Docling Heron as the layout model (FP32 on the GPU, INT8 on the CPU, XY-cut++ order), references found by their heading; 1.5–2.4× faster than PP-DocLayoutV3
- **[ADR-0009](docs/decisions/ADR-0009-phonon-speech-engine.md)** — Phonon (Gradium, Apache 2.0) as a second speech engine through Gradium's C library: real-time factor 0.061 against Kokoro's 0.224 on the same CPU, word alignment 34/40 passages against 35/40; Maeve by default
- **[ADR-0010](docs/decisions/ADR-0010-claude-for-maths.md)** — Claude as an option for both maths endpoints, beside any OpenAI-compatible one: Core's client for the vision model, Anthropic's .NET SDK for interpretation; tested against a stubbed transport and, on one drawn equation, the real API
- **[ADR-0011](docs/decisions/ADR-0011-formula-model.md)** — PP-FormulaNet_plus-M as a formula model for display equations, in-process on the CPU: its LaTeX given to the language model with or in place of the page image, read by rule when there is no interpretation, and checked against the page's text layer; 80 equations of one paper in 72 s, 15 of them compared with the page
- **[ADR-0012](docs/decisions/ADR-0012-saved-narration.md)** — a whole document narrated once to a file beside the PDF (script, Opus audio, timings) and played from it with no model and no engine; an M4B audiobook with chapters from it
- **[ADR-0013](docs/decisions/ADR-0013-engine-word-timing.md)** — word timing from the speech engine itself, beside the aligner: Phonon from the attention head that follows its text (30 ms mean from the aligner), Kokoro from its phoneme durations (16 ms)
- **[ADR-0014](docs/decisions/ADR-0014-review-annotations.md)** — review annotations: pause, note what was just read, carry on; RailReader2's tools through Core's model, kept by content hash beside (never in) the PDF, exported as Markdown, an annotated copy or RailReader2 JSON, and carried to a revised PDF by the words they are on

## Guarantees worth stating plainly

- **No notation reaches a speech engine.** A model's interpretation is rejected if it
  still carries symbols, Greek letters or variable names ("h t"), gets one repair
  turn, and is then refused. The model is then asked only to transcribe the passage
  from the page image, and a rule-based reader says that notation aloud in words
  ("h sub t equals alpha sub zero plus the sum from i equals one to q of …"),
  labelled as saying the notation rather than its meaning. If that fails too, a
  sentence is read as printed and an equation becomes an explicit "this equation
  could not be read reliably" announcement. Tested.
- **A short wait before the first word.** Play waits up to 20 s for about a minute
  of narration to be ready, so maths interpretation stays ahead of the voice.
- **No invented word timings.** Without timing evidence the highlight covers the
  whole sentence or equation and the diagnostics say so. Interpolating by character
  count would look measured and be wrong.
- **Highlights are never written into your PDF.** They are transient overlays.
- **Your notes are never written into your PDF either.** They are kept in the data
  directory by the PDF's content hash; Review ▸ Export writes an annotated *copy*.
- **Your settings are not shared with RailReader2.** Core's `AppConfig` hard-codes
  `~/.config/railreader2` and creates it on access; this app uses its own XDG
  directories and an inert annotation store.
- **Nothing is uploaded without an explicitly configured remote endpoint.** Choosing
  Claude in Settings is such a configuration: each passage with maths then goes to
  Anthropic as a page image with the text around it, and the dialogue says so. With the
  formula model installed and "the formula model's LaTeX only" chosen, no page image is sent:
  a display equation goes as LaTeX, and every passage as its text. An API key
  is either read from an environment variable you name, or typed into Settings and
  stored in `settings.json`, which is then written readable by you only (mode 0600);
  for Claude, a key given neither way is left to Anthropic's SDK to find
  (`ANTHROPIC_API_KEY`).
- **A document is loaded, never auto-played.** Including on restore.

## Release

```bash
./packaging/build-release.sh          # self-contained linux-x64, ~138 MB archive
```

Produces an FHS-shaped staging tree (`usr/bin`, `usr/lib/rail-narrator`,
`usr/share/applications`), a relocatable launcher, `rail-narrator.desktop` with
`MimeType=application/pdf`, and `NATIVE-DEPENDENCIES.txt`. Verified: the published
binary launches under X11 and loads a PDF with no exceptions. Models are excluded
by design. An AppImage is deliberately not built yet — the plain archive has not
been through a clean-machine test.

Windows (x64) is in progress and **not yet run on Windows**:

```bash
./packaging/build-windows.sh          # self-contained win-x64 folder and zip
```

On Windows (Git Bash, with CMake, MSVC and optionally cargo) it also builds PortAudio
v19.7.0 from a pinned commit and Phonon's `ptts_ffi.dll`; on Linux it cross-publishes the
.NET part only. The folder holds `rail-narrator.exe` and `rail-narrator-cli.exe`, the
console program for `narrate` and `export`, which a GUI executable cannot serve on
Windows. Settings go to `%APPDATA%\rail-narrator`, models and caches to
`%LOCALAPPDATA%\rail-narrator`. `.github/workflows/windows.yml` builds, tests and packages
it on a Windows runner. ffmpeg is never bundled on any platform: audiobook export runs the
ffmpeg the reader installed (`winget install ffmpeg` on Windows), so its licence binds
no one here. Word alignment (MFA) is Linux-only.

Both archives carry Kokoro's voices (`voices/`, 54 voices, 27 MB, Apache-2.0); the
Kokoro model is downloaded on approval as before. Before this, a release had no voices
and Kokoro found them only in a developer's NuGet cache.

## Known limitations

See `docs/status.md` for the full list. The headline items:

- Word highlighting needs the optional local alignment runtime and English models.
  Words missing from the dictionary get a generated pronunciation (acronyms are spelled
  out). A word that cannot be pronounced (letter–digit tokens such as "ggplot2") or an
  invalid interval triggers sentence highlighting for that sentence, with the reason.
- Acoustic word-boundary error and audible-to-visible latency still need corpus
  and loopback measurements; no millisecond accuracy guarantee is claimed.
- Real audible playback and the full GUI interaction loop have not been verified by
  a human; the audio path is verified by measurement and by a controllable fake device.
- Scanned-document OCR is wired and wired to real PP-OCRv5 models, but has not been
  exercised against a real scan.
- Continuous-scroll mode is not implemented; the surface shows one page at a time,
  though `PageTransform` already carries the page-offset terms it would need.
