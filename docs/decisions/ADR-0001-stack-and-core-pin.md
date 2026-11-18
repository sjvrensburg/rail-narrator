# ADR-0001: Stack, and pinning RailReaderCore to published NuGet packages

**Status:** accepted · **Date:** 2026-11-17 · **Amended:** 2026-11-18 (pin moved to 0.90.0, see below)

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
