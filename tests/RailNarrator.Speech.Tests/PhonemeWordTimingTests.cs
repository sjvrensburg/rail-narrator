using RailNarrator.Domain;
using RailNarrator.Speech;
using Xunit;

namespace RailNarrator.Speech.Tests;

/// <summary>
/// Word timing from a model's own phoneme durations: the script's words counted off against the
/// words of the phoneme string, each taking the samples of its phonemes.
/// </summary>
public sealed class PhonemeWordTimingTests
{
    /// <summary>A phoneme string as a run of phonemes, <paramref name="each"/> samples apiece, from <paramref name="at"/>.</summary>
    private static List<TimedPhoneme> Spoken(string phonemes, int each = 600, long at = 1200)
    {
        var list = new List<TimedPhoneme>();
        foreach (char c in phonemes) { list.Add(new TimedPhoneme(c, at, at + each)); at += each; }
        return list;
    }

    private static string Word(string script, TimedCue cue) => script.Substring(cue.SpokenRange!.Value.Start, cue.SpokenRange.Value.Length);

    [Fact]
    public void EachWordTakesTheSamplesOfItsPhonemes_AndPausesBelongToNoWord()
    {
        const string script = "The dog, then, barked.";
        //                    ðə dˈɒɡ, ðˈɛn, bˈɑːkt.
        var cues = PhonemeWordTiming.Words(script, Spoken("ðə dˈɒɡ, ðˈɛn, bˈɑːkt."), null, out string? problem);

        Assert.Null(problem);
        Assert.Equal(["The", "dog", "then", "barked"], cues!.Select(c => Word(script, c)));
        Assert.Equal((1200L, 3000L), (cues![0].StartSample, cues[0].EndSample));         // ð ə and the space after
        Assert.Equal((3000L, 5400L), (cues[1].StartSample, cues[1].EndSample));          // d ˈ ɒ ɡ, not the comma's pause
        Assert.Equal(6600L, cues[2].StartSample);                                        // after the comma and its space
        Assert.All(cues, c => Assert.Equal(CueGranularity.Word, c.Granularity));
        Assert.True(cues.Zip(cues.Skip(1)).All(p => p.First.EndSample <= p.Second.StartSample));
    }

    [Fact]
    public void ACompoundReadAsOneWord_IsDividedWhereItsFirstPartsSoundsEnd()
    {
        const string script = "a vine-based model";
        // The phonemiser glues the compound; alone, "vine" is vˈaɪn and "based" is bˈeɪst.
        var cues = PhonemeWordTiming.Words(script, Spoken("ɐ vˈaɪnbˌeɪst mˈɒdəl"),
            part => part switch { "vine" => "vˈaɪn", "based" => "bˈeɪst", _ => "?" }, out string? problem);

        Assert.Null(problem);
        Assert.Equal(["a", "vine", "based", "model"], cues!.Select(c => Word(script, c)));
        // v ˈ a ɪ n | b ˌ e ɪ s t : "based" begins at the b, whatever stress mark follows it.
        Assert.Equal(cues![1].EndSample, cues[2].StartSample);
        Assert.Equal(1200 + 2 * 600 + 5 * 600, cues[2].StartSample);
    }

    [Fact]
    public void ACompoundWhosePartsAreNotInIt_GivesNoTiming()
    {
        var cues = PhonemeWordTiming.Words("a vine-based model", Spoken("ɐ vˈaɪmbeɪst mˈɒdəl"),
            part => part == "vine" ? "vˈaɪn" : "bˈeɪst", out string? problem);

        Assert.Null(cues);
        Assert.Contains("read as one word", problem, StringComparison.Ordinal);
    }

    [Fact]
    public void WhenThePhonemiserReadMoreOrFewerWords_ThereIsNoTiming()
    {
        var cues = PhonemeWordTiming.Words("see e.g. this", Spoken("sˈiː fɔːɹ ɛɡzˈæmpəl ðˈɪs"), null, out string? problem);

        // Four words spoken for the script's four tokens ("see", "e", "g", "this"): the count agrees
        // by luck here, which is why the count is the least that is checked, not all of it.
        Assert.NotNull(cues);
        Assert.Null(PhonemeWordTiming.Words("one two three", Spoken("wˈʌn tˈuː"), null, out problem));
        Assert.Contains("read 2 words where the script has 3", problem, StringComparison.Ordinal);
    }

