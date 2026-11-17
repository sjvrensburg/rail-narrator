"""Local JSON-lines worker for the pinned MFA 3.3.9 / Kalpy runtime.

The model and lexicon stay loaded between clips. Inputs are already tokenized by
Rail Narrator; unknown words fail explicitly instead of becoming <unk> intervals.
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


def main():
    model, lexicon, aligner = load(*sys.argv[1:])
    reply({"ready": True})
    for line in sys.stdin:
        try:
            request = json.loads(line)
            words = request["words"]
            if not words or any(not lexicon.word_table.member(w) for w in words):
                raise ValueError("The pronunciation dictionary does not cover every spoken word.")
            with wave.open(request["wav"], "rb") as wav:
                duration = wav.getnframes() / wav.getframerate()
            utterance = Utterance(Segment(Path(request["wav"])), " ".join(words))
            utterance.generate_mfccs(model.mfcc_computer)
            cmvn = CmvnComputer().compute_cmvn_from_features([utterance.mfccs])
            utterance.apply_cmvn(cmvn)
            aligned = aligner.align_utterance(utterance)
            entries = [[max(0, w.begin), min(duration, w.end), w.label] for w in aligned.word_intervals]
            reply({"tiers": {"words": {"entries": entries}}})
        except Exception as exc:
            reply({"error": f"{type(exc).__name__}: {exc}"})


if __name__ == "__main__":
    try:
        main()
    except Exception as exc:
        reply({"error": f"{type(exc).__name__}: {exc}"})
        raise
