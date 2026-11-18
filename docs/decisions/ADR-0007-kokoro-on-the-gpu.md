# ADR-0007: Kokoro on the GPU through WebGPU

**Status:** accepted · **Date:** 2026-11-18

## Decision

Kokoro runs on a WebGPU-capable GPU when the converted model is installed, a device is found and a
warm-up clip comes out as real audio, and on the CPU otherwise. The setting is
`KokoroAcceleration`, default `Auto`; `Cpu` turns the GPU off. The GPU path uses the same ONNX
Runtime WebGPU plugin as layout analysis (ADR-0006), through RailReaderCore's
`WebGpuAccelerator.TryBuildSessionHook()`, so it is vendor-neutral (Vulkan on Linux) and shares
the process's one GPU binding with the layout model. CUDA and ROCm were ruled out by the operator:
they tie the application to one vendor.

The model is the fp32 export from the KokoroSharpBinaries release, with its `tokens` input rewritten
to int32 by `tools/kokoro-webgpu-model.sh`. Nothing is downloaded or converted implicitly.

## Context

On the development laptop (i7-12700H, 6 performance and 8 efficiency cores, 31 GB, Ubuntu 24.04,
on mains power with the performance profile) Kokoro could not keep up with playback: reading
stopped with "Synthesising speech" every few passages. The previous reference machine (Ryzen AI
MAX+ 395) measured RTF 0.11, so the plan assumed roughly ninefold headroom that this machine does
not have.

Where the time goes, measured with KokoroSharp's own tokeniser and segmentation and a bare ONNX
Runtime session on three sentences of a synthetic document:

| Configuration | RTF (wall time ÷ audio time) |
|---|---|
| fp16, KokoroSharp's engine, the provider's default 8 threads | 1.05–1.7 between runs |
| fp16, 1 thread / 2 / 4 / 6 / 8 / 14 threads (provider path) | 1.43 / 0.98 / 0.94 / 0.94 / 1.05 / 1.23 |
| fp32 export, CPU | 1.31 (no faster than fp16: 1.18 in the same run) |
| int8, MatMul only (made with ORT's dynamic quantiser) | 0.72 |
| int8 including convolutions | 1.5 (ConvInteger is slower) |
| two or three sentences in parallel, 3 or 2 threads each | 0.75–0.77 aggregate |
| ONNX Runtime 1.22 (KokoroSharp's own) vs 1.31.1 (the application's) | 1.88 vs 1.81, the same |

Phonemisation costs 1–4 ms once warm and every test sentence was one segment, so the time is the
model's own arithmetic, and this CPU tops out around RTF 0.75 whatever the arrangement. CPU timings
on this laptop also vary by a factor of two with background load (idle .NET build servers, as
ADR-0006 found), so CPU-only narration here has no margin.

On the same laptop's NVIDIA RTX A2000 8 GB, through WebGPU over Vulkan:

| Model on the GPU | Result |
|---|---|
| fp16 or fp32 as exported (int64 `tokens`) | the plugin aborts the process on the first run: `OrtDevice … Invalid memory type: -1` (the int64 failure RailReaderCore documents for EP 0.5.0) |
| fp16 with int32 `tokens` | RTF 0.14, but the samples are **NaN** |
| **fp32 with int32 `tokens`** | **RTF 0.27–0.32** once warm; same length, peak and loudness as the CPU, loudness-envelope correlation 1.000, sample RMS difference 0.0047 against a signal RMS of 0.055 |

Through the application's provider (smoke tool, six clips): RTF 0.30–0.32 after the first two clips
(0.77 and 0.41 while shapes compile), aggregate 0.40, against 1.72 on the CPU in the same session.
In the window, with the layout model also on the GPU, a four-page document read all 16 passages to
the end with no "Synthesising speech" wait (sampled every 4 s); on the CPU the same document waited
every few passages. Opening cost about 6 s more to the first audio than the CPU (80 s against 74 s
in that environment, where the 47 s before Kokoro starts is the layout model's and the same either
way).

## How it is wired

- `KokoroTtsProvider(settings, KokoroGpuOptions? gpu)`. The composition root builds the options
  only with `KokoroAcceleration` Auto, the converted model present and a WebGPU device found
  (otherwise the CPU, as before, with a start-up note when only the device is missing).
- `KokoroSessionRunner` runs the graph in its own ONNX Runtime session, building the inputs exactly
  as KokoroSharp's `KokoroModel.Infer` does (pad with 0 at both ends, unknown tokens to 4, style
  row `T - 1`, truncate to 510). KokoroSharp's engine cannot be used on the GPU: it always feeds
  int64 tokens. The CPU path is still KokoroSharp's engine (`KokoroEngineRunner`), unchanged.
- `KokoroRunnerChain` checks every GPU clip (`KokoroOutputCheck`: finite, not silent). A bad clip,
  or an exception from the GPU session, moves synthesis to the CPU for the rest of the run, says
  why, and redoes that request on the CPU, so the bad clip is never heard. Initialisation runs a
  warm-up clip through the same check, which also compiles the shaders (several seconds) before
  the first real passage.
- The model revision in the speech-cache key names the device: CPU audio keeps exactly the revision
  it had before this change (cached clips stay valid); GPU audio is
  `kokoro-sharp/0.8.4/trim-2/webgpu-i32:<model digest>`. The two are close but not the same bytes,
  so they are cached apart, as layout snapshots are (ADR-0006).
- The diagnostics panel says where Kokoro runs ("kokoro on NVIDIA GPU (device 0x25BA) (WebGPU)"),
  and a fallback adds a note with its reason.

## Rechecked on ONNX Runtime 1.32 and plugin 0.6 (Core 0.91.0)

Stock exports still abort the process on WebGPU (`Invalid memory type: -1`); fp16 with int32 tokens
still gives NaN samples (65 529 in a 4.17 s clip, now at RTF 0.25); fp32 with int32 tokens still
gives real audio at RTF 0.26–0.28, and the opt-in GPU test still passes. Nothing here changes.

## Not done, and why

- **The fp16 model on the GPU.** It is twice as fast but its output is NaN on this device; the check
  would reject every clip.
- **CUDA, ROCm, OpenVINO.** Vendor-specific; ruled out.
- **Changing the CPU default thread count.** 4–6 threads beat the provider's 8 by about 10 % here,
  but the 8 was chosen on the reference machine and was not re-measured there.
- **Intel and AMD integrated GPUs.** WebGPU would use them too, but only the NVIDIA card was
  measured. The output check exists because integrated GPUs are where silent or garbage output was
  seen before (ADR-0002).