    [Fact]
    public void DurationsBecomePhonemeTimes_FramesRoundedAsTheModelRoundsThem()
    {
        // pad, three phonemes, pad; 2.4 -> 2 frames, 0.3 -> at least 1, 2.5 -> 2 (to even).
        float[] durations = [12.2f, 2.4f, 0.3f, 2.5f, 1.0f];
        long clip = (12 + 2 + 1 + 2 + 1) * 600;

        var phonemes = PhonemeWordTiming.FromDurations(['$', 'a', 'b', 'c', '$'], [true, false, false, false, true], durations, 600, clip, offset: 1000);

        Assert.NotNull(phonemes);
        Assert.Equal("abc", new string(phonemes.Select(p => p.Symbol).ToArray()));
        Assert.Equal((1000L + 12 * 600, 1000L + 14 * 600), (phonemes[0].StartSample, phonemes[0].EndSample));
        Assert.Equal(1000L + 15 * 600, phonemes[2].StartSample);

        // The audio runs three frames ahead of that count in Kokoro: every time moves, none below the clip's start.
        var led = PhonemeWordTiming.FromDurations(['$', 'a', 'b', 'c', '$'], [true, false, false, false, true], durations, 600, clip, offset: 1000, leadFrames: 3);
        Assert.Equal(phonemes.Select(p => p.StartSample - 1800), led!.Select(p => p.StartSample));
        var early = PhonemeWordTiming.FromDurations(['a', '$'], [false, true], [2f, 1f], 600, 1800, offset: 0, leadFrames: 3);
        Assert.Equal(0, early![0].StartSample);
    }

    [Fact]
    public void DurationsThatDoNotAddUpToTheClip_AreNotUsed()
    {
        Assert.Null(PhonemeWordTiming.FromDurations(['a', 'b'], [false, false], [2f, 3f], 600, 5 * 600 + 1, 0));
        Assert.Null(PhonemeWordTiming.FromDurations(['a', 'b'], [false, false], [2f, float.NaN], 600, 3000, 0));
        Assert.Null(PhonemeWordTiming.FromDurations(['a'], [false, false], [2f, 3f], 600, 3000, 0));
    }

    /// <summary>
    /// The real timestamped export (onnx-community/Kokoro-82M-v1.0-ONNX-timestamped), which names
    /// its phoneme ids "input_ids" and has a durations output: run in this application's own
    /// session, it times every word. Opt-in: RAIL_NARRATOR_KOKORO_TIMED_MODEL.
    /// </summary>
    [Fact]
    public async Task RealKokoroWithDurations_TimesEveryWord()
    {
        string? model = Environment.GetEnvironmentVariable("RAIL_NARRATOR_KOKORO_TIMED_MODEL");
        if (model is null || !File.Exists(model)) return;
        await using var tts = new KokoroTtsProvider(new KokoroProviderSettings(model, DefaultVoice: "bf_isabella"));
        var caps = await tts.GetCapabilitiesAsync();
        Assert.True(caps.IsAvailable, caps.Unavailable);
        Assert.Equal(TimingProvenance.NativeDuration, caps.NativeTiming);
        Assert.StartsWith("kokoro-session/", caps.ModelRevision, StringComparison.Ordinal);

        const string text = " In this section, we first describe the vine-based partial correlation framework, and then its estimation.";
        var result = await tts.SynthesizeAsync(new TtsSynthesisRequest(text, "bf_isabella", "en-GB", AudioFormat.Mono24000Float32));

        Assert.Equal(TimingProvenance.NativeDuration, result.TimingProvenance);
        Assert.Equal(SpokenWord.Tokenize(text).Select(w => w.Range), result.Timings.Select(c => c.SpokenRange!.Value));
        Assert.True(result.Timings.Zip(result.Timings.Skip(1)).All(p => p.First.EndSample <= p.Second.StartSample));
        Assert.InRange(result.Timings[^1].EndSample, result.FrameCount - 24000, result.FrameCount);
        Assert.True(Pcm.Peak(result.Pcm.Span, result.Format) > 0.05f);
    }
}
