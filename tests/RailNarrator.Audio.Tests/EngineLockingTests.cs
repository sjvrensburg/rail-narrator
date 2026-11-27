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
        // this handler runs if the thread raising the event is not holding that lock. The read gets a
        // thread of its own rather than Task.Run: this handler already blocks a pool thread, and on a
        // two-core runner the queued read waited for the pool to grow and missed a 1.5 s wait with the
        // lock free (a Buffering raised from the writer's underrun branch). A held lock never returns,
        // so the longer wait costs only a failing run.
        engine.StateChanged += state =>
        {
            var reader = new Thread(() => _ = engine.PositionSeconds) { IsBackground = true };
            reader.Start();
            observed.Enqueue((state, reader.Join(TimeSpan.FromSeconds(10))));
        };

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
    /// Two stops in a row, the second followed at once by a load and play (two quick seeks). The
    /// first stop waits for the writer to leave a slow write; the second used to find no writer and
    /// return at once, and the first then flushed and emptied the engine under the clip the second
    /// had started, which left the coordinator playing an idle transport (the Windows runner's
    /// stress-test failure). Stops, loads and pauses now run in order, each to its end.
    /// </summary>
    [Fact]
    public async Task AnEarlierStopCannotEmptyAClipLoadedAfterALaterOne()
    {
        var device = new FakeAudioOutputDevice(new AudioFormat(24000, 1, SampleFormat.Float32LittleEndian))
        {
            Devices = [new AudioDeviceInfo("0", "Fake", true, 24000, 1, 0.01)],
        };
        await using var engine = new AudioPlaybackEngine(device, new AudioEngineOptions(PreferredDeviceRate: 24000));
        await engine.LoadAsync("s0", SyntheticNarration.Clip("s0", 24000, 24000), SyntheticNarration.TonePcm(24000, 24000));
        await engine.PlayAsync();
        device.WriteLatency = TimeSpan.FromMilliseconds(300);
        await Task.Delay(50);   // the writer is now inside a 300 ms write

        Task first = engine.StopAsync();
        Task secondThenPlay = Task.Run(async () =>
        {
            await engine.StopAsync();
            device.WriteLatency = TimeSpan.Zero;
            await engine.LoadAsync("s1", SyntheticNarration.Clip("s1", 24000, 24000), SyntheticNarration.TonePcm(24000, 24000));
            await engine.PlayAsync();
        });
        await Task.WhenAll(first, secondThenPlay).WaitAsync(TimeSpan.FromSeconds(10));

        Assert.Equal(PlaybackState.Playing, engine.State);
        Assert.Equal("s1", engine.CurrentSegmentId);
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
        var clock = System.Diagnostics.Stopwatch.StartNew();
        var transitionTimes = new ConcurrentQueue<long>();
        using var stop = new CancellationTokenSource();
        var uiWork = new ConcurrentQueue<Action>();
        // Every snapshot and engine state, timed, for the failure message: the race this test
        // provokes has been seen only on a Windows runner, so the log is the only view of it.
        var events = new ConcurrentQueue<string>();
        void Note(string what) { events.Enqueue($"{clock.ElapsedMilliseconds}ms {what}"); while (events.Count > 400) events.TryDequeue(out _); }
        coordinator.SnapshotChanged += snap => Note($"snapshot {snap.State} #{snap.SegmentIndex} gen {snap.Generation}/{coordinator.Generation} '{snap.Message}'");
        engine.StateChanged += state => Note($"engine {state}");
        engine.ClipCompleted += id => Note($"engine completed {id}");
        engine.ClipAdvanced += (done, started) => Note($"engine advanced {done} -> {started}");
        coordinator.SnapshotChanged += snap => uiWork.Enqueue(() =>
        {
            if (snap.Generation != coordinator.Generation || snap.SegmentIndex == lastIndex) return;
            lastIndex = snap.SegmentIndex;
            Interlocked.Increment(ref transitions);
            transitionTimes.Enqueue(clock.ElapsedMilliseconds);
            if (snap.SegmentId is { } id)
            {
                Note($"seek to {id} (gen {coordinator.Generation})");
                _ = coordinator.GoToSegmentAsync(id).ContinueWith(t => Note($"seek to {id} ended {t.Status}{(t.Exception is { } e ? " " + e.InnerException?.Message : "")}"), TaskScheduler.Default);
            }
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
        // Done when every transition was seen, or when the reading reached the end: the simulated UI
        // drops a snapshot whose generation a seek has already overtaken (as the real one does), so a
        // passage can go by uncounted. A run left stuck anywhere before the end still fails.
        bool Finished() => Interlocked.Read(ref transitions) >= segments - 1
            || coordinator.Snapshot is { State: PlaybackState.Completed } end && end.SegmentIndex == segments - 1;
        while (!Finished() && Environment.TickCount64 < deadline)
        {
            await Task.Delay(100);
            long beat = Interlocked.Read(ref heartbeat);
            if (beat != lastBeat) { lastBeat = beat; stalledSince = Environment.TickCount64; }
            Assert.True(Environment.TickCount64 - stalledSince < 5000,
                $"The UI thread stopped after {Interlocked.Read(ref transitions)} passage transitions: deadlock.");
        }
        stop.Cancel();
        var last = coordinator.Snapshot;
        Assert.True(Finished(),
            $"Only {Interlocked.Read(ref transitions)} of {segments - 1} transitions completed in 60 s, and the reading did not reach the end. " +
            $"Transitions at (ms): {string.Join(", ", transitionTimes.Take(20))}. " +
            $"Last snapshot: {last.State}, passage {last.SegmentIndex}, position {last.PositionSeconds:F3} s of {last.ClipDurationSeconds:F3} s, " +
            $"buffered ahead {last.BufferedAheadSeconds:F3} s, message '{last.Message}'; engine {engine.State}; " +
            $"device presented {device.PresentedFrames}, queued {device.QueuedFrames}; UI ticks {Interlocked.Read(ref heartbeat)}.\n" +
            "Last events:\n" + string.Join("\n", events.TakeLast(40)));
    }
}
