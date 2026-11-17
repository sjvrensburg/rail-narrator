# Rail Narrator

A Linux desktop application that reads a PDF aloud, highlights the passage being
spoken in the original document, and follows the reading position — including
mathematics, which is rendered as faithful spoken English rather than handed to a
speech engine as raw LaTeX.

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

# The model is NOT bundled and NOT fetched implicitly. See docs/model-manifest.json.
mkdir -p ~/.local/share/rail-narrator/models
curl -L -o ~/.local/share/rail-narrator/models/kokoro-fp16.onnx \
  https://github.com/Lyrcaxis/KokoroSharpBinaries/releases/download/v2.0.0/kokoro-fp16.onnx

# Run (a PDF on the command line is loaded, never auto-played)
dotnet run --project src/RailNarrator.App -c Release -- path/to/document.pdf
```

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
| `RailNarrator.Pdf` | RailReaderCore adapter: extraction snapshots, coordinate transforms, hit-testing, overlays | Core, SkiaSharp |
| `RailNarrator.Speech` | Kokoro and ZONOS3 adapters, local MFA word alignment, PCM/WAV codecs | Domain, KokoroSharp |
| `RailNarrator.Audio` | Output device, resampling, pitch-preserving speed, media clock | Domain |
| `RailNarrator.Inference` | Maths transcription, verbalisation, closed-schema validation, local rule reader | Domain, Core.Vlm |
| `RailNarrator.Infrastructure` | XDG settings, caches, model manifest and downloads, sidecar lifecycle | Domain |
| `RailNarrator.App` | Avalonia views, commands, Skia-backed PDF surface | all of the above |

`RailNarrator.Domain` has no dependencies. Its tests run without a GPU, network,
model or sound card.

## Design decisions

Recorded in `docs/decisions/`, each with the measurement that drove it:

- **[ADR-0001](docs/decisions/ADR-0001-stack-and-core-pin.md)** — stack, and why RailReaderCore is pinned to published 0.89.0 packages
- **[ADR-0002](docs/decisions/ADR-0002-speech-provider.md)** — Kokoro default, ZONOS3 alternative; both measured, neither inert
- **[ADR-0003](docs/decisions/ADR-0003-audio-backend.md)** — PortAudio callback DAC timestamps and the presented-frame clock
- **[ADR-0004](docs/decisions/ADR-0004-alignment-policy.md)** — local word alignment with validated sentence/equation fallback
- **[ADR-0005](docs/decisions/ADR-0005-geometry-and-word-spacing.md)** — coordinate frames, and why word boundaries need both the text layer and the geometry

## Guarantees worth stating plainly

- **No LaTeX reaches a speech engine.** With no verbaliser configured, or one whose
  output fails validation, the segment becomes an explicit "this equation could not
  be read reliably" announcement. Tested.
- **No invented word timings.** Without timing evidence the highlight covers the
  whole sentence or equation and the diagnostics say so. Interpolating by character
  count would look measured and be wrong.
- **Highlights are never written into your PDF.** They are transient overlays.
- **Your settings are not shared with RailReader2.** Core's `AppConfig` hard-codes
  `~/.config/railreader2` and creates it on access; this app uses its own XDG
  directories and an inert annotation store.
- **Nothing is uploaded without an explicitly configured remote endpoint**, and API
  keys are read from environment variables, never persisted.
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

## Known limitations

See `docs/status.md` for the full list. The headline items:

- Word highlighting needs the optional local alignment runtime and English models.
  Missing dictionary words or invalid intervals trigger sentence highlighting.
- Acoustic word-boundary error and audible-to-visible latency still need corpus
  and loopback measurements; no millisecond accuracy guarantee is claimed.
- Real audible playback and the full GUI interaction loop have not been verified by
  a human; the audio path is verified by measurement and by a controllable fake device.
- Scanned-document OCR is wired and wired to real PP-OCRv5 models, but has not been
  exercised against a real scan.
- Continuous-scroll mode is not implemented; the surface shows one page at a time,
  though `PageTransform` already carries the page-offset terms it would need.
