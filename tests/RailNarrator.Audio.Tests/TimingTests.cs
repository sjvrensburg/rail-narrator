using RailNarrator.Audio;
using RailNarrator.Domain;
using RailNarrator.TestFixtures;
using Xunit;

namespace RailNarrator.Audio.Tests;

public sealed class PresentationClockTests
{
    [Fact]
    public void ClockUsesDacPresentationInsteadOfSubmissionTime()
    {
        var clock = new PresentationClock();
        clock.Record(10.1, 0, 480, 48000);
        Assert.Equal(0, clock.PresentedAt(10.0));
        Assert.InRange(clock.PresentedAt(10.105), 239, 240);
        Assert.Equal(480, clock.PresentedAt(10.2));
    }
    [Fact]
    public void FutureQueuedBlocksDoNotAdvanceTheClockAndUnderrunSilenceFreezesIt()
    {
        var clock = new PresentationClock();
        clock.Record(1, 0, 480, 48000);
        clock.Record(1.01, 480, 0, 48000);
        clock.Record(1.5, 480, 480, 48000);
        Assert.InRange(clock.PresentedAt(1.005), 239, 240);
        Assert.Equal(480, clock.PresentedAt(1.4));
        Assert.InRange(clock.PresentedAt(1.505), 719, 720);
    }
    [Fact]
    public void TimestampJitterCannotMoveBackwardsAndFlushResetsTheEpoch()
    {
        var clock = new PresentationClock();
        clock.Record(1, 0, 480, 48000);
        long first = clock.PresentedAt(1.005);
        Assert.Equal(first, clock.PresentedAt(1.004));
        clock.Reset();
        Assert.Equal(0, clock.PresentedAt(1.5));
        clock.Reset(240);
        clock.Record(2, 240, 480, 48000);
        Assert.Equal(240, clock.PresentedAt(1.9));
    }
}

public sealed class RenderMappingTests
{
    [Theory]
    [InlineData(0.04, 0.5)]
    [InlineData(0.04, 2.5)]
    [InlineData(1.0, 1.5)]
    [InlineData(1.0, 2.5)]
    [InlineData(10.0, 2.5)]
    public void RateChangesHaveExactDurationAndMonotonicInvertibleSourceMap(double duration, double rate)
    {
        var source = Enumerable.Range(0, (int)(duration * 24000)).Select(i => (float)Math.Sin(2 * Math.PI * 220 * i / 24000)).ToArray();
        var render = new WsolaTimeStretcher(24000).Render(source, rate);
        Assert.Equal((int)Math.Round(source.Length / rate), render.Samples.Length);
        Assert.Equal(0, render.SourceFrameAt(0));
        Assert.Equal(source[0], render.Samples[0], 5);
        Assert.Equal(source.Length, render.SourceFrameAt(render.Samples.Length));
        Assert.Equal(source.Length - 1, render.SourceFrameAt(render.Samples.Length - 1), 5);
        Assert.Equal(source[^1], render.Samples[^1], 5);
        double previous = -1;
        for (int frame = 0; frame < render.Samples.Length; frame += 31)
        {
            double mapped = render.SourceFrameAt(frame);
            Assert.True(mapped >= previous);
            previous = mapped;
            long back = render.OutputFrameAt(mapped);
            Assert.InRange(render.SourceFrameAt(back) - mapped, 0, 2);
        }
        Assert.All(render.Samples, sample => Assert.True(float.IsFinite(sample)));
    }

    [Fact]
    public void MapTracksAnAudibleTransitionAtNonUnitySpeed()
    {
        var source = new float[48000];
        for (int i = 24000; i < source.Length; i++) source[i] = (float)(0.4 * Math.Sin(2 * Math.PI * 440 * i / 24000));
        var render = new WsolaTimeStretcher(24000).Render(source, 1.5);
        int audible = Array.FindIndex(render.Samples, s => Math.Abs(s) > 0.05);
        double mapped = render.SourceFrameAt(audible) / 24000;
        Assert.InRange(mapped, 0.98, 1.02);
    }
}

public sealed class DeviceIntegrationTests
{
    [AudioDeviceFact]
    public async Task SilentRealDevicePlaybackSupportsPauseSeekRateChangeAndFinalDrain()
    {
        using var device = new PortAudioOutputDevice();
        Assert.NotEmpty(device.Enumerate());
        await using var engine = new AudioPlaybackEngine(device);
        var asset = SyntheticNarration.Clip("clock-probe", 48000, 24000);
        await engine.LoadAsync(asset.SegmentId, asset, new byte[48000 * 4]);
        await engine.PlayAsync();
        await Until(() => engine.PositionSeconds >= 0.08);
        await engine.PauseAsync();
        double paused = engine.PositionSeconds;
        await Task.Delay(80);
        Assert.Equal(paused, engine.PositionSeconds, 6);
        await engine.SeekAsync(1.2);
        await engine.SetPlaybackRateAsync(1.5);
        Assert.InRange(engine.PositionSeconds, 1.199, 1.201);
        var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        engine.ClipCompleted += _ => done.TrySetResult();
        await engine.ResumeAsync();
        await done.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(2, engine.PositionSeconds, 4);
        Assert.True(device.HasDrained);
        Assert.NotNull(device.ReportedLatencySeconds);
        Assert.True(device.ReportedLatencySeconds >= 0);
        await engine.StopAsync();
        Assert.Equal(0, engine.PositionSeconds);
    }
    /// <summary>
    /// Through a real device: a Resume pressed while a speed change renders a long clip plays on.
    /// It left the stream stopped (the change's flush after the Resume's start) with the engine
    /// on Buffering and the clock still.
    /// </summary>
    [AudioDeviceFact]
    public async Task OnARealDevice_AResumeWhileARateChangeRenders_StillPlays()
    {
        using var device = new PortAudioOutputDevice();
        await using var engine = new AudioPlaybackEngine(device);
        var asset = SyntheticNarration.Clip("rate-probe", 24000 * 120, 24000);
        await engine.LoadAsync(asset.SegmentId, asset, new byte[24000 * 120 * 4]);
        await engine.PlayAsync();
        await Until(() => engine.PositionSeconds >= 0.2);
        await engine.PauseAsync();

        var change = engine.SetPlaybackRateAsync(1.5);
        await Task.Delay(20);
        Assert.False(change.IsCompleted, "the render finished before Resume was pressed");
        await engine.ResumeAsync();
        await change;

        double from = engine.PositionSeconds;
        await Task.Delay(1000);
        Assert.Equal(PlaybackState.Playing, engine.State);
        // A second of listening at 1.5x is a second and a half of the clip; the stalled engine moved not at all.
        Assert.InRange(engine.PositionSeconds - from, 1.2, 1.8);
        await engine.StopAsync();
    }

    private static async Task Until(Func<bool> predicate)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        while (!predicate()) await Task.Delay(5, timeout.Token);
    }
}

public sealed class AudioDeviceFactAttribute : FactAttribute
{
    public AudioDeviceFactAttribute()
    {
        if (Environment.GetEnvironmentVariable("RN_AUDIO_INTEGRATION") != "1")
            Skip = "Set RN_AUDIO_INTEGRATION=1 to exercise a real output device with silent audio.";
    }
}
