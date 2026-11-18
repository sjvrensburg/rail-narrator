"""Local JSON-lines worker for the pinned MFA 3.3.9 / Kalpy runtime.

The model and lexicon stay loaded between clips. Inputs are already tokenized by
Rail Narrator. A word the pronunciation dictionary lacks gets a pronunciation before
alignment, never an <unk> interval: an all-capitals token is also tried spelled out
from the dictionary's own letter names (Kokoro reads "RMSE" as letters), and the
optional G2P model proposes one from the spelling. The aligner chooses between
candidates acoustically. A word with no candidate fails the clip explicitly.
No downloads, network calls, transcription, or guessed word durations occur here.
"""
import json
import sys
import wave
from pathlib import Path

protocol = sys.stdout
sys.stdout = sys.stderr  # Native/library diagnostics must not enter the JSON protocol.

import fcntl
import pywrapfst
from kalpy.aligner import KalpyAligner
from kalpy.feat.cmvn import CmvnComputer
from kalpy.fstext.lexicon import LexiconCompiler
from kalpy.fstext.lexicon import Pronunciation
from kalpy.utterance import Segment, Utterance
from montreal_forced_aligner.models import AcousticModel


def reply(value):
    print(json.dumps(value, ensure_ascii=False), file=protocol, flush=True)


def load(dictionary, acoustic, cache):
    model = AcousticModel(Path(acoustic))
    params = model.parameters
    lexicon = LexiconCompiler(
        disambiguation=False,
        silence_probability=params["silence_probability"],
        initial_silence_probability=params["initial_silence_probability"],
        final_silence_correction=params["final_silence_correction"],
        final_non_silence_correction=params["final_non_silence_correction"],
        silence_phone=params["optional_silence_phone"],
        oov_phone=params["oov_phone"],
        position_dependent_phones=params["position_dependent_phones"],
        phones=params["non_silence_phones"],
    )
    cache = Path(cache)
    cache.mkdir(parents=True, exist_ok=True)
    # Another application instance may be preparing the same content-addressed lexicon.
    with (cache / "lock").open("w") as lock:
        fcntl.flock(lock, fcntl.LOCK_EX)
        if (cache / "ready").exists():
            lexicon.load_l_from_file(cache / "L.fst")
            lexicon.load_l_align_from_file(cache / "L_align.fst")
            lexicon.word_table = pywrapfst.SymbolTable.read_text(cache / "words.txt")
            lexicon.phone_table = pywrapfst.SymbolTable.read_text(cache / "phones.txt")
        else:
            lexicon.load_pronunciations(Path(dictionary))
            lexicon.create_fsts()
            lexicon.fst.write(str(cache / "L.fst"))
            lexicon.align_fst.write(str(cache / "L_align.fst"))
            lexicon.word_table.write_text(cache / "words.txt")
            lexicon.phone_table.write_text(cache / "phones.txt")
            (cache / "ready").write_text("1", encoding="utf-8")
            lexicon.clear()
    return model, lexicon, KalpyAligner(model, lexicon, careful=True)


def letter_names(dictionary):
    """The most probable pronunciation of each single-letter entry: the letter's name."""
    best = {}
    with open(dictionary, encoding="utf-8") as lines:
        for line in lines:
            parts = line.rstrip("\n").split("\t")
            if len(parts) < 2 or len(parts[0]) != 1 or not parts[0].isalpha():
                continue
            try:
                probability = float(parts[1]) if len(parts) >= 6 else 1.0
            except ValueError:
                probability = 1.0
            if parts[0] not in best or probability > best[parts[0]][0]:
                best[parts[0]] = (probability, parts[-1].strip())
    return {letter: phones for letter, (_, phones) in best.items()}


def candidates(word, form, letters, g2p):
    """Pronunciations to try for a word the dictionary lacks, with where each came from."""
    found = []
    if form and form.isalpha() and form.isupper() and 2 <= len(form) <= 6 and all(c.lower() in letters for c in form):
        found.append((" ".join(letters[c.lower()] for c in form), "spelled"))
    if g2p is not None:
        for generated in (g2p.rewriter(word) or [])[:1]:
            if generated.pronunciation and generated.pronunciation not in (phones for phones, _ in found):
                found.append((generated.pronunciation, "g2p"))
    return found


def main():
    dictionary, acoustic, cache = sys.argv[1:4]
    g2p_path = sys.argv[4] if len(sys.argv) > 4 and sys.argv[4] else None
    model, lexicon, aligner = load(dictionary, acoustic, cache)
    letters = letter_names(dictionary)
    g2p = None
    if g2p_path:
        from montreal_forced_aligner.models import G2PModel
        g2p = G2PModel(Path(g2p_path))
    reply({"ready": True, "g2p": g2p is not None})
    stale = False  # the lexicon has words the aligner's compiled graph does not
    for line in sys.stdin:
        try:
            request = json.loads(line)
            words = request["words"]
            forms = request.get("forms") or [None] * len(words)
            if not words or len(forms) != len(words):
                raise ValueError("The request has no words, or forms that do not match them.")
            generated, uncovered = {}, []
            for word, form in zip(words, forms):
                if lexicon.word_table.member(word) or word in generated or word in uncovered:
                    continue
                found = candidates(word, form, letters, g2p)
                if not found:
                    uncovered.append(word)
                    continue
                for phones, source in found:
                    lexicon.add_pronunciation(Pronunciation(word, phones, None, None, None, None, None))
                    stale = True
                generated[word] = [[phones, source] for phones, source in found]
            if uncovered:
                raise ValueError("No pronunciation for: " + ", ".join(uncovered[:8]))
            if stale:
                # The graph compiler copies the lexicon when it is built, so it must be rebuilt to see
                # added words, including ones added by an earlier request that then failed.
                aligner = KalpyAligner(model, lexicon, careful=True)
                stale = False
            with wave.open(request["wav"], "rb") as wav:
                duration = wav.getnframes() / wav.getframerate()
            utterance = Utterance(Segment(Path(request["wav"])), " ".join(words))
            utterance.generate_mfccs(model.mfcc_computer)
            cmvn = CmvnComputer().compute_cmvn_from_features([utterance.mfccs])
            utterance.apply_cmvn(cmvn)
            aligned = aligner.align_utterance(utterance)
            entries = [[max(0, w.begin), min(duration, w.end), w.label] for w in aligned.word_intervals]
            reply({"tiers": {"words": {"entries": entries}}, "generated": generated})
        except Exception as exc:
            reply({"error": f"{type(exc).__name__}: {exc}"})


if __name__ == "__main__":
    try:
        main()
    except Exception as exc:
        reply({"error": f"{type(exc).__name__}: {exc}"})
        raise
