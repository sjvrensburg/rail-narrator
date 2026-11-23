# Word highlighting

English prose can be highlighted word by word using a local Montreal Forced Aligner
(MFA) runtime. It aligns the exact spoken script against the final, trimmed PCM
that is cached and played. It works with the existing Kokoro audio-only model.
Equations retain whole-equation highlighting because their spoken paraphrases do
not correspond one to one to the printed symbols.

## Without the aligner

Two engines can time their own words, with nothing else installed (ADR-0013): Phonon, once its
library is built by `tools/setup-phonon.sh` (which applies `tools/phonon-word-timing.patch`), and
Kokoro with the export that has a durations output (Models and endpoints ▸ Download Kokoro with
word timing). Settings ▸ Reading ▸ Word timing from chooses whose timing is shown: the aligner's
when it is installed (the default; the engine's is shown until the aligner's arrives), or the
engine's own, in which case the aligner is not run for a passage the engine timed. Against the
aligner, Phonon's word starts are a mean 30 ms away and Kokoro's 16 ms. The rest of this
document is about the aligner.


## Setup on Linux

Install micromamba, then run from the repository:

```bash
bash tools/setup-word-alignment.sh
```

This explicitly downloads a separate environment containing MFA 3.3.9, Kalpy
0.10.5, Kaldi 5.5.1172 and Python 3.11, plus the `english_mfa` acoustic model and
`english_uk_mfa` pronunciation dictionary. Allow several GB for the runtime and
package cache. The script records model checksums. No Python packages are added
to the system environment. Restart Rail Narrator after setup.

By default the files live in
`$XDG_DATA_HOME/rail-narrator/alignment` (normally
`~/.local/share/rail-narrator/alignment`). The application never downloads them
implicitly. Existing installations can be selected by merging these settings into
`$XDG_CONFIG_HOME/rail-narrator/settings.json` (normally
`~/.config/rail-narrator/settings.json`):

```json
{
  "WordHighlighting": true,
  "MfaExecutable": "/absolute/path/runtime/bin/mfa",
  "MfaDictionaryPath": "/absolute/path/english_uk_mfa.dict",
  "MfaAcousticModelPath": "/absolute/path/english_mfa.zip",
  "MfaG2pModelPath": "/absolute/path/g2p/english_uk_mfa.zip",
  "MfaRuntimeRevision": "3.3.9"
}
```

The worker uses the `python` executable beside `mfa`. Use the pinned environment;
the adapter depends on Kalpy's Python API. A custom install root passed to the
setup script requires these path overrides. `WordHighlighting: false` selects
sentence highlighting even when cached word cues exist.

## What the application does

- Keeps the model and lexicon loaded in a local worker, and prepares clips ahead
  of playback. Cancellation or a 120-second timeout kills and reaps the worker;
  a later request starts a fresh one.
- Matches every aligned word to its ordered UTF-16 range in the spoken script,
  then follows the source mappings to individual PDF word rectangles. Repeated
  words, ligatures, dehyphenation and expanded numbers retain their source links.
- Gives a word the dictionary lacks a pronunciation before aligning: an all-capitals acronym
  is also tried spelled out from the dictionary's letter names, and the G2P model proposes
  one from the spelling. The aligner chooses between them acoustically, and the timing notes
  name every word aligned this way. A word with neither (a letter–digit token such as
  "ggplot2") falls that sentence back to sentence highlighting, and the reason names the word.
- Requires complete lexical coverage, valid ordered intervals and known anchors.
  Silence need not occupy word intervals. MFA supplies no calibrated word
  probability, so confidence is left unknown rather than invented.
- Falls back to sentence highlighting when assets are missing, a word has no
  pronunciation, or alignment fails. Playback remains available and the
  timing status shows the reason. It never spreads words evenly across a clip.
- Keys alignment by PCM hash, spoken-script hash and aligner/model/dictionary
  revision. Model changes can realign cached speech without synthesising it again.
  PCM hash checking also detects a same-length replacement of cached audio.
- Reads PortAudio's callback DAC timestamps and stream clock to estimate the
  presented content position. Underrun silence does not advance that position.
  WSOLA carries an output-to-source map through speed changes. Highlights refresh
  on animation frames; camera movement is throttled separately.

English integers and decimals are expanded before synthesis so the same words
reach the speech engine and aligner. Unknown technical terms and unusual spoken
forms can still require pronunciation substitutions or a compatible dictionary.
Dictionary coverage and complete intervals do not prove acoustic accuracy.

## Verification and remaining accuracy work

On the reference machine, the real Kokoro sentence “The quick brown fox jumps
over the lazy dog” produced 2.952 seconds of audio and nine valid aligned words.
The first worker request took 2.913 seconds; a repeat with the loaded worker took
0.017 seconds. These are a small integration benchmark, not a throughput guarantee.

Run the model-free suite with `dotnet test RailNarrator.slnx -c Release`. To run the
real alignment tests, export `RAIL_NARRATOR_KOKORO_MODEL`, `RN_MFA_EXECUTABLE`,
`RN_MFA_DICTIONARY` and `RN_MFA_MODEL` (and `RN_MFA_G2P` for the test with words the
dictionary lacks), then run:

```bash
dotnet test tests/RailNarrator.Speech.Tests -c Release \
  --filter FullyQualifiedName~RealKokoroAudioAlignsEveryWord
RN_AUDIO_INTEGRATION=1 dotnet test tests/RailNarrator.Audio.Tests -c Release \
  --filter FullyQualifiedName~DeviceIntegrationTests
```

The first test saves WAV, script, word intervals and cold/warm timings under
`/tmp/rn-mfa-integration`. The second uses silent PCM on a real output device and
checks pause, seek, speed changes and final drain. Normal runs skip these two tests
when their dependencies are not explicitly enabled.

No end-to-end millisecond accuracy bound is claimed yet. Manually label word
onsets in a varied speech corpus and measure median, p95 and worst alignment
error; then use output loopback and display capture to measure the audible-to-visible
offset at several speeds and on different devices. WSOLA overlaps multiple source
regions, so its weighted source map is a correspondence estimate. Device timestamp
quality, compositor delay and display refresh also remain part of the error budget.
