namespace RailNarrator.Audio;

/// <summary>
/// Maps callback DAC timestamps to content frames. Silence inserted by an underrun consumes no
/// content. The callback is the sole writer; readers use a sequence lock, without blocking it.
/// </summary>
public sealed class PresentationClock
{
    private readonly Block[] _blocks = new Block[256];
    private int _sequence;
    private long _count;
    private long _base;
    private long _lastPresented;
    private readonly record struct Block(double DacTime, long ContentStart, int ContentFrames, int SampleRate);

    /// <summary>Call only when the callback has stopped.</summary>
    public void Reset(long frame = 0)
    {
        Interlocked.Increment(ref _sequence);
        _count = 0;
        _base = frame;
        Interlocked.Exchange(ref _lastPresented, frame);
        Interlocked.Increment(ref _sequence);
    }

    public void Record(double dacTime, long contentStart, int contentFrames, int sampleRate)
    {
        if (!double.IsFinite(dacTime) || dacTime <= 0 || sampleRate <= 0) return;
        Interlocked.Increment(ref _sequence);
        _blocks[_count % _blocks.Length] = new Block(dacTime, contentStart, contentFrames, sampleRate);
        _count++;
        Interlocked.Increment(ref _sequence);
    }

    public long PresentedAt(double streamTime)
    {
        if (!double.IsFinite(streamTime) || streamTime <= 0) return Interlocked.Read(ref _lastPresented);
        for (int attempt = 0; attempt < 4; attempt++)
        {
            int before = Volatile.Read(ref _sequence);
            if ((before & 1) != 0) continue;
            long count = _count, presented = _base;
            for (long i = count - 1; i >= Math.Max(0, count - _blocks.Length); i--)
            {
                var block = _blocks[i % _blocks.Length];
                if (streamTime < block.DacTime) continue;
                double elapsed = (streamTime - block.DacTime) * block.SampleRate;
                presented = block.ContentStart + (long)Math.Clamp(elapsed, 0, block.ContentFrames);
                break;
            }
            if (before != Volatile.Read(ref _sequence)) continue;
            // Device timestamps can jitter. Reading progress must not move backwards within a session.
            long previous = Interlocked.Read(ref _lastPresented);
            while (presented > previous)
            {
                long seen = Interlocked.CompareExchange(ref _lastPresented, presented, previous);
                if (seen == previous) return presented;
                previous = seen;
            }
            return previous;
        }
        return Interlocked.Read(ref _lastPresented);
    }
}
