# ADR-0006: Layout analysis on the GPU through RailReaderCore's WebGPU plugin

**Status:** accepted · **Date:** 2026-11-18

## Decision

The PP-DocLayoutV3 layout model runs on a WebGPU-capable GPU when one is found and the model
loads on it, and on the CPU otherwise. This is the setting `LayoutAcceleration`, default `Auto`;
`Cpu` turns the GPU off. The GPU comes from `RailReader.Core.Analysis.WebGpu` (0.90.0 when this was
decided: WebGPU plugin EP 0.5.0 with ORT 1.31.1; 0.91.0 since, with plugin 0.6.0 and ORT 1.32.0,
re-tested in ADR-0001's 0.91.0 amendment). OCR stays on
the CPU.

## Context

RailReader2 analyses only the page on screen, so a slow layout model is never felt there.
Rail Narrator analyses the whole document before it can narrate, so layout time is load time. On
a 24-page, 336 KB pdfTeX chapter, timed on an i7-12700H laptop (20 threads, 31 GB) with an
NVIDIA RTX A2000 8 GB Laptop GPU, Ubuntu 24.04, with nothing else running:

| Where the model ran | Whole-document extraction | Start-up (backend built) |
|---|---|---|
| CPU, Core's default 4 intra-op threads | 26.9 s and 29.5 s (two runs) | 0.57–0.63 s |
| GPU (WebGPU on the RTX A2000) | 11.7 s and 14.7 s (two runs) | 1.2–1.5 s |

Model inference alone, on an 800 × 800 raster, in steady state: 1.24 s per page on 4 CPU threads,
0.73 s on 8, 0.64 s on 14, and 0.28 s on the GPU. Raising the CPU thread count was the other
option. It buys less (about 1.1 s per whole page at 14 threads against 0.7 s on the GPU) and takes
the whole machine while it runs, which Core's 4-thread cap exists to prevent.

The first timings taken that day were 4–5 times slower than these, for both CPU and GPU, while
idle .NET build servers were competing for the CPU. Numbers from a busy machine are not
comparable.

## Same reading, not the same bytes

CPU and GPU snapshots of the same chapter, compared field by field:

- the same number of blocks and lines on every page, the same block kinds, and the same reading
  sequence on every page;
- block geometry differed by at most 0.02 pt, line geometry by at most 0.0003 pt, and confidence
  by at most 5 × 10⁻⁴;
- the model's raw reading-order score differed by one on three blocks, without changing the
  order.

Core reports the same on a 42-page, 11-document corpus: no missed and no extra detections with the
FP32 model on WebGPU. Narration is unchanged. The device still goes into the extraction cache key,
because a cached snapshot is meant to be what this configuration produces. Switching device costs
one re-extraction and never reuses geometry produced the other way.

## How it is wired

- `LayoutAnalyzerConstruction` follows Core's documented pattern. It holds
  `WebGpuAccelerator.ConstructionLock` for the whole enable → construct → disable sequence, for CPU
  constructions too (they depend on the static hook being null), and the hook is never left
  installed outside the lock.
- `ExtractionBackend.OnnxLayout(…, LayoutAcceleration.Auto)` decides once, at start-up, by building
  the probe analyzer on the GPU. If no device is found, the runtime is missing or the model will not
  load, the backend is CPU and `LayoutDevice.FallbackReason` says why. The app shows that reason as
  a start-up note, and the diagnostics panel names the device.
- An extraction that cannot start its GPU analyzer, or whose GPU inference throws partway through,
  finishes on the CPU starting from the page that failed, and that page's warnings say so. Without
  this, a GPU fault would leave every remaining page without blocks.
  The source then reports `LayoutDeviceFellBack`, and the app stops caching snapshots, because one
  made partly on the CPU does not match a cache key that names the GPU.
- Library callers (tests, `rail-narrator-smoke`, `rail-narrator-mathbench`) default to the CPU and
  opt in.

## Consequences

- The release grows by 15.8 MB (`libonnxruntime_providers_webgpu.so`). The GPU path needs the
  system Vulkan loader (`libvulkan.so.1`) and a driver; without them the app runs on the CPU as
  before.
- One process uses one GPU, chosen by Dawn: a discrete card over an integrated one. There is no
  setting to pick an adapter.

## Verified

- With the Vulkan drivers hidden (`VK_ICD_FILENAMES`/`VK_DRIVER_FILES` pointing at nothing), `Auto`
  fell back to the CPU with the reason "Failed to get a WebGPU adapter: No supported adapters" and
  extracted the chapter normally.
- `LayoutAccelerationTests` run with no GPU and no model. They cover a start-up failure and a
  mid-document failure, each finishing on the fallback with a single warning, and the old
  behaviour when there is no fallback, plus the source reporting the fallback. Opt-in tests check that the `Cpu` setting never uses the
  GPU and, with `RN_WEBGPU_INTEGRATION=1` on this machine, that the GPU and the CPU give the same
  blocks, kinds, reading units and geometry within 0.1 pt.

## Not verified

- AMD and Intel GPUs. Core measured an Intel Iris Xe at about 1.8 s per page; this application has
  not been run on one.
- Long sessions with many documents on the GPU.
- The Fedora reference machine.
