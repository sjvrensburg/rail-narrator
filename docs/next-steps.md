# Next steps and open decisions

Handover note, 18 November 2026, after PR #10. `main` builds with 0 warnings; 380 tests pass
and 3 opt-in tests are skipped. What is verified, and how it was measured, is in `status.md`.
How to run the real models, the GUI and the audio path on a new machine is in
`testing-real-world.md`.

## Where things stand

The pipeline works end to end on a real 27-page statistics paper: layout-aware skipping,
citation removal, maths interpreted during playback by a local multimodal LLM, a rule-based
mathspeak fallback, and pre-roll buffering on Play. The last full measurement
(`status.md` item 39): of 224 maths passages, 180 were interpreted, 43 read as notation by
rule and 1 announced. That took about 62 minutes of model time against about 85 minutes of
narration.

## First, on a new machine

1. Install the prerequisites and models listed in `testing-real-world.md`, then run
   `dotnet build RailNarrator.slnx -c Release && dotnet test RailNarrator.slnx -c Release`.
2. Re-run the opt-in tests that apply (Kokoro, layout model, audio device; variables in
   `CLAUDE.md`).
3. If the LLM is a different model or on different hardware, run `rail-narrator-mathbench` on a
   maths paper first. The thinking policy and the timings in `status.md` hold only for
   Ornith-1.5-35B-A3B on the reference machine.

## Work that needs no decision

In rough priority order:

- **Look at the pre-roll message in the GUI.** "Interpreting maths before reading (n of 60 s
  ready)" is covered by tests but has never been watched in the running app. Play from just
  before a maths-heavy page with an empty cache.
- **Make rule-read passages cheaper.** A passage that ends up read by rule costs a thinking
  attempt (often running out of tokens), a thinking-off retry, a refusal and then a
  transcription: about 66 s for a lead-in. That is the main reason model time is close to
  narration time. Options: a smaller thinking budget for the first attempt, or transcribing in
  parallel when the server has a second slot. Measure with `rail-narrator-mathbench`.
- **Saving a restored position as a passage id.** When the plan changes (new layout model, a
  version bump), the saved id can point at a different passage. Store a page plus text-offset
  anchor and map it onto the new plan.
- **Proof-read a sample of rule readings against the printed equations**, symbol by symbol. So
  far they have been checked only for leftover notation.
- **Identify the flaky Audio test.** It failed once in a full-solution run while the LLM was
  busy on the same machine, then passed four times. Loop the Audio suite under CPU load and
  look for a timing assumption.
- **More documents.** Everything about maths was measured on one paper. Try a physics paper,
  a pure-maths paper and an ML paper.

## Decisions needed from you

1. **How should equations be read by default?** The interpreter still makes meaning errors
   even with the page image (the order of the transition polynomial was twice called "the
   number of regimes"). The choices:
   - keep interpretation as the default (current);
   - read the notation for display equations and interpret only inline maths;
   - interpret, but follow every interpreted equation with a short notation reading.
2. **DOIs.** They are currently read as "ten point one zero one six slash …". Skip them, spell
   them, or say "DOI link" in their place?
3. **Author affiliations** on the first page are still read. Skip them like the other front
   matter, or keep them, since some readers want to know where the work is from?
4. **Gapless playback.** About 0.4 s of silence is added between passages, on top of Kokoro's own
   ~0.5 s trailing silence. Removing it means writing the next clip before the current one
   drains, which changes `IAudioTransport` and the coordinator's clip-completion path. It is a
   medium-sized change to the most delicate part of the code. Worth it now?
5. **Words missing from the pronunciation dictionary** ("GARCH", authors' names) send their whole
   sentence to sentence-level highlighting; 58 % of the paper's prose passages are affected.
   Options:
   - highlight the in-dictionary words and treat the missing ones as gaps;
   - generate pronunciations for missing words (a G2P model: another model to install);
   - accept sentence highlighting.
6. **Sideways pages with no Rotate attribute.** The layout model sees the page unrotated and
   loses a line. Detect the orientation and rotate the raster before layout (application side,
   since Core cannot change)? Or leave it, since such pages are rare?
7. **Which LLM endpoint on the new system?** Defaults assume a local server on `:8088`. A
   remote API would need `MathLlmApiKeyEnvVar` and means sending page images off the machine.
   Say whether that is acceptable.
