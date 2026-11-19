# ADR-0009: Phonon as a second speech engine

**Status:** accepted · **Date:** 2026-11-19 · **Amends:** ADR-0002 (Kokoro default, ZONOS3 alternative)

## Decision

Rail Narrator gains a third speech engine, **Phonon** (Gradium's `phonon-31m-en`, Apache 2.0). It
runs in process through **Gradium's own C library**, `libptts_ffi.so` from
[xn-ptts](https://github.com/gradium-ai/xn-ptts), called by `PhononTtsProvider` through function
pointers. It runs on the CPU, with q8 weights and 4 threads. Kokoro stays as a second engine, and
ZONOS3 is unchanged.

- **Choosing the engine.** The setting `SpeechProviderId` takes `phonon`, `kokoro`, `zonos3-mini`, or
  `auto`, the new default: Phonon when installed, else Kokoro. A settings file written before this
  change says `kokoro` explicitly, so it keeps Kokoro until the reader chooses Phonon in Settings.
- **Voices.** Each engine keeps its own saved voice (`PhononVoiceId`, `KokoroVoiceId`), so
  switching engines loses neither choice. Phonon's default voice is **Maeve** (British, female),
  chosen by the operator after listening. When a document's saved position names another engine's
  voice, the voice in use is kept.
- **Installing.** Nothing is downloaded or built by the application. `tools/setup-phonon.sh`:
  1. builds the library from the v0.7.1 tag, refusing to build if the tag no longer points at
     commit `5f9522b3`;
  2. downloads the model at the revision that release pins (`92741c5b`);
  3. downloads the 25 British and American English voices of `gradium/phonon-voices` (`da6c9ec0`),
     each file checked against its SHA-256.

  Everything goes to `$XDG_DATA_HOME/rail-narrator/phonon`.
- **Packaging.** `packaging/build-release.sh` builds the library the same way and ships it beside
  the application, which looks there first. The library is MIT OR Apache-2.0 code. The model and
  voices are never bundled. Without `cargo`, the archive goes out without the library and the
  setup script builds it on the reader's machine.

## Context

Kokoro on this project's development laptop (i7-12700H) cannot keep up with playback on the CPU,
and its WebGPU path shares the GPU with the layout model. Gradium released Phonon on 2026-11-17: a
31M-parameter flow-matching model from the Pocket TTS/Mimi lineage, 24 kHz, made for on-device
use. Its card claims better human-preference and word-error scores than Kokoro on English. Those
are its authors' figures and were not re-measured here.

## Evidence

All measured on the i7-12700H over 40 passages of `nbag025.pdf`: 30 prose passages and 10 maths
interpretations, up to 1,713 characters, about 510 s of speech.

| Engine and runtime | Real-time factor | First audio | Passages MFA timed word by word |
|---|---|---|---|
| Phonon, C library from .NET, q8, 4 threads | **0.061** | 32 ms median, 87 ms max | 34 / 40 |
| Phonon, prebuilt CLI | 0.067 | 78 ms median | — |
| Phonon, community ONNX export (ai-ecoverse), Python onnxruntime | 0.122 (q8), 0.135 (fp32) | — | — |
| Kokoro fp16, CPU, `bf_isabella` | 0.224 | whole passage | 35 / 40 |
| Kokoro fp32, WebGPU | 0.258 | whole passage | — |

- **Word highlighting.**
  - Five of the six Phonon failures are shared with Kokoro. All five are words garbled in the
    PDF's text layer ("4b", "f10, 50g", "þttest"), which the aligner cannot pronounce. The sixth
    was "Ttrain/Ttest", another garbled token.
  - Both engines took 9 s in total to align the 40 passages, with a warm aligner.
- **Loading.**
  - The library loads the model in 57 ms; peak RSS for one sentence was 304 MB.
  - The library is 11.6 MB, links only libc, libm and libgcc, and builds in 41–60 s.
- **Threads.** Eight threads were no faster than four (RTF 0.062).
- **Text handling.** The runtime's English normaliser changed only apostrophe style in 1 of 40 of
  the application's already-spoken scripts. It also splits long text into sentences itself.
- **Not deterministic.** Two runs with the same seed and one thread give different bytes,
  although the duration is the same. This does no harm:
  - a clip is synthesised once and cached;
  - its word timing is keyed by its own PCM hash.

  Regenerating a clip gives different audio.
- **GPU builds.** Building with CUDA or Vulkan needs their SDKs; `glslc` was missing here. The
  `webgpu` feature build still chose the CPU. At RTF 0.061 no GPU is needed, and the GPU is left
  to the layout model.

### Checked in the application

On 2026-11-19 the application read `nbag025.pdf` under Xvfb, with isolated XDG directories and
audio sent to a null sink. With `SpeechProviderId` set to `phonon` and a Kokoro voice still in the
copied settings:

- the voice list showed "Maeve (British, female)";
- audio reached the sink 1.3 s after Play;
- MFA timed 9 of the first 11 passages word by word. The other two (the front matter, with a DOI,
  and one more) fell back to whole-passage highlighting;
- the screen showed the word highlight and the camera following on passage 3.

## Alternatives considered

- **The prebuilt CLI, run once per passage.** No build is needed, but it adds a process and about
  70 ms of model loading per passage, plus WAV files on disk. It needs AVX2.
- **The community ONNX export in the existing ONNX Runtime stack.** No native library is needed,
  but it is about twice as slow (0.122). The application would have to supply the autoregressive
  loop, the tokeniser and the text normaliser. It has 37 voices and is maintained by one person.
- **Gradium's OpenAI-compatible server in Docker.** This is a reasonable HTTP provider, but it
  needs Docker and a second process to keep alive.
- **The Python package.** Rejected, because the application has no Python dependency on the
  playback path.

## Consequences

- **Exposure to the library.** It runs in process, so a crash in it ends the application, as with
  Kokoro's ONNX Runtime. The provider makes every call through one gate, on a worker thread. The
  audio callback never lets an exception reach native code. Cancellation returns `false` from the
  callback, and the runtime stops at the next frame.
- **No native timings.** Phonon supplies no word timings and no phonemes. Word highlighting comes
  from MFA as before (ADR-0004). The planner's pronunciation dictionary works on text, so it still
  applies.
- **Cache key.** The cache key's model revision is `phonon/<library sha>/<weights sha>`, so a
  rebuilt library or a new model never reuses old audio.
- **Not verified:** listening at length in the application, prosody across long passages (the
  runtime splits sentences), GPU builds, and the 40m models, which are CC BY-NC and are not used.
