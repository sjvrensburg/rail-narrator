using RailNarrator.Audio;
using RailNarrator.Domain;
using RailNarrator.Speech;
using RailNarrator.TestFixtures;
using Xunit;

namespace RailNarrator.Audio.Tests;

/// <summary>
/// Sample-rate conversion and the frame mapping the media clock depends on.
/// </summary>
public sealed class ResamplerTests
{
    [Fact]
    public void SameRate_IsAnExactCopy()
    {
        var input = new float[] { 0.1f, -0.2f, 0.3f, -0.4f };
        var output = Resampler.Resample(input, 24000, 24000);
        Assert.Equal(input, output);
    }

    [Fact]
    public void UpSampling_ProducesTheExactFrameCount()
    {
        // Kokoro's 24 kHz into a 44.1 kHz device: the ratio is not an integer, so a naive
        // implementation drifts. The mapping must be exact integer arithmetic.
        var input = new float[24000];
        var output = Resampler.Resample(input, 24000, 44100);
        Assert.Equal(Resampler.OutputFramesFor(24000, 24000, 44100), output.Length);
        Assert.Equal(44100, output.Length);
    }

    [Fact]
    public void FrameMapping_RoundTrips()
    {
        // A cue at source frame S must map to the same output frame the clock reports when that
        // source frame is presented, or highlights drift away from the audio.
        for (long source = 0; source < 24000; source += 1000)
        {
            long output = Resampler.OutputFrameForSource(source, 24000, 44100);
            long back = Resampler.SourceFrameForOutput(output, 24000, 44100);
            Assert.True(Math.Abs(back - source) <= 1, $"source {source} -> output {output} -> {back}");
        }
    }

    [Fact]
    public void PreservesDcLevel_WithoutPassbandRipple()
    {
        // A constant input must come out constant. Without normalising by the window sum, the
        // filter's passband ripple would audibly modulate speech loudness.
        var input = new float[8000];
        Array.Fill(input, 0.5f);
        var output = Resampler.Resample(input, 24000, 44100);

        // Ignore the filter's edge transient at both ends.
        var middle = output.AsSpan(64, output.Length - 128);
        foreach (float s in middle)
            Assert.True(Math.Abs(s - 0.5f) < 0.01f, $"DC level drifted to {s}");
    }

    [Fact]
    public void PreservesASineTonesFrequency()
    {
        // A 1 kHz tone must still be 1 kHz after conversion; a wrong ratio would shift the pitch.
        const int inRate = 24000, outRate = 44100, n = 24000;
        var input = new float[n];
        for (int i = 0; i < n; i++) input[i] = (float)Math.Sin(2 * Math.PI * 1000 * i / inRate);

        var output = Resampler.Resample(input, inRate, outRate);
        double peakHz = DominantFrequency(output, outRate);
        Assert.True(Math.Abs(peakHz - 1000) < 25, $"expected ~1000 Hz, measured {peakHz:F1} Hz");
    }

    [Fact]
    public void DownSampling_FiltersContentAboveTheDestinationNyquist()
    {
        // A 20 kHz tone at 48 kHz, converted down to 24 kHz whose Nyquist is 12 kHz. Without an
        // anti-alias filter the tone folds back to 4 kHz and appears as audible content that was
        // never in the source. The tone must be attenuated, not wrapped.
        const int n = 48000;
        var input = new float[n];
        for (int i = 0; i < n; i++) input[i] = (float)Math.Sin(2 * Math.PI * 20000 * i / 48000);

        var down = Resampler.Resample(input, 48000, 24000);
        var middle = down.AsSpan(512, down.Length - 1024).ToArray();
        double peak = middle.Max(Math.Abs);
        Assert.True(peak < 0.25, $"20 kHz was not filtered out on the way down; residual peak {peak:F3}");
    }

    [Fact]
    public void DownSampling_PreservesContentBelowTheDestinationNyquist()
    {
        // The companion case: a tone that is legitimately representable must survive, so the filter
        // is not simply discarding everything.
        const int n = 48000;
        var input = new float[n];
        for (int i = 0; i < n; i++) input[i] = (float)Math.Sin(2 * Math.PI * 1000 * i / 48000);

        var down = Resampler.Resample(input, 48000, 24000);
        var middle = down.AsSpan(512, down.Length - 1024).ToArray();
        Assert.True(middle.Max(Math.Abs) > 0.5, $"a 1 kHz tone was attenuated to {middle.Max(Math.Abs):F3}");
        Assert.True(Math.Abs(DominantFrequency(middle, 24000) - 1000) < 25);
    }

