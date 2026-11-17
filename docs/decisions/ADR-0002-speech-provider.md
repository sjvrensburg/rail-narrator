# ADR-0002: Kokoro in-process as the default provider, ZONOS3 sidecar as a working alternative

**Status:** accepted · **Date:** 2026-11-17
**Supersedes:** the brief's "ZONOS3-Mini first" ordering, by operator instruction.

## Decision

Implement **two working providers** behind `ITtsProvider`, and default to Kokoro:

| | Kokoro (KokoroSharp, in-process) | ZONOS3-Mini (ggml sidecar, HTTP `/v2`) |
|---|---|---|
| Cold start | **398 ms** | ~3 s per CLI invocation; amortised by a persistent server |
| Synthesis RTF (measured) | **0.11–0.20** | 0.58 (server, model warm); 0.62 documented for the CLI |
| Output | 24 kHz mono float32 | 44.1 kHz mono float32 (`/v2/generate`, `format: pcm`) |
| Assets | 163.6 MB model + 61 voices in the NuGet package | 3.2 GB of GGUFs plus a C++ build |
| Word timings | none (this export has no `durations` output) | none (`/v2/edit/align` needs assets this install lacks) |
| Dependencies | NuGet only | external binary, managed as a sidecar |

No inert provider selector is shipped. `SpeechProviderIds` lists exactly what is
implemented; the ZONOS3 *streaming* path and the Audio8 patched-llama.cpp
protocol are absent rather than greyed out, because a picker implying an
unimplemented backend works wastes the reader's time.

## Context

The operator's instruction was to drop ZONOS3 as primary and use Kokoro-ONNX.
Both were then proven by real synthesis before either was chosen as default.

`kokoro-onnx` (Python) and the `KokoroOnnx` .NET port were both rejected: the
former needs a Python sidecar for no benefit over in-process inference, and the
latter pulls in **NAudio and DirectML**, which are Windows-only — the specific
failure the brief warns against. `KokoroSharp` 0.8.4 is MIT, pure C#,
cross-platform, and ships a `GPU.Linux` runtime package.

## Evidence

Both providers were exercised against real text, not mocked:

- Kokoro, through the application's own provider adapter on a generated PDF:
  3 clips, 12.49 s of audio in 1.34 s wall (**aggregate RTF 0.11**), peak
  0.35–0.38, 157 voices discovered. Output WAVs verified independently with
  `ffprobe`: `pcm_f32le`, 24000 Hz, mono, durations matching to the millisecond.
- ZONOS3 `zonos3-server` on `127.0.0.1:19191`: 4.333 s of audio in 2.516 s
  (**RTF 0.58**), peak 0.5117. Response headers carried the format exactly —
  `X-Audio-Sample-Rate: 44100`, `X-Audio-Format: float32`, `X-Audio-Channels: 1`,
  `X-Decoder: full_mr`, `X-Seed`.

KokoroSharp's own playback path (NAudio on Windows, OpenAL elsewhere) is never
called; playback goes through `RailNarrator.Audio` so the media clock stays under
this application's control. Those assemblies arrive transitively and stay
unloaded.

## ZONOS3 settings, carried over from measured prior art

`~/Projects/halo-prep` documents an operator-tuned ZONOS3 narration pipeline on
this same machine. Two settings are adopted verbatim because they were measured,
not guessed:

- `speaker_cfg_scale: 1.5` — without it an audible accent and delivery shift
  appears at paragraph joins; speaker similarity drops from 0.92–0.94 to 0.89–0.92.
- `condition_values: {"loudness": -18}` — without a loudness condition,
  paragraphs came out up to 12 dB apart and faded within themselves (loudness
  range 16.5 LU); with it, about 3 LU.

Also inherited: **CPU only**. The Radeon 8060S registers as an IGPU, so
`ggml_backend_dev_is_gpu` is false and the Vulkan backend silently falls back to
CPU; and when the backend is patched to force Vulkan, the decoder outputs
silence. `Rail Narrator` therefore does not attempt GPU decode, and the
coordinator rejects silent audio by peak amplitude rather than playing it.

## Consequences

- Default path needs no child process, no HTTP server and one 164 MB file.
- RTF 0.11 gives roughly 9× headroom over real time, so bounded lookahead keeps
  up with playback comfortably; that headroom is what makes a 4-segment lookahead
  safe rather than aspirational.
- Neither provider gives word timings, so **clip-boundary (sentence/equation)
  highlighting is the shipping path**. See ADR-0004.
