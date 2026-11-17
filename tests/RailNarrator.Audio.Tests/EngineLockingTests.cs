using System.Collections.Concurrent;
using RailNarrator.Audio;
using RailNarrator.Domain;
using RailNarrator.Speech;
using RailNarrator.TestFixtures;
using Xunit;

namespace RailNarrator.Audio.Tests;

/// <summary>
/// The application froze during ordinary playback: "State: Playing", silence, dead menus. The engine
/// raised its state events while holding its own lock, and the coordinator read the engine while
/// holding the coordinator's — each waiting for the other.
/// </summary>
public sealed class EngineLockingTests
{
    [Fact]
    public async Task StateEvents_AreRaisedOutsideTheEngineLock()
    {
        var device = new FakeAudioOutputDevice { AutoAdvance = true };
        await using var engine = new AudioPlaybackEngine(device);
        var observed = new ConcurrentQueue<(PlaybackState State, bool LockWasFree)>();
        // PositionSeconds needs the engine lock. Read from another thread, it can only return while
        // this handler runs if the thread raising the event is not holding that lock.
        engine.StateChanged += state =>
            observed.Enqueue((state, Task.Run(() => engine.PositionSeconds).Wait(1500)));

        const long frames = 24000;
        await engine.LoadAsync("s0", SyntheticNarration.Clip("s0", frames), SyntheticNarration.TonePcm(frames));
        await engine.PlayAsync();
        await engine.PauseAsync();
        await engine.ResumeAsync();
        await engine.StopAsync();

        Assert.Contains(observed, o => o.State == PlaybackState.Playing);
        Assert.Contains(observed, o => o.State == PlaybackState.Idle);
        Assert.All(observed, o => Assert.True(o.LockWasFree, $"{o.State} was raised while the engine lock was held"));
    }

    /// <summary>
    /// The real coordinator driving the real engine, with a simulated UI thread that runs the frame
    /// tick and seeks on every passage change. Before the fix this stopped within a few dozen
    /// transitions on most runs; it is a stress test, so a pass is evidence rather than proof — the
    /// two deterministic tests above and in <c>CoordinatorLockingTests</c> pin the actual ordering.
    /// </summary>
    [Fact]
    public async Task SeeksRacingClipTransitions_DoNotDeadlock()
    {
        const int segments = 120;
        var units = new List<ReadingUnit>();
        for (int i = 0; i < segments; i++)
        {
            var anchor = SyntheticNarration.Anchor($"a{i}", 0, new PageRect(72, 100 + i, 300, 112 + i));
            units.Add(SyntheticNarration.ProseUnit($"u{i}", $"Sentence number {i} of the document.", anchor));
        }
        var plan = SyntheticNarration.Plan(units);
        var device = new FakeAudioOutputDevice { AutoAdvance = true };
        await using var engine = new AudioPlaybackEngine(device);
        await using var coordinator = new NarrationCoordinator(
            new FakeTtsProvider(new FakeTtsOptions(FixedFrameCount: 1200)), engine);   // 50 ms clips

        long heartbeat = 0, transitions = 0;
        int lastIndex = -1;
        using var stop = new CancellationTokenSource();
        var uiWork = new ConcurrentQueue<Action>();
        coordinator.SnapshotChanged += snap => uiWork.Enqueue(() =>
        {
            if (snap.Generation != coordinator.Generation || snap.SegmentIndex == lastIndex) return;
            lastIndex = snap.SegmentIndex;
            Interlocked.Increment(ref transitions);
            if (snap.SegmentId is { } id) _ = coordinator.GoToSegmentAsync(id);
        });
        var ui = new Thread(() =>
        {
            while (!stop.IsCancellationRequested)
            {
                while (uiWork.TryDequeue(out var work)) work();
                coordinator.Tick();
                Interlocked.Increment(ref heartbeat);
                Thread.Sleep(1);
            }
        }) { IsBackground = true, Name = "simulated-ui" };

        await coordinator.LoadPlanAsync(plan);
        ui.Start();
        await coordinator.PlayAsync();

        long lastBeat = -1;
        long stalledSince = Environment.TickCount64, deadline = Environment.TickCount64 + 60_000;
        while (Interlocked.Read(ref transitions) < segments - 1 && Environment.TickCount64 < deadline)
        {
            await Task.Delay(100);
            long beat = Interlocked.Read(ref heartbeat);
            if (beat != lastBeat) { lastBeat = beat; stalledSince = Environment.TickCount64; }
            Assert.True(Environment.TickCount64 - stalledSince < 5000,
                $"The UI thread stopped after {Interlocked.Read(ref transitions)} passage transitions: deadlock.");
        }
        stop.Cancel();
        Assert.True(Interlocked.Read(ref transitions) >= segments - 1,
            $"Only {Interlocked.Read(ref transitions)} of {segments - 1} transitions completed in 60 s.");
    }
}
