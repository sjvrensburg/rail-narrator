# ADR-0011: A formula model for display equations

**Status:** accepted · **Date:** 2026-11-20 · **Follows:** ADR-0010

## Decision

The reader asked (2026-11-20) for PP-FormulaNet_plus-M in the application, with the choice of
sending a language model its output, the page image, or both. The aim is to stop depending on a
slow or expensive vision model for the passages that cost most: a local 35B model takes about
19 s for a display equation.

PP-FormulaNet_plus-M (Baidu, Apache-2.0) reads the image of one display equation to LaTeX. It runs
in process on ONNX Runtime, on the CPU, behind a new port, `IFormulaRecogniser`
(`PpFormulaNetRecogniser` in `RailNarrator.Inference`). It is used when its two files are
installed and `UseFormulaModel` is on (the default). It is never bundled and never fetched
implicitly: Models and endpoints ▸ Download formula model fetches both files, with size and
licence shown first and the digests checked after.

What it is used for:

- **Evidence for the language model.** `MathLlmEvidence` decides what the interpreter is given for
  a passage that holds a display equation: `Image` (the page image, as before the formula model),
  `Formula` (the LaTeX and no image) or `Both` (the default). The text layer and the surrounding
  text are sent in every case. Without the formula model it is always the image.
- **The reading by rule.** A display equation whose interpretation is refused is read by
  `LocalLatexSpeaker` from the formula model's LaTeX, which is already made, before the
  interpreter is asked to transcribe the image itself.
- **No language model at all.** Display equations are transcribed during playback and read by
  rule, labelled `LocalRule`. A sentence that leads into an equation keeps its printed reading and
  the equation's announcement becomes the reading. Before, every equation was announced as
  unreadable.
- **The older reading modes.** With no vision model configured, the formula model is the
  `IMathTranscriber` (`FormulaModelTranscriber`).

The model reads a formula's crop, not a line of text. A sentence with maths in its line therefore
has nothing for it to read: under `Formula` such a sentence is interpreted from its text layer and
context alone, and under `Both` from its image, as before. This is the stated limit of "LaTeX
only", not an oversight: sending the image whenever there is no LaTeX would send images for most
passages of a paper, which is what the reader choosing that option wants to avoid.

## Which model and which file

The choice of model rests on a survey of published sources (20 November 2026; not in the
repository), not on a comparison of models here. PP-FormulaNet_plus-M was its first candidate:
permissive weights, an ONNX graph that needs no Python, published CPU times of one to two seconds
an equation, and adoption as the CPU formula model of MinerU 4 and RapidDoc. No other formula
model was run here.

The file is OpenDataLab's export, the one MinerU 4 runs:
`opendatalab/MinerU-4_models_onnx`, `MFR/pp_formulanet_plus_m/PP-FormulaNet_plus-M.onnx`
(591,263,297 bytes) and its `inference.yml` (2,244,564 bytes), pinned to repository commit
`358310b4f64b95f9fefc372ad899356e4111f376`. The digests in the manifest are of the files
downloaded on the reference machine and equal those in OpenDataLab's own manifest. That
repository carries no licence tag; it says "Original model licenses and attribution remain
applicable", and the weights are Baidu's, Apache-2.0.

The graph holds the whole decoding loop: a 384 × 384 grey image in, token ids out, with the end
of the sequence forced at 1,536 tokens. So the port is small (prepare the image, one `Run`, decode
the ids), and there is no token probability to read. The checks are therefore on the output and
against the page, below.

## The port

