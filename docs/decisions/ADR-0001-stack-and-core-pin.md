# ADR-0001: Stack, and pinning RailReaderCore to published NuGet packages

**Status:** accepted · **Date:** 2026-11-17 · **Amended:** 2026-11-18 (pin moved to 0.90.0, then 0.91.0), 2026-11-19 (0.93.0); see below

## Decision

C#/.NET 10, Avalonia 12.1.6 and SkiaSharp 3.119.4, consuming RailReaderCore as
**published NuGet packages pinned to 0.89.0** rather than as a source submodule.

## Context

The brief asked for a real published package if one exists, and a reproducible
source reference otherwise. Two facts settled it:

- `RailReader.Core`, `.Pdfium`, `.Analysis`, `.Ocr.RapidOcr`, `.Vlm.OpenAI` and
  `RailReader.Renderer.Skia` are all published at **0.89.0** on nuget.org.
- The upstream repository HEAD at the time of writing is
  `e7d41208454dcd4d25ab4a778309f6e483f1c953` — **the same commit**
  `docs/Rail Narrator implementation investigation.md` inspected, whose
  `VersionPrefix` is 0.89.0.

So the published package and the investigated source are the same snapshot. That
removes the usual reason to prefer a submodule (needing unreleased fixes).

SkiaSharp is pinned to 3.119.4 because `RailReader.Renderer.Skia` declares
`SkiaSharp 3.119.*`; picking a different minor would risk two native binaries in
the output directory.

## Consequences

- Restoring is reproducible and needs no network access to GitHub at build time.
- Every third-party version lives in one file, `Directory.Packages.props`, so
  "the build is reproducible" is checkable by reading it.
- Core cannot be modified. Where an additive seam would have been convenient, an
  application-side adapter was written instead — which the brief prefers anyway.

## Verified

`dotnet restore` across all 14 projects reports no version conflicts.
`RailReader.Core.Analysis` resolves ONNX Runtime **1.31.1** while `KokoroSharp`
alone resolves **1.22.0**; the combined application unifies on 1.31.1. That
combination is not assumed safe — `tools/RailNarrator.Smoke speak --provider
kokoro` exercises KokoroSharp inference in a process that also loads
RailReaderCore, and it runs.

## Amendment 2026-11-18: pin moved to 0.90.0

All six packages were moved to **0.90.0**, published from upstream commit
`b8b9dcfb4db84aa317032ed05c2f7f7077465b4a`. Upstream lists three fixes since 0.89.0 and no
public API changes:

