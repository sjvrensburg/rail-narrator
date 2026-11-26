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

    [Theory]
    [InlineData(24000, 48000)]
    [InlineData(24000, 44100)]
    [InlineData(44100, 24000)]
    [InlineData(22050, 48000)]
    public void PolyphaseFilter_MatchesTheDirectSincFilter(int inputRate, int outputRate)
    {
        // The fast path precomputes one weight set per fractional phase. It must be the same filter,
        // edges included, as evaluating the windowed sinc at every output frame.
        var random = new Random(7);
        var input = new float[inputRate / 5];
        for (int i = 0; i < input.Length; i++) input[i] = (float)(random.NextDouble() * 2 - 1) * 0.5f;

        var fast = Resampler.Resample(input, inputRate, outputRate);
        var reference = DirectResample(input, inputRate, outputRate, Resampler.DefaultHalfTaps);

        Assert.Equal(reference.Length, fast.Length);
        double worst = 0;
        for (int i = 0; i < fast.Length; i++) worst = Math.Max(worst, Math.Abs(fast[i] - reference[i]));
        Assert.True(worst < 1e-5, $"largest difference {worst:E2}");
    }

    private static float[] DirectResample(float[] input, int inputRate, int outputRate, int halfTaps)
    {
        double cutoff = Math.Min(1.0, (double)outputRate / inputRate) * 0.95;
        double step = (double)inputRate / outputRate;
        var output = new float[Resampler.OutputFramesFor(input.Length, inputRate, outputRate)];
        var window = Resampler.BuildKaiserWindow(halfTaps);
        for (int n = 0; n < output.Length; n++)
        {
            double srcPos = n * step;
            int centre = (int)Math.Floor(srcPos);
            double sum = 0, weightSum = 0;
            for (int k = -halfTaps + 1; k <= halfTaps; k++)
            {
                int index = centre + k;
                if (index < 0 || index >= input.Length) continue;
                double w = Resampler.Sinc((index - srcPos) * cutoff) * cutoff * window[k + halfTaps - 1];
                sum += input[index] * w;
                weightSum += w;
            }
            output[n] = weightSum > 1e-9 ? (float)(sum / weightSum) : 0f;
        }
        return output;
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

    [Theory]
    [InlineData(0.75)]
    [InlineData(1.25)]
    [InlineData(1.5)]
    public void FastGrainSearch_ChoosesTheSameGrainsAsTheDirectSearch(double rate)
    {
        // A voiced, noisy signal with a slowly gliding pitch, so the correlation peak is real but
        // not trivially periodic. The optimised search (sliding energy, SIMD dot product) must
        // render what the direct per-candidate search rendered.
        const int sampleRate = 48000;
        var random = new Random(11);
        var input = new float[sampleRate * 2];
        double phase = 0;
        for (int i = 0; i < input.Length; i++)
        {
            phase += 2 * Math.PI * (140 + 40 * Math.Sin(i * 2.0 / sampleRate)) / sampleRate;
            input[i] = (float)(0.3 * Math.Sin(phase) + 0.15 * Math.Sin(3 * phase) + 0.05 * (random.NextDouble() - 0.5));
        }

        var fast = new WsolaTimeStretcher(sampleRate).Render(input, rate).Samples;
        var reference = DirectWsola(input, sampleRate, rate);

        Assert.Equal(reference.Length, fast.Length);
        double worst = 0;
        for (int i = 0; i < fast.Length; i++) worst = Math.Max(worst, Math.Abs(fast[i] - reference[i]));
        Assert.True(worst < 1e-4, $"largest difference {worst:E2}");
    }

    /// <summary>The grain search as first written: every quantity recomputed per candidate.</summary>
    private static float[] DirectWsola(float[] input, int sampleRate, double r)
    {
        int frameSize = Math.Max(64, sampleRate * 30 / 1000), tolerance = frameSize / 2 / 3;
        int length = (int)Math.Round(input.Length / r, MidpointRounding.AwayFromZero);
        var output = new float[length];
        var weights = new double[length];
        int window = Math.Min(frameSize, Math.Min(input.Length, Math.Max(1, length / 2)));
        int hop = Math.Max(1, window / 2);
        int previous = 0;
        for (int target = 0; ; target = Math.Min(target + hop, length - window))
        {
            bool last = target == length - window;
            int nominal = last ? input.Length - window : Math.Clamp((int)Math.Round(target * r), 0, Math.Max(0, input.Length - window));
            int best = nominal;
            double bestScore = double.NegativeInfinity;
            if (target > 0 && !last)
            {
                for (int candidate = Math.Max(previous, nominal - tolerance); candidate <= Math.Min(input.Length - window, nominal + tolerance); candidate++)
                {
                    double dot = 0, a = 0, b = 0;
                    for (int i = 0; i < hop && target + i < length; i++)
                    {
                        double x = weights[target + i] > 0 ? output[target + i] / weights[target + i] : 0;
                        double y = input[candidate + i];
                        dot += x * y; a += x * x; b += y * y;
                    }
                    double score = dot / Math.Sqrt(Math.Max(1e-20, a * b)) - Math.Abs(candidate - nominal) * 1e-8;
                    if (score > bestScore) { bestScore = score; best = candidate; }
                }
            }
            best = Math.Max(previous, best);
            previous = best;
            for (int i = 0; i < window && target + i < length; i++)
            {
                double weight = Math.Max(1e-6, 0.5 - 0.5 * Math.Cos(2 * Math.PI * (i + 0.5) / window));
                output[target + i] += (float)(input[best + i] * weight);
                weights[target + i] += weight;
            }
            if (last) break;
        }
        for (int i = 0; i < length; i++) if (weights[i] > 0) output[i] = (float)(output[i] / weights[i]);
        return output;
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

        // Trim 50 ms from each edge. Trimming 250 ms from each edge left no samples when
        // the renderer correctly produced exactly half a second at 2x.
        var body = output.AsSpan(Rate / 20, output.Length - Rate / 10).ToArray();
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
    public async Task ADeviceThatCannotStart_IsAnError_NotSilentPlaying()
    {
        var (engine, device, _) = Load();
        await using (engine)
        {
            string? failure = null;
            engine.DeviceFailed += d => failure = d;
            device.FailStartWith = "the sink went away";

            await Assert.ThrowsAsync<InvalidOperationException>(() => engine.PlayAsync());

            Assert.Equal(PlaybackState.Error, engine.State);
            Assert.Equal("the sink went away", failure);
        }
    }

    [Fact]
    public async Task ClearingTheQueue_WaitsForAClipStillBeingQueued()
    {
        var (engine, _, _) = Load();
        await using (engine)
        {
            // A long clip, so queueing it (decode, resample) is still running when the clear comes.
            const long frames = 24000 * 30;
            var queueing = engine.QueueNextAsync("s1", SyntheticNarration.Clip("s1", frames, 24000), SyntheticNarration.TonePcm(frames, 24000));
            var clearing = engine.ClearQueuedAsync();
            bool queued = await queueing;
            await clearing;

            Assert.True(queued);
            Assert.Null(engine.QueuedSegmentId);
        }
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
            await engine.SetPlaybackRateAsync(2.0);
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

    /// <summary>
    /// A minute of speech at 1.5x: decoding, resampling and time-stretching it takes a tenth of a
    /// second or more. Play with the passage already prepared awaited nothing that yielded, so all
    /// of it ran on the caller's thread, which is the UI thread, and the window froze.
    /// </summary>
    [Fact]
    public async Task LoadingALongClip_ReturnsBeforeItIsRendered()
    {
        var device = new FakeAudioOutputDevice(new AudioFormat(44100, 1, SampleFormat.Float32LittleEndian))
        {
            Devices = [new AudioDeviceInfo("0", "Fake", true, 44100, 1, 0.01)],
        };
        var engine = new AudioPlaybackEngine(device, new AudioEngineOptions(PreferredDeviceRate: 44100));
        await using (engine)
        {
            await engine.SetPlaybackRateAsync(1.5);
            const long frames = 24000 * 60;
            var load = engine.LoadAsync("s0", SyntheticNarration.Clip("s0", frames, 24000), SyntheticNarration.TonePcm(frames, 24000));
            Assert.False(load.IsCompleted, "the clip was rendered on the caller's thread");
            await load;
            Assert.Equal(60.0, engine.ClipDurationSeconds, 1);
        }
    }

    /// <summary>Changing the speed re-renders the clip: not on the caller's thread either.</summary>
    [Fact]
    public async Task ARateChangeOnALongClip_ReturnsBeforeItIsRendered()
    {
        var (engine, _, _) = Load(frames: 24000 * 60);
        await using (engine)
        {
            var change = engine.SetPlaybackRateAsync(1.5);
            Assert.False(change.IsCompleted, "the clip was re-rendered on the caller's thread");
            await change;
            Assert.Equal(1.5, engine.PlaybackRate, 6);
            Assert.Equal(60.0, engine.ClipDurationSeconds, 1);
        }
    }

    /// <summary>
    /// Play pressed while a speed change is still rendering waits for it. It started the device
    /// meanwhile, and the change's flush then stopped it again: playing over, with no sound and
    /// nothing to start the device, so the reader sat on Buffering.
    /// </summary>
    [Fact]
    public async Task AResumeWhileARateChangeRenders_StillPlays()
    {
        var (engine, device, _) = Load(frames: 24000 * 120);
        await using (engine)
        {
            await engine.PlayAsync();
            await WaitForQueuedAsync(engine, device, 4096);
            await engine.PauseAsync();

            var change = engine.SetPlaybackRateAsync(1.5);
            await Task.Delay(20);   // a two-minute clip takes far longer than this to render
            Assert.False(change.IsCompleted, "the render finished before Resume was pressed");
            await engine.ResumeAsync();
            await change;

            Assert.Equal(PlaybackState.Playing, engine.State);
            Assert.True(device.IsRunning, "the device was left stopped");
            long written = device.TotalWritten;
            await WaitUntil(() => { device.AdvanceByFrames(2048); return device.TotalWritten > written + 8192; }, "writing after the resume");
            Assert.Equal(PlaybackState.Playing, engine.State);
        }
    }

    /// <summary>
    /// Several changes asked for while one renders end at the last rate asked for, with the
    /// position held, whatever order their turns come in.
    /// </summary>
    [Fact]
    public async Task QuickRateChanges_EndAtTheLastRate_AndHoldThePosition()
    {
        var (engine, device, _) = Load(frames: 48000);
        await using (engine)
        {
            await engine.PlayAsync();
            await WaitForQueuedAsync(engine, device, 22050);
            device.AdvanceBySeconds(0.5);
            Assert.Equal(0.5, engine.PositionSeconds, 1);

            await Task.WhenAll(engine.SetPlaybackRateAsync(1.25), engine.SetPlaybackRateAsync(2.0), engine.SetPlaybackRateAsync(0.75));
            Assert.Equal(0.75, engine.PlaybackRate, 6);
            Assert.Equal(0.5, engine.PositionSeconds, 1);
        }
    }

    /// <summary>
    /// A clip queued while a rate change is under way plays at the new rate: queueing takes its turn
    /// with the change, so the change either finds the clip and re-renders it or the clip is
    /// rendered after it, at the new rate.
    /// </summary>
    [Fact]
    public async Task AClipQueuedBesideARateChange_IsWrittenAtTheNewRate()
    {
        var (engine, device) = LoadAtSourceRate(24000);   // one second, the device at the clip's rate
        await using (engine)
        {
            await engine.PlayAsync();
            var queue = engine.QueueNextAsync("s1", SyntheticNarration.Clip("s1", 24000, 24000), SyntheticNarration.TonePcm(24000, 24000));
            var change = engine.SetPlaybackRateAsync(2.0);
            await Task.WhenAll(queue, change);
            Assert.True(await queue);
            Assert.Equal("s1", engine.QueuedSegmentId);

            // Both seconds at 2x are about 24000 frames; the queued second at 1x would add 12000 more.
            await WaitUntil(() => device.TotalWritten >= 20000, "both clips written at the new rate");
            await Task.Delay(200);
            Assert.InRange(device.TotalWritten, 20000, 27000);
        }
    }

    [Fact]
    public async Task RateChangeAfterSeek_PreservesThePosition()
    {
        // Regression: SetPlaybackRate recomputed the position from presented frames alone,
        // discarding the seek base, so changing speed at 1.6 s jumped back to ~0.1 s.
        var (engine, device, _) = Load(frames: 48000);
        await using (engine)
        {
            await engine.PlayAsync();
            await WaitForQueuedAsync(engine, device, 22050);
            device.AdvanceBySeconds(0.5);

            await engine.SeekAsync(1.5);
            await WaitForQueuedAsync(engine, device, 4410);
            device.AdvanceBySeconds(0.1);
            Assert.Equal(1.6, engine.PositionSeconds, 1);

            await engine.SetPlaybackRateAsync(2.0);
            Assert.Equal(1.6, engine.PositionSeconds, 1);

            // A second change must also hold: the first change re-seats the base it must read.
            await engine.SetPlaybackRateAsync(0.75);
            Assert.Equal(1.6, engine.PositionSeconds, 1);
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

    // ---- gapless continuation ------------------------------------------------------------

    private static (AudioPlaybackEngine Engine, FakeAudioOutputDevice Device) LoadAtSourceRate(long frames)
        => Load(frames: frames, sourceRate: 24000, deviceRate: 24000) is var (engine, device, _) ? (engine, device) : default;

    /// <summary>
    /// A queued clip is written straight after the current one, before the current one drains, so
    /// the device never runs dry at the boundary. The clock and the segment switch over when the
    /// boundary frame is presented, and the boundary is reported as an advance, not a completion.
    /// </summary>
    [Fact]
    public async Task AQueuedClip_FollowsWithoutAGap_AndTheClockSwitchesAtTheBoundary()
    {
        var (engine, device) = LoadAtSourceRate(24000);   // one second
        await using (engine)
        {
            var completed = new List<string>();
            (string, string)? advanced = null;
            engine.ClipCompleted += id => { lock (completed) completed.Add(id); };
            engine.ClipAdvanced += (from, to) => advanced = (from, to);

            Assert.True(await engine.QueueNextAsync("s1", SyntheticNarration.Clip("s1", 12000, 24000), SyntheticNarration.TonePcm(12000, 24000)));
            Assert.Equal("s1", engine.QueuedSegmentId);
            await engine.PlayAsync();

            // Both clips are written while nothing has been presented: no wait for a drain.
            await WaitUntil(() => device.TotalWritten >= 36000, "the queued clip to be written behind the first");

            device.AdvanceByFrames(23000);
            Assert.Equal("s0", engine.CurrentSegmentId);
            Assert.Equal(23000 / 24000.0, engine.PositionSeconds, 3);

            device.AdvanceByFrames(2000);   // 1000 frames past the boundary
            await WaitUntil(() => advanced is not null, "the switch to the queued clip");
            Assert.Equal(("s0", "s1"), advanced);
            Assert.Equal("s1", engine.CurrentSegmentId);
            Assert.Null(engine.QueuedSegmentId);
            Assert.Equal(1000 / 24000.0, engine.PositionSeconds, 3);
            Assert.Equal(0.5, engine.ClipDurationSeconds, 3);
            Assert.Equal(PlaybackState.Playing, engine.State);
            lock (completed) Assert.Empty(completed);   // the boundary is not a completion

            device.DrainAll();
            await WaitUntil(() => { lock (completed) return completed.Count > 0; }, "the queued clip to complete");
            lock (completed) Assert.Equal(["s1"], completed);
        }
    }

    /// <summary>
    /// A queued clip that must not be heard (its words were edited) is removed even after the writer
    /// has begun writing it: the device is flushed and the current clip resumes where it was.
    /// </summary>
    [Fact]
    public async Task ClearingAQueuedClipAlreadyWritten_FlushesIt_AndKeepsThePosition()
    {
        var (engine, device) = LoadAtSourceRate(24000);
        await using (engine)
        {
            string? advanced = null;
            var completed = new List<string>();
            engine.ClipAdvanced += (_, to) => advanced = to;
            engine.ClipCompleted += id => { lock (completed) completed.Add(id); };

            Assert.True(await engine.QueueNextAsync("s1", SyntheticNarration.Clip("s1", 12000, 24000), SyntheticNarration.TonePcm(12000, 24000)));
            await engine.PlayAsync();
            await WaitUntil(() => device.TotalWritten >= 36000, "the queued clip to be written");
            device.AdvanceByFrames(12000);
            int flushes = device.FlushCount;

            await engine.ClearQueuedAsync();

            Assert.True(device.FlushCount > flushes, "written audio of the cleared clip must be flushed");
            Assert.Null(engine.QueuedSegmentId);
            Assert.Equal(0.5, engine.PositionSeconds, 2);
            await WaitUntil(() => { device.DrainAll(); lock (completed) return completed.Count > 0; }, "completion");
            Assert.Null(advanced);
            lock (completed) Assert.Equal(["s0"], completed);
        }
    }

    [Fact]
    public async Task QueueingAfterTheClipCompleted_IsRefused()
    {
        var (engine, device) = LoadAtSourceRate(2400);
        await using (engine)
        {
            string? completed = null;
            engine.ClipCompleted += id => completed = id;
            await engine.PlayAsync();
            await WaitUntil(() => { device.DrainAll(); return completed is not null; }, "completion");

            Assert.False(await engine.QueueNextAsync("s1", SyntheticNarration.Clip("s1", 2400, 24000), SyntheticNarration.TonePcm(2400, 24000)));
        }
    }

    [Fact]
    public async Task ARateChange_ReRendersTheQueuedClip_AndStillFollowsWithoutAGap()
    {
        var (engine, device) = LoadAtSourceRate(24000);
        await using (engine)
        {
            (string, string)? advanced = null;
            engine.ClipAdvanced += (from, to) => advanced = (from, to);
            Assert.True(await engine.QueueNextAsync("s1", SyntheticNarration.Clip("s1", 24000, 24000), SyntheticNarration.TonePcm(24000, 24000)));
            await engine.PlayAsync();
            await WaitUntil(() => device.TotalWritten >= 48000, "both clips written");

            await engine.SetPlaybackRateAsync(2.0);   // flushes; both clips re-rendered at half length
            await WaitUntil(() => device.TotalWritten >= 20000, "both clips written again at the new rate");
            Assert.Equal("s1", engine.QueuedSegmentId);

            await WaitUntil(() => { device.AdvanceByFrames(1200); return advanced is not null; }, "the switch at the new rate");
            Assert.Equal(("s0", "s1"), advanced);
        }
    }

    /// <summary>
    /// A speed change just after the device began presenting the queued clip, before the writer
    /// noticed, carries on in that clip from where it is. It wrote the queued clip again from its
    /// start, so its first moments were heard twice and the clock re-based on the old clip's end.
    /// </summary>
    [Fact]
    public async Task ARateChangeJustPastTheBoundary_CarriesOnInTheQueuedClip()
    {
        var device = new FakeAudioOutputDevice(new AudioFormat(24000, 1, SampleFormat.Float32LittleEndian))
        {
            Devices = [new AudioDeviceInfo("0", "Fake", true, 24000, 1, 0.01)],
        };
        // A writer that looks for the boundary only every two seconds, so the test can cross it
        // between two looks, as a real writer sometimes is between two looks when a change comes.
        var engine = new AudioPlaybackEngine(device, new AudioEngineOptions(PreferredDeviceRate: 24000, WriterPollMilliseconds: 2000));
        await using (engine)
        {
            await engine.LoadAsync("s0", SyntheticNarration.Clip("s0", 24000, 24000), SyntheticNarration.TonePcm(24000, 24000));
            (string, string)? advanced = null;
            engine.ClipAdvanced += (from, to) => advanced = (from, to);
            Assert.True(await engine.QueueNextAsync("s1", SyntheticNarration.Clip("s1", 24000, 24000), SyntheticNarration.TonePcm(24000, 24000)));
            await engine.PlayAsync();
            await WaitUntil(() => device.TotalWritten >= 48000, "both clips written");
            await Task.Delay(100);   // the writer is now waiting out its poll

            device.AdvanceByFrames(24000 + 2400);   // a tenth of a second into s1
            Assert.Equal("s0", engine.CurrentSegmentId);   // the writer has not looked yet
            await engine.SetPlaybackRateAsync(2.0);

            Assert.Equal(("s0", "s1"), advanced);
            Assert.Equal("s1", engine.CurrentSegmentId);
            Assert.Null(engine.QueuedSegmentId);
            Assert.Equal(0.1, engine.PositionSeconds, 2);
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
