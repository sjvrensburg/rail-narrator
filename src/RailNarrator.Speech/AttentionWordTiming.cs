using System.Text;
using RailNarrator.Domain;

namespace RailNarrator.Speech;

/// <summary>
/// Word timing read from a speech model's own attention while it spoke: no aligner, no second
/// model, and no guess.
/// </summary>
/// <remarks>
/// <para>
/// A model that is given its text first and then generates audio frame by frame has, in practice,
/// one attention head that follows the text: at each frame nearly all of its attention sits on
/// the token being spoken. For Phonon 31M that is layer 3, head 0 (found independently by
/// blixt/phonon, and measured here: 96 % of its attention on the text, correlation 0.995 with
/// time). Reading that head gives, for every 80 ms frame, a position in the text; a word starts
/// where the position passes it.
/// </para>
/// <para>
/// The constants are fitted to the forced aligner this application has used (MFA), not to the
/// published recipe, which runs about 40 ms late against it: on 504 words in one voice,
/// cross-validated, word starts are a mean 44 ms and a median 30 ms from the aligner's, 89 %
/// within 100 ms, with no bias; the same constants on two other voices give 41 and 44 ms (ADR-0013).
/// The head works in 80 ms frames, so this is coarser than forced alignment and is labelled as
/// what it is (<see cref="TimingProvenance.ModelAttention"/>).
/// </para>
/// <para>
/// Nothing here is interpolated by character count. A word the engine's text and the script do
/// not share, a head that does not reach the end of the text or does not follow it: each gives no
/// timing at all and the reason, and the passage is highlighted whole.
/// </para>
/// </remarks>
public static class AttentionWordTiming
{
    /// <summary>One chunk of text as the model was given it, with the head's attention per frame.</summary>
    /// <param name="Text">The chunk's text after the engine's own preparation.</param>
    /// <param name="TokenEnds">For each token, the UTF-8 byte offset in <paramref name="Text"/> where it ends.</param>
    /// <param name="Frames">For each generated frame, the head's attention over the chunk's tokens.</param>
    public sealed record Chunk(string Text, IReadOnlyList<int> TokenEnds, IReadOnlyList<float[]> Frames);

    /// <summary>A word starts when the head's position is this far into its first token.</summary>
    public const double Level = 0.375;

    /// <summary>Frames by which the audio trails the head at that point.</summary>
    public const double LagFrames = 0.375;

    /// <summary>A start that falls where the audio is quieter than this moves to where sound begins.</summary>
    public const double StartSilenceDb = -60;

    /// <summary>A word followed by a pause ends where the audio falls below this.</summary>
    public const double EndSilenceDb = -45;

    /// <summary>Bump when the constants or the method change: timings are stored with the audio.</summary>
    public const string Version = "attention/1";

    private const double MinimumWordSeconds = 0.02;

    /// <summary>The loudness of mono audio in decibels, one value for each 10 ms.</summary>
    public static double[] Envelope(ReadOnlySpan<float> samples, int sampleRate)
    {
        int hop = Math.Max(1, sampleRate / 100);
        var db = new double[samples.Length / hop];
        for (int i = 0; i < db.Length; i++)
        {
            double sum = 0;
            foreach (float v in samples.Slice(i * hop, hop)) sum += (double)v * v;
            db[i] = 20 * Math.Log10(Math.Sqrt(sum / hop) + 1e-9);
        }
        return db;
    }

