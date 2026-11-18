# ADR-0008: Docling Heron as the layout model

**Status:** accepted · **Date:** 2026-11-18 · **Supersedes:** the implicit PP-DocLayoutV3 default

## Decision

The layout model is **Docling Heron** when it is installed, with PP-DocLayoutV3 as the
alternative and the model-free analyzer as the last resort. It is chosen and built the way
RailReader2 does it, through RailReaderCore's `LayoutModelRegistry` routing and
`LayoutAnalyzerFactory`: Heron's **FP32** export on a GPU (WebGPU), its **INT8**-backbone export
on the CPU. Heron gives no reading order of its own, so blocks are ordered by Core's XY-cut++, the
pairing RailReader2 and Core's `AnalysisWorker` use. The setting `LayoutAnalyzer` takes `heron`,
`pp-doclayout-v3`, `none`, or anything else for automatic (Heron, then V3, then none);
`text-layout`, the old default older settings files still carry, means automatic.

Files are looked for in the application's models directory, then in RailReader2's
(`$XDG_CONFIG_HOME/railreader2/models`), read only. Nothing is downloaded implicitly; the Models
menu offers Heron INT8 (69 MB) and FP32 (171 MB).

## Context

Nothing recorded why PP-DocLayoutV3 had been the model; the application was hard-wired to it. The
operator pointed out that RailReader2 has run Heron with Core's geometric reading order for months,
that Heron is accurate with maths, and that Core documents its speed. Core's own record: on a GPU,
FP32 Heron is 9.85× faster than on the CPU (PP-DocLayoutV3 7.98×) with 0 missed and 0 extra
detections on a 42-page, 11-document corpus (0.60.2); on the CPU the INT8-backbone Heron is the
recommended default, 2.6–3× faster than FP32 at about 0.99 detection agreement (0.9.0).

## Evidence

Four papers from the RailDLA corpus (a 20-page slide deck of OLS algebra, an Elsevier two-column
article, an arXiv preprint, a Frontiers article), extracted and planned end to end by the
application's own composition root on the i7-12700H laptop with its RTX A2000, warm driver cache:

| Document | Pages | Heron, GPU | V3, GPU | Heron INT8, CPU | V3, CPU |
|---|---|---|---|---|---|
| OLS slides | 20 | 10.1–13.7 s | 20.2 s | 58.1 s | 141.6 s |
| Elsevier article | 14 | 11.1–16.7 s | 16.8 s | | |
| arXiv preprint | 17 | 17.1–19.6 s | 37.9 s | 68.3 s | 122.1 s |
| Frontiers article | 12 | 7.2–7.4 s | 12.2 s | | |

What is read, compared by content (a passage counts as the same when all its words appear in the
other model's script, whatever the segmentation):

- **Display equations**: 38/37, 1/1, 12/12 and 2/5 (Heron/V3).
- **References**: neither model's script reads a bibliography. V3 labels reference entries; Heron
  has no such class, so the reference-section rule below supplied it: 60, 21 and 41 blocks under a
  References heading on three of the papers (the slide deck has none).
- **Differences**: Heron reads some page furniture V3 skips (an Elsevier journal banner and its
  "(caption on next page)", an algorithm's pseudo-code read as prose rather than announced); V3
  reads some Heron skips (an arXiv margin stamp, a Frontiers article's editor and citation front
  matter and its running footer). Heron dropped one sentence of an Elsevier article that V3 read;
  V3 dropped none that Heron read.

Opt-in tests on the real models: Heron INT8 reads a two-column page left column first; Heron FP32
gives the same blocks, line counts and reading units on the GPU as on the CPU.

## The reference-section rule

Narration skips references by default, and PP-DocLayoutV3's `reference` class was the only thing
that did it. `ReferenceSections` (Domain) relabels, from a heading or title that reads
"References", "Bibliography", "Works Cited" and the like up to the next heading or title, every
prose block as a reference, across pages in reading order. It runs only when the snapshot has model
roles and no block is already a reference, so a model that labels references is trusted, and the
model-free analyzer (no headings) is left alone.

## Consequences

- Extraction identity names the model (`heron` or `pp-doclayout-v3`), and the cache fingerprint
  the file that runs, so switching model or device re-extracts.
- The diagnostics panel names the model and file ("Docling Heron (docling-layout-heron.onnx) on
  NVIDIA GPU").
- PP-DocLayoutV3's model-supplied inline-maths boxes are gone with Heron; sentences with maths are
  still flagged by the glyph detector and the text cue (`ReadingUnitBuilder.HasInlineMaths`).
- Not measured: Heron's maths interpretation end to end with the LLM (the plans above were built
  without an endpoint, so maths passages were not interpreted), and the "References" heading itself,
  which is still spoken under both models.