    internal static double DominantFrequency(float[] samples, int sampleRate)
    {
        // A coarse DFT over the first 4096 samples is enough to identify a single tone and avoids
        // taking a dependency on an FFT library for one test.
        int n = Math.Min(4096, samples.Length);
        double bestMag = -1; int bestBin = 0;
        int bins = n / 2;   // bin k is k*sampleRate/n Hz, so n/2 is Nyquist
        for (int k = 1; k < bins; k++)
        {
            double re = 0, im = 0;
            double freq = (double)k * sampleRate / n;
            for (int i = 0; i < n; i++)
            {
                double angle = 2 * Math.PI * freq * i / sampleRate;
                re += samples[i] * Math.Cos(angle);
                im -= samples[i] * Math.Sin(angle);
            }
            double mag = re * re + im * im;
            if (mag > bestMag) { bestMag = mag; bestBin = k; }
        }
        return (double)bestBin * sampleRate / n;
    }
}

/// <summary>
/// Pitch-preserving speed change. Changing the sample rate instead would raise the pitch and, worse,
/// silently break the source-to-output frame mapping the highlights are built on.
/// </summary>
public sealed class TimeStretchTests
{
    private const int Rate = 24000;

    private static float[] Tone(double freq, double seconds, int sampleRate = Rate)
    {
        var buffer = new float[(int)(seconds * sampleRate)];
        for (int i = 0; i < buffer.Length; i++)
            buffer[i] = (float)(0.4 * Math.Sin(2 * Math.PI * freq * i / sampleRate));
        return buffer;
    }

    [Fact]
    public void UnityRate_IsBitExact()
    {
        var input = Tone(220, 0.5);
        var stretcher = new WsolaTimeStretcher(Rate);
        Assert.Equal(input, stretcher.Process(input, 1.0));
    }

    [Fact]
    public void DoubleRate_HalvesTheDuration()
    {
        var input = Tone(220, 1.0);
        var output = new WsolaTimeStretcher(Rate).Process(input, 2.0);
        double ratio = output.Length / (double)input.Length;
        Assert.InRange(ratio, 0.45, 0.60);
    }

    [Fact]
    public void HalfRate_DoublesTheDuration()
    {
        var input = Tone(220, 1.0);
        var output = new WsolaTimeStretcher(Rate).Process(input, 0.5);
        double ratio = output.Length / (double)input.Length;
        Assert.InRange(ratio, 1.7, 2.3);
    }

    [Fact]
    public void PitchIsPreserved_NotShiftedByTheRateChange()
    {
        // This is the assertion that separates a time-stretcher from a sample-rate hack. At 2x speed
        // the audio is half as long but the dominant frequency must stay at 220 Hz.
        var input = Tone(220, 1.0);
        var output = new WsolaTimeStretcher(Rate).Process(input, 2.0);

        var body = output.AsSpan(Rate / 4, output.Length - Rate / 2).ToArray();
        double freq = ResamplerTests.DominantFrequency(body, Rate);
        // Tolerance is 10%. What this test is really separating is a time-stretcher from a
        // sample-rate hack: changing the rate would put the tone at 440 Hz, a 100% error, which no
        // plausible overlap-add artifact reaches.
        Assert.True(Math.Abs(freq - 220) / 220 < 0.10,
            $"pitch moved to {freq:F1} Hz ({(freq - 220) / 220 * 100:F1}%); a rate change must not do that");
    }

    [Fact]
    public void RateIsClampedToTheSupportedRange()
    {
        var stretcher = new WsolaTimeStretcher(Rate);
        var input = Tone(220, 0.2);
        // Absurd rates must not produce silence or garbage: they clamp.
        Assert.NotEmpty(stretcher.Process(input, 100));
        Assert.NotEmpty(stretcher.Process(input, 0.001));
    }
}