    /// <summary>
    /// The script's words with their sample intervals, or null with the reason in
    /// <paramref name="problem"/>. Ranges are those of <see cref="SpokenWord.Tokenize"/> on
    /// <paramref name="script"/>, which is the text the engine was asked to speak.
    /// </summary>
    /// <param name="envelope">Loudness of the clip, 10 ms a value (<see cref="Envelope"/>).</param>
    /// <param name="frameSamples">Samples of audio in one generated frame.</param>
    /// <param name="totalSamples">The clip's length.</param>
    public static IReadOnlyList<TimedCue>? Words(
        string script, IReadOnlyList<Chunk> chunks, IReadOnlyList<double> envelope,
        int sampleRate, int frameSamples, long totalSamples, out string? problem)
    {
        ArgumentNullException.ThrowIfNull(script);
        ArgumentNullException.ThrowIfNull(chunks);
        ArgumentNullException.ThrowIfNull(envelope);
        problem = null;
        if (sampleRate <= 0 || frameSamples <= 0 || totalSamples <= 0) { problem = "there is no audio"; return null; }
        double frame = (double)frameSamples / sampleRate;

        // The engine's text as a run of letters and digits, each with the start of its token.
        var stream = new List<(char Letter, double Seconds)>();
        int frameOffset = 0;
        foreach (var chunk in chunks)
        {
            int n = chunk.TokenEnds.Count;
            if (n == 0 || chunk.Frames.Count == 0) { problem = "a chunk of the text has no frames"; return null; }
            var position = Position(chunk.Frames, n, out double peak);
            if (position[^1] < n - 2.5) { problem = "the model's attention did not reach the end of the text"; return null; }
            if (peak < 0.4) { problem = "the model's attention is not following the text"; return null; }

            var starts = new double[n];
            for (int k = 0; k < n; k++) starts[k] = (Crossing(position, k + Level) + LagFrames + frameOffset) * frame;

            int bytes = 0, token = 0;
            foreach (var rune in chunk.Text.EnumerateRunes())
            {
                while (token < n - 1 && chunk.TokenEnds[token] <= bytes) token++;
                if (Rune.IsLetterOrDigit(rune))
                    foreach (char c in rune.ToString().ToLowerInvariant()) stream.Add((c, starts[token]));
                bytes += rune.Utf8SequenceLength;
            }
            frameOffset += chunk.Frames.Count;
        }

        // The script's words, found in that run in order. The engine may change case and
        // punctuation; if it changed a word, its timing is not the script's.
        var words = SpokenWord.Tokenize(script);
        var times = new double[words.Count];
        int at = 0;
        for (int w = 0; w < words.Count; w++)
        {
            bool first = true;
            foreach (var rune in words[w].Text.EnumerateRunes())
            {
                if (!Rune.IsLetterOrDigit(rune)) continue;
                foreach (char c in rune.ToString().ToLowerInvariant())
                {
                    if (at >= stream.Count || stream[at].Letter != c) { problem = "the engine spoke other words than the script"; return null; }
                    if (first) { times[w] = stream[at].Seconds; first = false; }
                    at++;
                }
            }
            if (first) { problem = "the script has a word with no letter or digit"; return null; }
        }
        if (words.Count == 0 || at != stream.Count) { problem = "the engine spoke other words than the script"; return null; }

        double clip = (double)totalSamples / sampleRate;
        for (int w = 0; w < times.Length; w++)
        {
            double t = times[w];
            int q = (int)(t * 100);
            while (q < envelope.Count && envelope[q] < StartSilenceDb) q++;
            if (q < envelope.Count) t = Math.Max(t, q / 100.0);
            if (w > 0) t = Math.Max(t, times[w - 1] + MinimumWordSeconds);
            times[w] = Math.Min(t, clip);
        }

        var cues = new List<TimedCue>(words.Count);
        for (int w = 0; w < times.Length; w++)
        {
            double start = times[w], next = w + 1 < times.Length ? times[w + 1] : clip;
            // A word ends where the next begins, or where the sound stops when a pause follows.
            int c = Math.Min((int)(next * 100), envelope.Count - 1);
            while (c > (int)(start * 100) + 5 && envelope[c - 1] < EndSilenceDb) c--;
            double end = c < (int)(next * 100) ? c / 100.0 : next;
            end = Math.Min(Math.Min(Math.Max(end, start + MinimumWordSeconds), Math.Max(next, start + MinimumWordSeconds)), clip);
            long from = (long)Math.Round(start * sampleRate, MidpointRounding.ToEven), to = (long)Math.Round(end * sampleRate, MidpointRounding.ToEven);
            if (to - from < TimedCueValidator.MinimumCueSamples) { problem = "a word has no time of its own"; return null; }
            cues.Add(new TimedCue(from, to, words[w].Range, [], CueGranularity.Word, null, []));
        }
        return cues;
    }

    /// <summary>
    /// The head's position in the text at each frame: around the token it attends most, the mean
    /// index weighted by attention over that token and its neighbours. It never moves back.
    /// </summary>
    internal static double[] Position(IReadOnlyList<float[]> frames, int tokens, out double meanPeak)
    {
        var position = new double[frames.Count];
        double best = double.NegativeInfinity, peaks = 0;
        for (int f = 0; f < frames.Count; f++)
        {
            var row = frames[f];
            int n = Math.Min(tokens, row.Length), k = 0;
            double total = 0;
            for (int i = 0; i < n; i++) { total += row[i]; if (row[i] > row[k]) k = i; }
            int lo = Math.Max(0, k - 1), hi = Math.Min(n, k + 2);
            double mass = 0, weighted = 0;
            for (int i = lo; i < hi; i++) { mass += row[i]; weighted += row[i] * (double)i; }
            best = Math.Max(best, mass > 0 ? weighted / mass : k);
            position[f] = best;
            peaks += n > 0 ? row[k] / Math.Max(total, 1e-9) : 0;
        }
        meanPeak = frames.Count == 0 ? 0 : peaks / frames.Count;
        return position;
    }

    /// <summary>The frame, with its fraction, at which the position first reaches <paramref name="level"/>.</summary>
    internal static double Crossing(double[] position, double level)
    {
        for (int f = 0; f < position.Length; f++)
        {
            if (position[f] < level) continue;
            return f > 0 && position[f] > position[f - 1]
                ? f - 1 + (level - position[f - 1]) / (position[f] - position[f - 1])
                : f;
        }
        return position.Length;
    }
}