- **The image** (`FormulaImage`, `PillowResample`) follows the model's reference pipeline step
  for step: trim the margins, scale the short side to 384 (bilinear), shrink to fit the square
  (Pillow's `thumbnail`: a box reduction, then bicubic), centre on black, normalise. The scalings
  are done in Pillow's 8-bit arithmetic. Measured: the prepared image is the reference's byte for
  byte on 260 images (240 crops of the GARCH paper's equations at three paddings and 20 synthetic
  shapes), against Pillow 11.3 and 12.0. Three things had to be found by comparison and are in
  the code as comments: the rounding of the box reduction, the single-precision box of the
  second scaling, and that Pillow 13 runs the vertical pass first for some sizes (a few pixels
  differ by one level there; the model was not trained on that). A plainer scaling, without the
  box reduction, changed the tokens of one to five equations in eighty.
- **The vocabulary** (`FormulaVocabulary`) is read from `inference.yml`, PaddleX's YAML dump of a
  byte-level BPE tokeniser with 50,000 tokens. Decoding needs only the id-to-token table and the
  byte alphabet, so only the `vocab:` and `added_tokens:` blocks are read, by their indentation. A
  line the reader does not understand, a missing id or a file without a vocabulary is an error.
  No YAML library was added for one file whose digest is pinned.
- **The output** (`FormulaOutput`): upright-Greek commands written as the ordinary ones and
  layout commands dropped, as the reference post-processing does. The reference's repair of
  `\left`/`\right` pairs and environments is not ported; it exists for rendering, and the reader
  by rule does not need it.
- **One equation at a time**, on half the logical processors and at most eight threads. The
  session is built on first use. A cancelled token or a run longer than 45 s terminates the run.
- **The CPU only.** The graph's loop has no WebGPU kernel according to the survey, and the one
  published WebGPU measurement was slower than the CPU for a batch of large formulae. A GPU was
  not tried here.

## The crop

The formula model gets a crop of its own: the equation's bounds, 2 pt wider at each side and no
taller, 1,024 px across. The interpreter's crop is padded 6 pt all round, which takes in the edge
of the line above or below. Measured on the GARCH paper's 80 display equations: with the 6 pt
crop one equation ran on to the 1,536-token limit (it transcribed the top of the next equation and
then repeated itself, 7 s) and others gained stray symbols; with no padding above and below, none
did, and 34 of the 80 transcriptions differed from the padded ones.

## What stands between a transcription and the listener

A formula model can be wrong with nothing in its output to show it. The survey found no evidence
that token probability detects this for any such model, and this graph gives none anyway.

- **The output must be one equation's worth**: ended before the token limit, not ending in the
  same tokens over and over, braces balanced. Otherwise it is a problem, reported as a warning on
  the passage, and no transcription.
- **The page's text layer is the second witness** (`FormulaTextLayerCheck`). The text layer has an
  equation's structure wrong and its characters mostly right; the model has the structure right
  and can take one character for another. Two comparisons are made:
  - *Greek letters*, as sets. When each side names exactly one letter the other lacks, the model
    took one for the other: the text layer's letter is used, and a note says so. Any other
    difference is reported and left standing. A text layer with no Greek letter says nothing, and
    one with unmapped glyphs cannot show that a letter is absent.
  - *Decimal numbers*: a number the text layer has and the transcription lacks is a difference.
- **A transcription that still differs from the page is not read aloud by rule.** The interpreter
  is given it with the note and, under `Both`, the image; it is told the image is right where the
  two differ, and under `Formula` that the text layer is right about which letters occur.
- **Every reading keeps its label**: `LocalRule` with "it says the notation, not its meaning", or
  the interpretation's warning, which now names the formula model's transcription as a source.

## Caches and prompts

- A request without a formula is built and keyed exactly as before: `MathInterpretationPrompt`'s
  text is unchanged for it (a test pins it word for word) and `MathInterpretationPrompt.Version`
  is not bumped, so answers cached before this change stay valid.
- A request with a formula carries `RecognisedFormula.EvidenceVersion` and the transcription in
  its key. Change the formula paragraph of the prompt, or what `RecognisedFormula` carries, and
  bump that constant.
- A transcription is cached by the recogniser's revision (pipeline version and model file) and the
  crop's hash. Change the image preparation, the decoding or the output checks and bump
  `PpFormulaNetRecogniser.PipelineVersion`.
- `LocalLatexSpeaker.Version` is now `clearspeak/3`: a lone typed bar is read "given", as `\mid`
  already was ("E(y | F)" was left with the bar in it and so refused).

## Measurements

All on the reference machine (AMD Ryzen AI MAX+ 395, Fedora 44), 20 November 2026, on the GARCH
paper (27 pages; 80 display equations as PP-DocLayoutV3 finds them), eight threads.

- **Speed.** 72 s of formula-model time for the 80 equations: median 0.89 s, longest 2.0 s
  (`rail-narrator-mathbench --formula-model … --no-llm`). The local vision model's own
  transcription takes 8.7 s for an equation (`status.md` item 39).
- **Memory.** The recogniser alone, reading the 80 crops from files, peaked at 1.14 GB resident;
  the benchmark tool with the layout model and the PDF at 1.6 GB.
- **Same output as the reference pipeline.** On the 80 crops the C# port's LaTeX equals the
  Python reference's (ONNX Runtime 1.32, Pillow 12) for all 80, apart from one upright-delta
  command it rewrites on purpose.
- **With no language model**, 78 of the 80 equation passages were read by rule (20 of them an
  equation alone, the rest after their lead-in sentence as printed) and 2 were announced as
  unreadable, with the reason, because the page has a number the transcription lacks. Before, all
  80 were announced as unreadable.
- **The text-layer check.** It settled a zeta read as xi in 5 equations and left no letter
  disagreement standing; the number check, added after the sample below, flags the 2 above.
- **Against the page.** 15 of the 80 transcriptions were compared with the page by eye (five while
  developing, ten picked across the paper). 14 are right, two of them only because the check put
  zeta back; one leaves out the standard error printed under a coefficient, which the number check
  now flags. The other 65 were not proof-read.
- **With the local language model** (Ornith-1.5-35B, thinking for equations), pages 3 to 7, 85
  passages. Image only, the baseline: 71 interpreted and 14 read by rule in 776 s; of the 26
  passages with an equation, 15 interpreted and 11 by rule, 600 s. The run with both was stopped
  after 16 passages (6 with an equation): all 16 were interpreted, where the image-only run
  interpreted 12 of the same 16 and read 4 by rule, in the same model time (129 s against 128 s)
  plus 3.6 s for the formula model. Sixteen passages do not settle whether the transcription
  makes interpretation succeed more often. The LaTeX-only setting was not run against a model.

## Not verified

- **Other documents.** One paper, one publisher's fonts, one layout model. Heron has no class for
  an equation number, so its equation blocks may include the number, which the model would
  transcribe; not run.
- **The 65 equations not proof-read**, and whether the two checks miss an error: they compare
  Greek letters and decimal numbers only, so a wrong Latin letter, index or sign passes.
- **The three settings side by side**: the comparison above stopped after 16 passages, and
  LaTeX only was not run. A text-only language model given the LaTeX, the case that setting exists
  for, was not tried. Whether `Both` is the right default therefore rests on reasoning (the model
  is told the image wins), not on a measurement.
- **Claude** with a formula: the prompt is shared, and no request with a formula was sent to
  Anthropic.
- **The download through the application's menu** (the URLs answer with the manifest's sizes; the
  files were fetched with the Hugging Face CLI).
- **Slower machines**: the 45 s limit and the eight-thread default come from one 16-core machine.
- **A GPU**, and other formula models (PP-FormulaNet_plus-L, TexTeller 3, UniRec), which the
  survey ranks next.
