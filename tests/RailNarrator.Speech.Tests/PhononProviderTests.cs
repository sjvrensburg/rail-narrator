using RailNarrator.Domain;
using RailNarrator.Speech;
using Xunit;

namespace RailNarrator.Speech.Tests;

/// <summary>
/// Phonon through Gradium's C library (ADR-0009). The real-runtime tests run only with
/// RAIL_NARRATOR_PHONON_DIR set to an install made by tools/setup-phonon.sh.
/// </summary>
public sealed class PhononProviderTests
{
    private static PhononTtsProvider? Installed()
    {
        string? dir = Environment.GetEnvironmentVariable("RAIL_NARRATOR_PHONON_DIR");
        if (dir is null) return null;
        string library = Path.Combine(dir, "libptts_ffi.so"), model = Path.Combine(dir, "phonon-31m-en");
        return PhononTtsProvider.IsInstalled(library, model)
            ? new PhononTtsProvider(new PhononProviderSettings(library, model))
            : null;
    }

    [Fact]
    public async Task NotInstalled_ReportsWhyAndDoesNotThrow()
    {
        string missing = Path.Combine(Path.GetTempPath(), "rn-no-phonon-" + Guid.NewGuid().ToString("N"));
        await using var tts = new PhononTtsProvider(new PhononProviderSettings(
            Path.Combine(missing, "libptts_ffi.so"), Path.Combine(missing, "phonon-31m-en")));

        Assert.False(PhononTtsProvider.IsInstalled(Path.Combine(missing, "libptts_ffi.so"), missing));
        var caps = await tts.GetCapabilitiesAsync();
        Assert.False(caps.IsAvailable);
        Assert.Contains("setup-phonon.sh", caps.Unavailable);
        Assert.Empty(await tts.ListVoicesAsync());
        var test = await tts.SelfTestAsync(PhononTtsProvider.DefaultVoice);
        Assert.False(test.Succeeded);
    }

    [Fact]
    public async Task RealRuntime_SpeaksInMaeveByDefault_At24kHz()
    {
        var tts = Installed();
        if (tts is null) return;   // skipped without an install
        await using (tts)
        {
            var caps = await tts.GetCapabilitiesAsync();
            Assert.True(caps.IsAvailable, caps.Unavailable);
            // None from Gradium's library as released; its own attention with tools/phonon-word-timing.patch.
            Assert.Contains(caps.NativeTiming, new[] { TimingProvenance.None, TimingProvenance.ModelAttention });

            var voices = await tts.ListVoicesAsync();
            Assert.Equal(PhononTtsProvider.DefaultVoice, Assert.Single(voices, v => v.IsDefault).Id);
            Assert.Equal("en-GB", voices[0].Language);   // British voices first
            Assert.DoesNotContain(voices, v => v.Id == "default");

            var result = await tts.SynthesizeAsync(new TtsSynthesisRequest(
                "The estimator is consistent under mild conditions.", PhononTtsProvider.DefaultVoice, "en-GB", AudioFormat.Mono24000Float32));
            Assert.Equal(24000, result.Format.SampleRate);
            Assert.InRange(result.DurationSeconds, 1.5, 8);
            Assert.True(Pcm.Peak(result.Pcm.Span, result.Format) > 0.05);
            Assert.Empty(result.Warnings);

            await Assert.ThrowsAsync<InvalidOperationException>(() => tts.SynthesizeAsync(
                new TtsSynthesisRequest("Hello.", "Maeve", "en-GB", new AudioFormat(44100, 1, SampleFormat.Float32LittleEndian))));
        }
    }

    [Fact]
    public async Task RealRuntime_AnotherEnginesVoice_SpeaksInTheDefaultWithAWarning()
    {
        var tts = Installed();
        if (tts is null) return;
        await using (tts)
        {
            var result = await tts.SynthesizeAsync(new TtsSynthesisRequest(
                "Hello there.", "bf_isabella", "en-GB", new AudioFormat(24000, 1, SampleFormat.Int16LittleEndian)));
            Assert.True(result.DurationSeconds > 0.3);
            Assert.Contains(result.Warnings, w => w.Contains("Maeve"));
        }
    }

    [Fact]
    public async Task RealRuntime_Cancellation_StopsAndThrows()
    {
        var tts = Installed();
        if (tts is null) return;
        await using (tts)
        {
            await tts.GetCapabilitiesAsync();
            using var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(100));
            string text = string.Join(" ", Enumerable.Repeat("This sentence is long enough to take a while to say aloud.", 40));
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => tts.SynthesizeAsync(
                new TtsSynthesisRequest(text, "Maeve", "en-GB", AudioFormat.Mono24000Float32), cts.Token));

            // Still usable afterwards.
            var result = await tts.SynthesizeAsync(new TtsSynthesisRequest("Done.", "Maeve", "en-GB", AudioFormat.Mono24000Float32));
            Assert.True(result.DurationSeconds > 0.2);
        }
    }

    /// <summary>
    /// With the library built by tools/setup-phonon.sh (its word-timing patch applied), the real
    /// model times every word of what it speaks from its own attention: no aligner is involved.
    /// </summary>
    [Fact]
    public async Task RealPhonon_TimesEveryWordFromItsOwnAttention()
    {
        await using var tts = Installed();
        if (tts is null) return;
        var caps = await tts.GetCapabilitiesAsync();
        Assert.True(caps.IsAvailable, caps.Unavailable);
        if (caps.NativeTiming != TimingProvenance.ModelAttention) return;   // an older library, built without the patch

        // Two chunks' worth of text, with the leading space a caller may leave.
        const string text = " The conditional variance of the returns rises sharply after a large negative shock, and then decays slowly. " +
                            "Our results suggest that the penalty selects the correct structure in most of the simulated samples, even when the number of assets is large.";
        var result = await tts.SynthesizeAsync(new TtsSynthesisRequest(text, PhononTtsProvider.DefaultVoice, "en-GB", AudioFormat.Mono24000Float32));

        Assert.Equal(TimingProvenance.ModelAttention, result.TimingProvenance);
        var words = SpokenWord.Tokenize(text);
        Assert.Equal(words.Select(w => w.Range), result.Timings.Select(c => c.SpokenRange!.Value));
        Assert.All(result.Timings, c => Assert.Equal(CueGranularity.Word, c.Granularity));
        Assert.True(result.Timings.Zip(result.Timings.Skip(1)).All(p => p.First.EndSample <= p.Second.StartSample));
        Assert.InRange(result.Timings[0].StartSample, 0, 12000);                       // the first word within half a second
        Assert.InRange(result.Timings[^1].EndSample, result.FrameCount - 36000, result.FrameCount);
        // A word lasts a plausible time: none shorter than 20 ms, none longer than two seconds.
        Assert.All(result.Timings, c => Assert.InRange(c.EndSample - c.StartSample, 480, 48000));

        var asked = await tts.SynthesizeAsync(new TtsSynthesisRequest("No timings were asked for here.", PhononTtsProvider.DefaultVoice, "en-GB",
            AudioFormat.Mono24000Float32, RequestTimings: false));
        Assert.Empty(asked.Timings);
        Assert.Equal(TimingProvenance.None, asked.TimingProvenance);
    }
}
