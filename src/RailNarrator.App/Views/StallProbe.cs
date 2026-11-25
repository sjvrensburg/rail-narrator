using System.Diagnostics;
using System.Runtime;
using Avalonia.Threading;

namespace RailNarrator.App.Views;

/// <summary>
/// The runtime's own counters at one moment: collections, time paused for them, methods compiled
/// and the time spent compiling, lock contention and bytes allocated. The difference between two
/// says whether a stall was the runtime's (a garbage collection, a burst of JIT) or the
/// application's own work.
/// </summary>
internal readonly record struct RuntimeCounters(
    int Gen0, int Gen1, int Gen2, TimeSpan GcPause, long JitMethods, TimeSpan JitTime, long Contention, long Allocated)
{
    public static RuntimeCounters Now() => new(
        GC.CollectionCount(0), GC.CollectionCount(1), GC.CollectionCount(2), GC.GetTotalPauseDuration(),
        System.Runtime.JitInfo.GetCompiledMethodCount(), System.Runtime.JitInfo.GetCompilationTime(),
        Monitor.LockContentionCount, GC.GetTotalAllocatedBytes());

    /// <summary>"gc=0/0/1 pause=212 ms jit=+38 methods 61 ms contention=+0 alloc=+3 MB".</summary>
    public string Since(RuntimeCounters before) => FormattableString.Invariant(
        $"gc={Gen0 - before.Gen0}/{Gen1 - before.Gen1}/{Gen2 - before.Gen2} pause={(GcPause - before.GcPause).TotalMilliseconds:F0} ms jit=+{JitMethods - before.JitMethods} methods {(JitTime - before.JitTime).TotalMilliseconds:F0} ms contention=+{Contention - before.Contention} alloc=+{(Allocated - before.Allocated) / 1048576.0:F1} MB");
}

/// <summary>
/// Opt-in, with <c>RN_FOLLOW_LOG=1</c>: a background thread asks the UI thread to answer every
/// 20 ms, at the dispatcher's highest priority, and logs "ui stall 212 ms" with the runtime
/// counters' change over the wait whenever the answer takes longer than
/// <see cref="FollowLog.FrameGapSeconds"/>. A frame gap with no UI stall beside it is the
/// compositor's or the render thread's, not the UI thread's.
/// </summary>
internal static class StallProbe
{
    private static int _started;

    public static void Start()
    {
        if (!FollowLog.Enabled || Interlocked.Exchange(ref _started, 1) == 1) return;
        var thread = new Thread(Run) { IsBackground = true, Name = "rn-stall-probe", Priority = ThreadPriority.AboveNormal };
        thread.Start();
        FollowLog.Note(FormattableString.Invariant(
            $"stall probe on: gc server={GCSettings.IsServerGC} latency={GCSettings.LatencyMode} cpus={Environment.ProcessorCount}"));
    }

    private static void Run()
    {
        using var answered = new ManualResetEventSlim();
        while (true)
        {
            answered.Reset();
            var before = RuntimeCounters.Now();
            long start = Stopwatch.GetTimestamp();
            Dispatcher.UIThread.Post(answered.Set, DispatcherPriority.Send);
            answered.Wait();
            var waited = Stopwatch.GetElapsedTime(start);
            if (waited.TotalSeconds > FollowLog.FrameGapSeconds)
                FollowLog.Note(FormattableString.Invariant($"ui stall {waited.TotalMilliseconds:F0} ms {RuntimeCounters.Now().Since(before)}"));
            Thread.Sleep(20);
        }
    }
}