/// <summary>
/// The playback engine and its media clock, driven by a device whose clock the test controls.
/// </summary>
public sealed class PlaybackEngineTests
{
    private static (AudioPlaybackEngine Engine, FakeAudioOutputDevice Device, SpeechAsset Asset) Load(
        long frames = 24000, int sourceRate = 24000, int deviceRate = 44100)
    {
        var device = new FakeAudioOutputDevice(new AudioFormat(deviceRate, 1, SampleFormat.Float32LittleEndian))
        {
            Devices = [new AudioDeviceInfo("0", "Fake", true, deviceRate, 1, 0.01)],
        };
        var engine = new AudioPlaybackEngine(device, new AudioEngineOptions(PreferredDeviceRate: deviceRate));
        var asset = SyntheticNarration.Clip("s0", frames, sourceRate);
        engine.LoadAsync("s0", asset, SyntheticNarration.TonePcm(frames, sourceRate)).GetAwaiter().GetResult();
        return (engine, device, asset);
    }

    [Fact]
    public async Task ResamplesFromTheClipRateToTheDeviceRate()
    {
        // Kokoro produces 24 kHz; the devices on the reference machine run at 44.1 kHz and PortAudio's
        // ALSA host refuses to open a stream at any other rate. The engine must convert.
        var (engine, device, asset) = Load(frames: 24000, sourceRate: 24000, deviceRate: 44100);
        await using (engine)
        {
            Assert.Equal(24000, engine.SourceSampleRate);
            Assert.Equal(44100, engine.CurrentFormat!.Value.SampleRate);
            await engine.PlayAsync();
            await WaitForQueuedAsync(engine, device, 44100);
            device.AdvanceByFrames(44100);   // one second of device audio
            // One device-second at rate 1.0 is one source-second, whatever the rate conversion.
            Assert.Equal(1.0, engine.PositionSeconds, 2);
        }
    }

    [Fact]
    public async Task PositionIsAMediaPosition_NotWallClockTime()
    {
        var (engine, device, _) = Load();
        await using (engine)
        {
            await engine.PlayAsync();
            // No device time passes, so no media time may pass either - however long we wait.
            await Task.Delay(150);
            Assert.Equal(0.0, engine.PositionSeconds, 3);

            device.AdvanceBySeconds(0.5);
            Assert.Equal(0.5, engine.PositionSeconds, 2);

            await Task.Delay(150);
            Assert.Equal(0.5, engine.PositionSeconds, 2);   // still stationary
        }
    }

    [Fact]
    public async Task PlaybackRate_ScalesTheMediaPosition()
    {
        // 48000 frames at 24 kHz is 2.0 s of source audio.
        var (engine, device, _) = Load(frames: 48000);
        await using (engine)
        {
            engine.SetPlaybackRate(2.0);
            await engine.PlayAsync();

            // At 2x the clip plays out in about one device-second. WSOLA's output length is not
            // exact to the sample, so the assertions are proportional rather than exact.
            Assert.Equal(2.0, engine.ClipDurationSeconds, 1);

            await WaitForQueuedAsync(engine, device, 22050);
            device.AdvanceBySeconds(0.5);
            // Half a device-second at 2x is one source-second of the clip.
            Assert.Equal(1.0, engine.PositionSeconds, 1);

            // Drain until the writer has finished and the engine reports completion. Draining once and
            // waiting a fixed time is racy: the writer may still have frames to queue.
            string? completed = null;
            engine.ClipCompleted += _ => completed = "done";
            await WaitUntil(() => { device.DrainAll(); return completed is not null; }, "clip completion");

            // At completion the clock must equal the clip's own duration - not approximately 2.0,
            // because WSOLA's output length is not exact to the sample.
            Assert.Equal(engine.ClipDurationSeconds, engine.PositionSeconds, 3);
            Assert.InRange(engine.ClipDurationSeconds, 1.8, 2.2);
        }
    }

    [Fact]
    public async Task Pause_FreezesTheClock_AndResumeContinuesIt()
    {
        var (engine, device, _) = Load();
        await using (engine)
        {
            await engine.PlayAsync();
            await WaitForQueuedAsync(engine, device, 17640);
            device.AdvanceBySeconds(0.4);
            await engine.PauseAsync();
            Assert.Equal(PlaybackState.Paused, engine.State);

            double frozen = engine.PositionSeconds;
            device.AdvanceBySeconds(0.4);          // a paused device must not present more
            Assert.Equal(frozen, engine.PositionSeconds, 3);

            await engine.ResumeAsync();
            Assert.Equal(PlaybackState.Playing, engine.State);
            await WaitForQueuedAsync(engine, device, 8820);
            device.AdvanceBySeconds(0.2);
            Assert.True(engine.PositionSeconds > frozen);
        }
    }

