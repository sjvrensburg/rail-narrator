# ADR-0001: Stack, and pinning RailReaderCore to published 0.89.0 packages

**Status:** accepted · **Date:** 2026-11-17

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
