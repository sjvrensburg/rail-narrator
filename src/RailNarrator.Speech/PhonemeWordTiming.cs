using System.Text;
using RailNarrator.Domain;

namespace RailNarrator.Speech;

/// <summary>One phoneme of what a model spoke, with the samples it occupies in the clip.</summary>
public readonly record struct TimedPhoneme(char Symbol, long StartSample, long EndSample);

/// <summary>
/// Word timing from a speech model's own phoneme durations: the lengths it gave each phoneme when
/// it made the audio, which is exact by construction (Kokoro: each phoneme's frame count times 600
/// samples adds up to the clip to the sample).
/// </summary>
/// <remarks>
/// What is not given is which phonemes are which word of the script. The phoneme string keeps the
/// text's spaces and punctuation, so its words are counted off against the script's. A compound
/// the phonemiser reads as one word ("vine-based") is divided where its first part's own phonemes
/// end, if they do end there; no time is ever shared out by letter count. When the two sides do
/// not correspond (an abbreviation the phonemiser expanded, a symbol it read aloud) there is no
/// timing at all, with the reason, and the passage is highlighted whole or left to an aligner.
/// </remarks>
public static class PhonemeWordTiming
{
    /// <summary>Stress marks: where they fall can change when a word is phonemised on its own.</summary>
    private static bool IsStress(char c) => c is 'ˈ' or 'ˌ';

    /// <summary>What stands between the words of a phoneme string: space and punctuation.</summary>
    private static bool IsSeparator(char c) => char.IsWhiteSpace(c) || char.IsPunctuation(c) || c is '…';

    private static bool IsHyphen(char c) => c is '-' or '‐' or '‑';

    /// <summary>
    /// The script's words (<see cref="SpokenWord.Tokenize"/>) with their sample intervals, or null
    /// with the reason in <paramref name="problem"/>.
    /// </summary>
    /// <param name="phonemes">What was spoken, in order, pauses and punctuation included.</param>
    /// <param name="phonemise">The model's phonemiser for a single word, to divide a compound; null to refuse compounds.</param>
    public static IReadOnlyList<TimedCue>? Words(
        string script, IReadOnlyList<TimedPhoneme> phonemes, Func<string, string>? phonemise, out string? problem)
    {
        ArgumentNullException.ThrowIfNull(script);
        ArgumentNullException.ThrowIfNull(phonemes);
        problem = null;

        // The spoken words: runs of phonemes between spaces and punctuation.
        var spoken = new List<(int First, int Count)>();
        for (int i = 0; i < phonemes.Count;)
        {
            if (IsSeparator(phonemes[i].Symbol)) { i++; continue; }
            int start = i;
            while (i < phonemes.Count && !IsSeparator(phonemes[i].Symbol)) i++;
            spoken.Add((start, i - start));
        }

        // The script's words, those joined by a hyphen taken together as the phonemiser takes them.
        var words = SpokenWord.Tokenize(script);
        if (words.Count == 0) { problem = "the script has no words"; return null; }
        var groups = new List<List<SpokenWord>>();
        foreach (var word in words)
        {
            if (groups.Count > 0 && Joined(script, groups[^1][^1].Range.End, word.Range.Start)) groups[^1].Add(word);
            else groups.Add([word]);
        }
        if (groups.Count != spoken.Count)
        {
            problem = $"the phonemiser read {spoken.Count} words where the script has {groups.Count}";
            return null;
        }

        var cues = new List<TimedCue>(words.Count);
        for (int g = 0; g < groups.Count; g++)
        {
            var (first, count) = spoken[g];
            if (groups[g].Count == 1)
            {
                cues.Add(Cue(groups[g][0], phonemes, first, count));
                continue;
            }
            if (phonemise is null) { problem = $"'{Text(script, groups[g])}' is read as one word"; return null; }
            // Sounds only: stress can sit elsewhere in a word phonemised alone.
            var sounds = Enumerable.Range(first, count).Where(i => !IsStress(phonemes[i].Symbol)).ToList();
            int at = 0;
            foreach (var word in groups[g])
            {
                string own = new(phonemise(word.Text).Where(c => !IsStress(c) && !IsSeparator(c)).ToArray());
                if (own.Length == 0 || at + own.Length > sounds.Count
                    || !own.AsSpan().SequenceEqual(sounds.Skip(at).Take(own.Length).Select(i => phonemes[i].Symbol).ToArray()))
                {
                    problem = $"'{Text(script, groups[g])}' is read as one word, and its parts' own sounds are not found in it";
                    return null;
                }
                int from = at == 0 ? first : sounds[at], to = at + own.Length == sounds.Count ? first + count : sounds[at + own.Length];
                cues.Add(Cue(word, phonemes, from, to - from));
                at += own.Length;
            }
            if (at != sounds.Count) { problem = $"'{Text(script, groups[g])}' is read with sounds its parts do not have"; return null; }
        }
        for (int i = 0; i < cues.Count; i++)
        {
            if (cues[i].DurationSamples < TimedCueValidator.MinimumCueSamples || (i > 0 && cues[i].StartSample < cues[i - 1].EndSample))
            {
                problem = "the phoneme times are not in order";
                return null;
            }
        }
        return cues;
    }

