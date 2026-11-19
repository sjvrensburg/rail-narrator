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
            Assert.Equal(TimingProvenance.None, caps.NativeTiming);

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
}
