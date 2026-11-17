# Rail Narrator: implementation investigation

This is a proposed Linux-first PDF narration application built on RailReaderCore, with local speech generation and LLM-assisted mathematical narration. The recommendation is to retain the original PDF as the visual authority, create a separate source-linked narration plan, and drive highlighting and camera movement from actual audio playback.

Research date: 17 November 2026. This investigation inspected source code and upstream documentation; it did not benchmark speech quality, run model inference, or build the desktop application.

## Recommendation

- **Desktop stack:** Use C#/.NET 10, Avalonia, and Skia, consuming RailReaderCore through pinned dependencies. This avoids adding a Rust/.NET bridge to an application whose PDF and navigation foundation is already .NET; the repository describes its .NET 10 libraries and Avalonia parent application ([RailReaderCore](https://github.com/sjvrensburg/RailReaderCore)).
- **First speech backend:** Integrate ZONOS3-Mini through its native ggml sidecar, initially using short buffered utterances and built-in voices. Its runtime documents CPU, CUDA and Vulkan paths, but device-specific performance and operator support still need a real-machine smoke test ([native runtime](https://github.com/Zyphra/ZONOS3-Mini/blob/main/servers/ggml/README.md)).
- **Second speech backend:** Keep Audio8-TTS-Realistic-Air as an interchangeable candidate, not a simultaneous requirement for the first working release. It has an Audio8-specific patched llama.cpp runtime with buffered and streaming speech APIs; stock llama.cpp is not a substitute ([Audio8 runbook](https://github.com/Edge0-AI/Audio8_TTS/blob/0f8c4fade0cb14e38fbf737d0488553f16a0d658/guides/llama-cpp.md)).
- **Alignment policy:** Ship trustworthy sentence/equation highlighting before optional word highlighting. A generated equation explanation should remain linked to the equation region, not be falsely matched word-by-word to mathematical glyphs.
- **Math pipeline:** Separate visual transcription from natural spoken rendering, with a user-editable preview and uncertainty handling. Avoid asking the speech model itself to interpret LaTeX.

The companion file, `rail-narrator-coding-agent-prompt.md`, is the self-contained implementation prompt. It specifies the architecture, source mapping, runtime adapters, follow behaviour, tests, and completion criteria.

## What RailReaderCore already supplies

The checkout inspected was commit `e7d41208454dcd4d25ab4a778309f6e483f1c953`, with `VersionPrefix` 0.89.0; these identify the inspected source snapshot, not proof that a particular NuGet version is available ([repository](https://github.com/sjvrensburg/RailReaderCore)).

- **PDF and analysis infrastructure:** Core contains UI-independent models, controllers, reading-order resolvers and navigation; sibling packages provide PDF backends, Skia rendering, ONNX layout analysis, OCR and an OpenAI-compatible VLM client ([repository documentation](https://github.com/sjvrensburg/RailReaderCore)).
- **Geometry for narration anchors:** `PageText`, `CharBox`, `LayoutBlock`, `PageAnalysis`, `WordIndex`, `WordSpan` and `ReadingGuidePosition` supply useful text and location information, while `Viewport` and `DocumentController` provide camera and navigation operations ([inspected source repository](https://github.com/sjvrensburg/RailReaderCore)).
- **Reading order:** The layout pipeline supports model-provided ordering and a column-aware XY-cut resolver; its model-free text analyser produces only text roles, so it cannot be assumed to identify mathematical regions ([layout pipeline](https://github.com/sjvrensburg/RailReaderCore)).
- **Scanned documents:** Full OCR synthesises text and character boxes, but recognition quality depends on raster resolution; the README specifically warns about small text at low analysis resolutions ([OCR documentation](https://github.com/sjvrensburg/RailReaderCore)).
- **Equation transcription:** `IVlmService.DescribeBlockAsync` and `VlmService.BlockAction.LaTeX` offer crop-to-LaTeX transcription, not a complete mathematical narration service ([source repository](https://github.com/sjvrensburg/RailReaderCore)).

Important gaps remain. The inspected source supplies neither the proposed audio-clock-driven narration system nor a semantic mapping between rewritten spoken maths and its original PDF geometry; `WordIndex` treats inline mathematical expressions as glyph runs and is not a mathematical parser ([source repository](https://github.com/sjvrensburg/RailReaderCore)).

Use the existing library rather than flattening a PDF to Markdown and trying to rediscover its locations afterwards. Add narration-specific domain objects in a companion library; only make small, additive Core changes if a genuinely reusable integration seam is missing.

## Speech backend assessment

### ZONOS3-Mini

The model card describes approximately 800 million backbone parameters with about 100 million active per token, 43 languages, and Apache-2.0 weights; “100M active” must not be interpreted as total download size or resident memory ([model card](https://huggingface.co/Zyphra/ZONOS3-Mini)). The native runtime separately requires a waveform decoder and may need voice, encoder or steering assets depending on the selected features ([native runtime](https://github.com/Zyphra/ZONOS3-Mini/blob/main/servers/ggml/README.md)).

Its native interface includes `/v2/health`, `/v2/capabilities`, `/v2/speakers` and `/v2/generate`; the documented PCM format is mono 44.1 kHz float32 little-endian, and WAV output is buffered ([API documentation](https://github.com/Zyphra/ZONOS3-Mini/blob/main/docs/api.md)). The OpenAI-compatible speech route is not interchangeable with every OpenAI client: the documented `speed` field is rejected, so playback speed should be controlled independently unless a supported synthesis-rate capability is explicitly negotiated ([API documentation](https://github.com/Zyphra/ZONOS3-Mini/blob/main/docs/api.md)).

The runtime exposes a FuseAlign-backed `/v2/edit/align` path, but alignment/editing brings Python workers and extra capability requirements; the native runtime documentation also describes edit-backbone and encoder requirements for its editing feature set ([native runtime](https://github.com/Zyphra/ZONOS3-Mini/blob/main/servers/ggml/README.md)). Therefore, do not make this endpoint a mandatory dependency of the basic reader: probe the installed runtime, or use an independently managed aligner, and retain sentence-level highlighting when alignment is unavailable.

Recommendation: first candidate for a native Linux integration, subject to a pinned-runtime smoke test. Do not claim real-time AMD or CPU performance from the model's name, active-parameter count, or vendor GPU benchmarks.

### Audio8-TTS-Realistic-Air

The card describes a 0.6B main model with a separately counted codec, 23 languages, Apache-2.0 licensing, and both reference-free generation and reference-conditioned voice cloning; cloning requires a matching transcript for the reference clip ([model card](https://huggingface.co/Edge0/Audio8-TTS-Realistic-Air)). Its native distribution uses four F32 payloads totalling 3.76 GB and an explicitly linked runtime patch ([fresh model-card text](https://huggingface.co/Edge0/Audio8-TTS-Realistic-Air/raw/main/README.md)).

The pinned runbook documents Linux CPU and CUDA paths, `/v1/audio/speech`, SSE streaming at `/audio8/stream`, and cancellation at `/audio8/cancel/{request_id}`; stream events contain PCM sample offsets, not spoken-word timestamps ([Audio8 runbook](https://github.com/Edge0-AI/Audio8_TTS/blob/0f8c4fade0cb14e38fbf737d0488553f16a0d658/guides/llama-cpp.md)). Vulkan support was not established by that runbook, and its published CPU measurements do not justify assuming real-time narration on an arbitrary laptop ([Audio8 runbook](https://github.com/Edge0-AI/Audio8_TTS/blob/0f8c4fade0cb14e38fbf737d0488553f16a0d658/guides/llama-cpp.md)).

Recommendation: an alternative voice-quality/runtime candidate behind the same adapter boundary. Compare actual academic prose and mathematical narration on the target machine before selecting the default.

### Kokoro ONNX as an optional alternative

`kokoro-onnx` is an MIT-licensed runtime for the Apache-2.0 Kokoro model ([repository](https://github.com/thewh1teagle/kokoro-onnx)). Its duration-enabled example uses `create_timed`, returning audio, sample rate and phoneme timing entries; it explicitly checks `has_timings` because not every model export includes duration outputs ([timed example](https://github.com/thewh1teagle/kokoro-onnx/blob/main/examples/with_video_subtitles.py)).

That example groups phonemes at spaces and pairs them with `text.split()`, which is a demonstration rather than a robust source-text mapping for normalised numbers, abbreviations and equations ([timed example](https://github.com/thewh1teagle/kokoro-onnx/blob/main/examples/with_video_subtitles.py)). It is worth a fallback spike, but it must pass the same mapping and timing tests as the other backends.

## The architectural issue that matters most

There are three distinct representations: PDF source text and geometry, the narration script, and the audio timeline. Keep explicit mappings between them rather than assuming they share words, positions or lengths.

For ordinary prose, the script can largely preserve the source wording. For `\bar{x} = \frac{1}{n}\sum_{i=1}^{n}x_i`, a suitable spoken rendering is “X bar is the arithmetic mean of the n observations: add x sub i from i equals one to n, then divide by n.” The entire equation remains highlighted while that utterance plays; it is misleading to pretend that each spoken word corresponds to one original glyph.

For high-magnification reading, an aligned word can guide the camera along a line. Without word timing, keep the whole spoken sentence visible where possible, or use separately synthesised line/phrase units if the reader explicitly chooses high-zoom line tracking; do not silently invent word positions from character counts.

The minimal reliable path is:

```text
RailReaderCore page analysis and text
  -> source anchors and reading units
  -> source-linked narration script
  -> short audio clips with actual sample counts
  -> optional forced/native alignment
  -> playback media clock
  -> PDF highlights and follow camera
```

## Risks and decisions to validate

- **Mathematical fidelity:** A fluent narration can still change a bound, sign, quantifier or interpretation. Preserve the crop and transcription, distinguish faithful rendering from optional explanation, and make corrections possible.
- **Inline maths:** Do not assume every expression receives a separate layout box. Combine available layout roles, text cues and optional visual verification, with a manual “Treat as maths” override.
- **Mixed/poor text layers:** A PDF may contain some valid text and some scanned or corrupted content. Detect coverage problems instead of assuming that any text layer makes a page narratable.
- **Timing:** Audio delivery chunks, model attention and stream sample offsets are not necessarily word boundaries. Report alignment provenance and degrade gracefully when evidence is insufficient.
- **Resource contention:** OCR, vision inference, LLM inference and TTS can compete for the same memory and GPU. Use bounded preparation queues and record real peak RAM/VRAM, first-audio latency and preparation time.
- **Accessibility:** Preserve chosen magnification, allow reduced motion and temporary manual browsing, and make follow mode resumable. Do not continually override the user's camera.
- **Packaging:** Pin source and model revisions, download only selected assets, and disclose native/optional Python dependencies. Verify licences for code, weights, voice assets and transitive binaries separately.

No winning voice, latency target or hardware compatibility has been empirically established in this investigation. Those are explicit implementation-stage gates, not promises hidden in the prompt.