    /// <summary>
    /// A word from its first phoneme to its last, and on through the space after it: the space
    /// between two words is the tail of the first, as a forced aligner places it (without it every
    /// word ended 40 ms early). A pause at punctuation belongs to no word.
    /// </summary>
    private static TimedCue Cue(SpokenWord word, IReadOnlyList<TimedPhoneme> phonemes, int first, int count)
    {
        int last = first + count - 1;
        while (last + 1 < phonemes.Count && char.IsWhiteSpace(phonemes[last + 1].Symbol)) last++;
        return new(phonemes[first].StartSample, phonemes[last].EndSample, word.Range, [], CueGranularity.Word, null, []);
    }

    private static bool Joined(string script, int from, int to)
    {
        if (to <= from) return false;
        for (int i = from; i < to; i++) if (!IsHyphen(script[i])) return false;
        return true;
    }

    private static string Text(string script, List<SpokenWord> group)
        => script[group[0].Range.Start..group[^1].Range.End];

    /// <summary>
    /// Phonemes with their samples from a model's per-token durations: token k lasts
    /// max(1, round(duration k)) frames, as the model's own length regulator counts them. Null
    /// when the frames do not add up to the clip, in which case the durations are not this clip's.
    /// </summary>
    /// <param name="symbols">The phoneme of each token, padding included (any symbol for a pad).</param>
    /// <param name="isPad">True for the padding tokens at each end, which are silence.</param>
    /// <param name="leadFrames">
    /// Frames by which the audio runs ahead of the durations' own count: Kokoro's reference
    /// implementation takes three off its leading pad, and against a forced aligner the count
    /// without that is 76 ms late on every word (ADR-0013).
    /// </param>
    public static IReadOnlyList<TimedPhoneme>? FromDurations(
        IReadOnlyList<char> symbols, IReadOnlyList<bool> isPad, ReadOnlySpan<float> durations, int frameSamples, long clipSamples, long offset,
        int leadFrames = 0)
    {
        if (symbols.Count != durations.Length || isPad.Count != durations.Length || frameSamples <= 0) return null;
        var result = new List<TimedPhoneme>(symbols.Count);
        long at = 0;
        for (int k = 0; k < durations.Length; k++)
        {
            if (!float.IsFinite(durations[k])) return null;
            long frames = Math.Max(1, (long)Math.Round(durations[k], MidpointRounding.ToEven));
            long next = at + frames * frameSamples;
            long lead = (long)leadFrames * frameSamples;
            if (!isPad[k]) result.Add(new TimedPhoneme(symbols[k], offset + Math.Max(0, at - lead), offset + Math.Max(0, next - lead)));
            at = next;
        }
        return at == clipSamples ? result : null;
    }
}