    [Fact]
    public async Task Seek_FlushesStaleAudioAndRepositions()
    {
        var (engine, device, _) = Load(frames: 48000);
        await using (engine)
        {
            await engine.PlayAsync();
            await WaitForQueuedAsync(engine, device, 22050);
            device.AdvanceBySeconds(0.5);
            int flushesBefore = device.FlushCount;

            await engine.SeekAsync(1.5);

            Assert.True(device.FlushCount > flushesBefore,
                "a seek must flush the device, or audio from the old position plays over the new one");
            Assert.Equal(1.5, engine.PositionSeconds, 1);
        }
    }

    [Fact]
    public async Task ClipCompletion_FiresOnlyAfterTheDeviceDrains()
    {
        var (engine, device, asset) = Load(frames: 24000);
        await using (engine)
        {
            string? completed = null;
            engine.ClipCompleted += id => completed = id;

            await engine.PlayAsync();
            await WaitForQueuedAsync(engine, device, 22050);
            device.AdvanceBySeconds(0.5);
            await Task.Delay(100);
            Assert.Null(completed);      // audio is still being presented

            device.DrainAll();
            await WaitUntil(() => completed is not null, "clip completion");
            Assert.Equal("s0", completed);
            Assert.Equal(PlaybackState.Completed, engine.State);
            _ = asset;
        }
    }

    [Fact]
    public async Task DeviceFailure_IsReportedAsAnError()
    {
        var device = new FakeAudioOutputDevice { FailOpenWith = "device busy" };
        var engine = new AudioPlaybackEngine(device, new AudioEngineOptions(PreferredDeviceRate: 44100));
        await using (engine)
        {
            string? failure = null;
            engine.DeviceFailed += detail => failure = detail;

            var asset = SyntheticNarration.Clip("s0", 24000);
            await Assert.ThrowsAsync<InvalidOperationException>(
                () => engine.LoadAsync("s0", asset, SyntheticNarration.TonePcm(24000)));
            Assert.Equal("device busy", failure);
        }
    }

    [Fact]
    public async Task ClockDiagnostics_ReportsQueueDepthAndResidualError()
    {
        var (engine, device, _) = Load(frames: 48000);
        await using (engine)
        {
            await engine.PlayAsync();
            await WaitForQueuedAsync(engine, device, 22050);
            device.AdvanceBySeconds(0.5);
            await Task.Delay(120);   // let the writer queue ahead

            var diag = engine.GetClockDiagnostics();
            Assert.Equal(24000, diag.SourceSampleRate);
            Assert.Equal(44100, diag.OutputSampleRate);
            Assert.True(diag.PresentedOutputFrames > 0);
            // Written is always at least presented; the difference is the latency the clock excludes.
            Assert.True(diag.WrittenOutputFrames >= diag.PresentedOutputFrames);
            Assert.True(diag.PositionInSourceSeconds >= 0.4);
        }
    }

    /// <summary>
    /// Waits until the engine's writer thread has queued enough frames for the test to consume. The
    /// fake device only advances frames that have actually been written, so advancing first would
    /// measure nothing.
    /// </summary>
    private static async Task WaitForQueuedAsync(AudioPlaybackEngine engine, FakeAudioOutputDevice device, long frames, int timeoutMs = 5000)
    {
        var deadline = Environment.TickCount64 + timeoutMs;
        while (Environment.TickCount64 < deadline)
        {
            if (device.TotalWritten - device.PresentedFrames >= frames) return;
            await Task.Delay(5);
        }
        Assert.Fail($"Timed out waiting for {frames} frames to be queued (written={device.TotalWritten}, presented={device.PresentedFrames}).");
    }

    private static async Task WaitUntil(Func<bool> condition, string what, int timeoutMs = 3000)
    {
        var deadline = Environment.TickCount64 + timeoutMs;
        while (Environment.TickCount64 < deadline)
        {
            if (condition()) return;
            await Task.Delay(10);
        }
        Assert.Fail($"Timed out waiting for {what}.");
    }
}