- analysis: duplicate prose blocks are dropped in post-processing (upstream #216). This is
  the one that reaches narration: a duplicated block would be read twice.
- export: each region is exported once, and blocks whose text is not emitted no longer claim
  characters (#214). Rail Narrator does not use Core's Markdown export.
- annotations: `CreatedUtc` is stamped without an author (#213). Rail Narrator stores no
  annotations (`NullAnnotationStore`).

ONNX Runtime still resolves to 1.31.1 and SkiaSharp to 3.119.4. The Core assembly version
moved from 0.89.0.0 to 0.90.0.0, and `ServiceRegistry.ExtractionBackendFingerprint` folds that
into the extraction cache key, so snapshots made with 0.89.0 are not reused.
`ExtractorVersions.Current` is unchanged, because the application's own extractor did not change.

**Verified:** the build has 0 warnings. All 383 tests pass with every opt-in integration
enabled (real Kokoro, the PP-DocLayoutV3 layout model, MFA alignment, and silent output to a
real device), and `rail-narrator-smoke all` passes.
**Not measured:** the effect of the duplicate-block fix on a real document's narration.

## Amendment 2026-11-18: pin moved to 0.91.0

All seven RailReaderCore packages were moved to **0.91.0**, published from upstream commit
`f666eeade270f473ae7d4cceb662c3262bca0a73` (tag `v0.91.0`; the commit is the one recorded in the
package's nuspec). Upstream's change is dependencies: ONNX Runtime 1.31 → **1.32** (resolves
1.32.0) and the WebGPU plugin EP 0.5 → **0.6** (0.6.0), which ORT 1.32 is matched with, plus
OpenAI 2.15.0 for the VLM package. Upstream re-measured both layout models on the GPU against the
CPU (identical blocks on every page) on the same RTX A2000 as this project's laptop.

The application now resolves ONNX Runtime 1.32.0 throughout (KokoroSharp, built against 1.22.0,
still runs on it), the plugin 0.6.0, and SkiaSharp 3.119.4. The Core assembly version moves to
0.91.0.0 and is folded into the extraction cache key, so 0.90.0 snapshots are not reused.

**Verified:** the build has 0 warnings; all 470 tests pass with the opt-in integrations enabled
(real Kokoro, the PP-DocLayoutV3 layout model on the CPU and on the GPU, MFA alignment including
generated pronunciations, and Kokoro on WebGPU); only the real-audio-device test was left off.
Rechecked for ADR-0007 on plugin 0.6: stock Kokoro models (int64 `tokens`) still abort the process
on WebGPU, the fp16 export still gives NaN samples, and the converted fp32 model still gives real
audio at RTF 0.26–0.28, so the conversion and the fp32 choice stand.

## Amendment 2026-11-19: pin moved to 0.93.0

All seven RailReaderCore packages were moved to **0.93.0**, published from upstream commit
`5b89cb310c43d2f96c650dbdb7e27d125dc60042` (tag `v0.93.0`, as recorded in the package's nuspec).
0.92.0 changed only the Markdown export, which this application does not use. 0.93.0 answers the
two requests this project raised:

- **#222, region rendering.** Maths crops are rendered as a region (`RenderRegionPixmap`) instead
  of a whole page at the crop's scale: an 80 pt equation no longer costs an 8192 px page raster.
  The crops are not byte-identical to the old ones (measured; the whole-page render scaled its two
  axes a hair apart), so maths answers cached against old crops are asked for again once; the
  reader chose this over keeping the whole-page render. With the page no longer held to 8192 px,
  a narrow equation's crop gets the full width asked for (1024 px, where it had 828).
- **#223, XY-cut++ ordering.** Surveyed through this application's adapter with Heron INT8 on the
  CPU (`tools/RailNarrator.OrderSurvey`, OCR off) over the same 767 pages of 49 PDFs (the five
  econometrics papers of `local_lev_test/external papers` and the 44 of the RailDLA corpus, up to
  20 pages each): blocks read after the nearest content block directly below them fell from 13 to
  1 of 1,129 headings and from 107 to 6 of 7,141 other blocks.

Dependencies are unchanged from 0.91.0 (ONNX Runtime 1.32.0, WebGPU plugin 0.6.0), so ADR-0006 and
ADR-0007 stand. The Core assembly versions are part of the extraction cache key
(`ServiceRegistry.ExtractionBackendFingerprint`), so 0.91.0 snapshots are not reused and
`ExtractorVersions.Current` needs no bump.

**Verified:** the build has 0 warnings; all 562 tests pass with the opt-in integrations enabled
(real Kokoro on the CPU and on WebGPU, Heron and PP-DocLayoutV3 through the adapter on the CPU and
the GPU, MFA alignment); only the real-audio-device test was left off.

## Amendment 2026-11-19: pin moved to 0.94.0

All seven RailReaderCore packages were moved to **0.94.0**, published from upstream commit
`9cf331bde5be8951677afb41d5a6c537cd83c1bb` (tag `v0.94.0`, as recorded in the package's nuspec),
and an eighth, `RailReader.Core.Vlm.Anthropic`, was added at the same version. 0.94.0 holds two
changes:

- **`RailReader.Core.Vlm.Anthropic`**: an `IVlmService` for Claude over Anthropic's .NET SDK,
  chosen by `VlmEndpointConfig.Provider`. This application uses it for the vision transcriber when
  the reader chooses Claude there (ADR-0010).
- **#228, VLM block crops rendered as regions.** No effect here: this application does not call
  `BlockCropRenderer`, and its own maths crops have been regions since 0.93.0.

The three requests this project depends on (#222 region rendering, #223 XY-cut++ ordering, #225
re-inserted blocks following their own column) were already in 0.93.0.

The third-party dependencies of the seven packages are unchanged from 0.93.0 (compared from their
nuspecs), so ADR-0006 and ADR-0007 stand. The Core assembly versions are part of the extraction
cache key, so 0.93.0 snapshots are not reused and `ExtractorVersions.Current` needs no bump.

**Verified:** the build has 0 warnings and the default suite passes. The order survey
(`tools/RailNarrator.OrderSurvey`, Heron INT8 on the CPU, OCR off) gives the same output on 0.93.0
and 0.94.0, finding for finding: 1 of 94 headings and 0 of 762 other blocks read after the block
below them, over 92 pages of the five econometrics papers of `local_lev_test/external papers`. The
44 RailDLA papers of the 0.93.0 survey were not on the machine. The Pdf suite passes with its
opt-in integrations on (Heron and PP-DocLayoutV3 through the adapter, on the CPU and on the
Radeon 8060S through WebGPU). *Not run against 0.94.0:* real Kokoro, MFA alignment and the
real-audio-device test, none of which touches Core.
