using System.Runtime.InteropServices;
using RailNarrator.Domain;

namespace RailNarrator.Audio;

/// <summary>PortAudio V19 ABI on Linux: C long/unsigned long are pointer-sized.</summary>
internal static class PortAudioNative
{
    private const string Library = "libportaudio.so.2";
    public const nuint SampleFormatFloat32 = 1;
    public const nuint SampleFormatInt16 = 8;
    public const int NoDevice = -1;
    [StructLayout(LayoutKind.Sequential)]
    public struct DeviceInfo
    {
        public int structVersion;
        public IntPtr name;
        public int hostApi, maxInputChannels, maxOutputChannels;
        public double defaultLowInputLatency, defaultLowOutputLatency;
        public double defaultHighInputLatency, defaultHighOutputLatency, defaultSampleRate;
    }
    [StructLayout(LayoutKind.Sequential)]
    public struct StreamParameters
    {
        public int device, channelCount;
        public nuint sampleFormat;
        public double suggestedLatency;
        public IntPtr hostApiSpecificStreamInfo;
    }
    [StructLayout(LayoutKind.Sequential)]
    public struct StreamInfo
    {
        public int structVersion;
        public double inputLatency, outputLatency, sampleRate;
    }
    [StructLayout(LayoutKind.Sequential)]
    public struct CallbackTimes { public double inputBufferAdcTime, currentTime, outputBufferDacTime; }
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    public delegate int Callback(IntPtr input, IntPtr output, nuint frames, ref CallbackTimes times, nuint flags, IntPtr userData);
    [DllImport(Library)] public static extern int Pa_Initialize();
    [DllImport(Library)] public static extern IntPtr Pa_GetVersionText();
    [DllImport(Library)] public static extern int Pa_GetDeviceCount();
    [DllImport(Library)] public static extern int Pa_GetDefaultOutputDevice();
    [DllImport(Library)] public static extern IntPtr Pa_GetDeviceInfo(int device);
    [DllImport(Library)] public static extern IntPtr Pa_GetErrorText(int error);
    [DllImport(Library)] public static extern IntPtr Pa_GetStreamInfo(IntPtr stream);
    [DllImport(Library)] public static extern double Pa_GetStreamTime(IntPtr stream);
    [DllImport(Library)] public static extern int Pa_OpenStream(out IntPtr stream, IntPtr input,
        ref StreamParameters output, double rate, nuint framesPerBuffer, nuint flags, Callback callback, IntPtr userData);
    [DllImport(Library)] public static extern int Pa_StartStream(IntPtr stream);
    [DllImport(Library)] public static extern int Pa_AbortStream(IntPtr stream);
    [DllImport(Library)] public static extern int Pa_CloseStream(IntPtr stream);
    [DllImport(Library)] public static extern int Pa_IsStreamActive(IntPtr stream);
    public static string Describe(int code) => Marshal.PtrToStringAnsi(Pa_GetErrorText(code)) ?? $"error {code}";
    public static string? VersionText() => Marshal.PtrToStringAnsi(Pa_GetVersionText());
}

/// <summary>
/// Callback audio output with a bounded, preallocated ring and DAC-timestamped content clock.
/// The callback only copies samples and records timestamps: no locks, allocations or user events.
/// </summary>
public sealed class PortAudioOutputDevice : IAudioOutputDevice
{
    public const int FramesPerBuffer = 512;

    /// <summary>
    /// Smallest stream latency requested from the host, in seconds.
    /// </summary>
    /// <remarks>
    /// Narration needs an accurate clock, not a short buffer: highlights follow DAC timestamps, so a
    /// longer device buffer costs start-up delay and nothing else. The device's own low-latency
    /// suggestion (8.7 ms on the reference machine) starves under PipeWire's ALSA bridge as soon as
    /// another client raises the graph quantum. Measured through the Bluetooth sink with the
    /// quantum at 2048 frames: 10 ms delivered 1 % of real time, 35 ms delivered 43 %, 100 ms and
    /// above delivered 100 %. See ADR-0003.
    /// </remarks>
    public const double MinimumStreamLatencySeconds = 0.1;

    /// <summary>
    /// Ring length in seconds of audio.
    /// </summary>
    /// <remarks>
    /// Space in the ring is counted against <i>presented</i> frames, because frames the callback has
    /// copied but the listener has not yet heard are kept for a pause to rewind to. The ring must
    /// therefore be longer than the whole output latency (device buffer plus, for Bluetooth, some
    /// 200 ms of transport) or the writer is locked out while the callback plays silence.
    /// </remarks>
    public const double RingSeconds = 2.0;
    private static readonly object InitLock = new();
    private static bool _initialised;
    private static string? _initError;
    private readonly object _gate = new();
    private readonly PortAudioNative.Callback _callback;
    private readonly PresentationClock _clock = new();
    private IntPtr _stream;
    private byte[] _ring = [];
    private int _capacityFrames, _bytesPerFrame, _sampleRate;
    private long _written, _consumed;
    private bool _running, _disposed;
    private double? _outputLatency;
    private int _underflows;
    public event Action<string>? DeviceFailed;
    public PortAudioOutputDevice() => _callback = Render;

    public static string? Initialise()
    {
        lock (InitLock)
        {
            if (_initialised) return _initError;
            try
            {
                int rc;
                using (NativeAudioChatter.Quiet()) rc = PortAudioNative.Pa_Initialize();
                _initError = rc == 0 ? null : PortAudioNative.Describe(rc);
            }
            catch (DllNotFoundException) { _initError = "Install libportaudio.so.2 to enable audio output."; }
            catch (EntryPointNotFoundException ex) { _initError = ex.Message; }
            _initialised = true;
            return _initError;
        }
    }
    public static string? Version => PortAudioNative.VersionText();
    public IReadOnlyList<AudioDeviceInfo> Enumerate()
    {
        if (Initialise() is not null) return [];
        var devices = new List<AudioDeviceInfo>();
        int count = PortAudioNative.Pa_GetDeviceCount(), defaultId = PortAudioNative.Pa_GetDefaultOutputDevice();
        for (int i = 0; i < count; i++)
        {
            var ptr = PortAudioNative.Pa_GetDeviceInfo(i);
            if (ptr == IntPtr.Zero) continue;
            var info = Marshal.PtrToStructure<PortAudioNative.DeviceInfo>(ptr);
            if (info.maxOutputChannels <= 0) continue;
            devices.Add(new AudioDeviceInfo(i.ToString(System.Globalization.CultureInfo.InvariantCulture),
                Marshal.PtrToStringAnsi(info.name) ?? $"Device {i}", i == defaultId,
                info.defaultSampleRate > 0 ? (int)info.defaultSampleRate : 44100, info.maxOutputChannels,
                info.defaultLowOutputLatency > 0 ? info.defaultLowOutputLatency : null));
        }
        return devices;
    }
    public void Open(string deviceId, AudioFormat format)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (Initialise() is { } error) throw new InvalidOperationException(error);
        if (!format.IsValid) throw new ArgumentException("Invalid output format.");
        lock (_gate)
        {
            CloseCore();
            int device = int.TryParse(deviceId, out int id) ? id : PortAudioNative.Pa_GetDefaultOutputDevice();
            var ptr = PortAudioNative.Pa_GetDeviceInfo(device);
            if (ptr == IntPtr.Zero) throw new InvalidOperationException("No usable audio output device.");
            var info = Marshal.PtrToStructure<PortAudioNative.DeviceInfo>(ptr);
            var parameters = new PortAudioNative.StreamParameters
            {
                device = device, channelCount = format.Channels,
                sampleFormat = format.SampleFormat switch
                {
                    SampleFormat.Float32LittleEndian => PortAudioNative.SampleFormatFloat32,
                    SampleFormat.Int16LittleEndian => PortAudioNative.SampleFormatInt16,
                    _ => throw new NotSupportedException("Unsupported output encoding."),
                },
                suggestedLatency = Math.Max(MinimumStreamLatencySeconds, info.defaultHighOutputLatency),
            };
            _bytesPerFrame = format.BytesPerFrame;
            _sampleRate = format.SampleRate;
            _capacityFrames = Math.Max(FramesPerBuffer * 8, (int)(_sampleRate * RingSeconds));
            _ring = new byte[_capacityFrames * _bytesPerFrame];
            _written = _consumed = 0;
            _underflows = 0;
            _clock.Reset();
            int rc = PortAudioNative.Pa_OpenStream(out var opened, IntPtr.Zero, ref parameters,
                format.SampleRate, FramesPerBuffer, 0, _callback, IntPtr.Zero);
            if (rc != 0) throw new InvalidOperationException("Cannot open audio: " + PortAudioNative.Describe(rc));
            _stream = opened;
            var streamInfo = PortAudioNative.Pa_GetStreamInfo(_stream);
            _outputLatency = streamInfo == IntPtr.Zero ? null : Marshal.PtrToStructure<PortAudioNative.StreamInfo>(streamInfo).outputLatency;
        }
    }
    public int BufferCapacityFrames => _capacityFrames;
    public int UnderflowCount => Volatile.Read(ref _underflows);
    public double? ReportedLatencySeconds { get { lock (_gate) return _outputLatency; } }
    public bool IsRunning { get { lock (_gate) return _running; } }
    public long PresentedFrames { get { lock (_gate) return PresentedCore(); } }
    private long PresentedCore() => _stream == IntPtr.Zero ? 0
        : Math.Min(Interlocked.Read(ref _written), _clock.PresentedAt(PortAudioNative.Pa_GetStreamTime(_stream)));
    public long QueuedFrames { get { lock (_gate) return Math.Max(0, _written - PresentedCore()); } }
    public bool HasDrained { get { lock (_gate) return PresentedCore() >= _written; } }

    public void Start()
    {
        lock (_gate)
        {
            if (_stream == IntPtr.Zero) throw new InvalidOperationException("The audio device is closed.");
            if (_running) return;
            int rc = PortAudioNative.Pa_StartStream(_stream);
            if (rc != 0) throw new InvalidOperationException(PortAudioNative.Describe(rc));
            _running = true;
        }
    }
    public void Pause()
    {
        lock (_gate)
        {
            if (!_running || _stream == IntPtr.Zero) return;
            long presented = PresentedCore();
            PortAudioNative.Pa_AbortStream(_stream);
            _running = false;
            // The ring retains unpresented samples, even if they were already copied to a callback.
            // Rewind the consumer so aborting does not skip those samples on resume.
            _consumed = presented;
            _clock.Reset(presented);
        }
    }
    public void Flush()
    {
        lock (_gate)
        {
            if (_stream != IntPtr.Zero && _running) PortAudioNative.Pa_AbortStream(_stream);
            _running = false;
            _written = _consumed = 0;
            _clock.Reset();
        }
    }
    public void Close() { lock (_gate) CloseCore(); }
    private void CloseCore()
    {
        if (_stream != IntPtr.Zero)
        {
            if (_running) PortAudioNative.Pa_AbortStream(_stream);
            PortAudioNative.Pa_CloseStream(_stream);
        }
        _stream = IntPtr.Zero;
        _running = false;
        _written = _consumed = 0;
        _outputLatency = null;
        _clock.Reset();
    }
    public int Write(ReadOnlySpan<byte> pcm, int frames)
    {
        if (frames <= 0) return 0;
        long deadline = Environment.TickCount64 + 5000;
        while (true)
        {
            lock (_gate)
            {
                if (!_running || _stream == IntPtr.Zero) return 0;
                int available = (int)Math.Max(0, _capacityFrames - (_written - PresentedCore()));
                int count = Math.Min(Math.Min(frames, pcm.Length / _bytesPerFrame), available);
                if (count > 0)
                {
                    int first = (int)(_written % _capacityFrames);
                    int part = Math.Min(count, _capacityFrames - first);
                    pcm[..(part * _bytesPerFrame)].CopyTo(_ring.AsSpan(first * _bytesPerFrame));
                    pcm.Slice(part * _bytesPerFrame, (count - part) * _bytesPerFrame).CopyTo(_ring);
                    Interlocked.Add(ref _written, count);
                    return count;
                }
            }
            // Backpressure is outside the state lock; UI clock reads never wait for a device write.
            if (Environment.TickCount64 >= deadline)
            {
                const string detail = "Audio presentation clock stalled for five seconds.";
                DeviceFailed?.Invoke(detail);
                throw new IOException(detail);
            }
            Thread.Sleep(1);
        }
    }
    private unsafe int Render(IntPtr input, IntPtr output, nuint frames, ref PortAudioNative.CallbackTimes times, nuint flags, IntPtr userData)
    {
        int frameCount = (int)frames;
        var destination = new Span<byte>((void*)output, frameCount * _bytesPerFrame);
        destination.Clear();
        long start = _consumed;
        int count = (int)Math.Min(frameCount, Math.Max(0, Interlocked.Read(ref _written) - start));
        if (count > 0)
        {
            int first = (int)(start % _capacityFrames);
            int part = Math.Min(count, _capacityFrames - first);
            _ring.AsSpan(first * _bytesPerFrame, part * _bytesPerFrame).CopyTo(destination);
            _ring.AsSpan(0, (count - part) * _bytesPerFrame).CopyTo(destination[(part * _bytesPerFrame)..]);
        }
        _clock.Record(times.outputBufferDacTime, start, count, _sampleRate);
        Interlocked.Exchange(ref _consumed, start + count);
        if ((flags & 4) != 0) Interlocked.Increment(ref _underflows);
        return 0;
    }
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        Close();
        GC.KeepAlive(_callback);
    }
}

/// <summary>
/// A controllable fake output device with an explicit presented-sample clock.
/// </summary>
/// <remarks>
/// <para>
/// Every playback correctness test runs against this: pause, resume, seek, speed changes, buffering,
/// underruns, device changes, final drain and rapid document switches. A real device cannot be driven
/// deterministically, and a test that sleeps until audio "should" have played is both slow and flaky.
/// </para>
/// <para>
/// The clock advances only when the test says so (<see cref="AdvanceByFrames"/>), or by real elapsed
/// time when <see cref="AutoAdvance"/> is on. That distinction is what makes "no cue advances while
/// media time is stationary" directly assertable: stop advancing the clock and assert nothing moves.
/// </para>
/// </remarks>
public sealed class FakeAudioOutputDevice : IAudioOutputDevice
{
    private readonly object _gate = new();
    private readonly List<byte> _buffer = [];
    private long _presented;
    private long _written;
    private AudioFormat _format;
    private bool _running;
    private bool _opened;
    private long _autoAdvanceOriginTicks;

    public FakeAudioOutputDevice(AudioFormat? format = null) => _format = format ?? AudioFormat.Mono44100Float32;

    /// <summary>Devices offered by <see cref="Enumerate"/>. Empty simulates a machine with no audio server.</summary>
    public IReadOnlyList<AudioDeviceInfo> Devices { get; set; } =
    [
        new AudioDeviceInfo("0", "Fake Default Output", true, 44100, 2, 0.0087),
        new AudioDeviceInfo("1", "Fake HDMI", false, 48000, 8, 0.012),
    ];

    /// <summary>Simulated device buffer depth in frames.</summary>
    public int BufferCapacityFrames { get; set; } = 2048;

    /// <summary>When true, <see cref="PresentedFrames"/> advances with real elapsed time.</summary>
    public bool AutoAdvance { get; set; }

    /// <summary>Throw this message from <see cref="Write"/> to simulate a device failure.</summary>
    public string? FailWritesWith { get; set; }

    /// <summary>Throw this message from <see cref="Open"/> to simulate an unusable device.</summary>
    public string? FailOpenWith { get; set; }

    /// <summary>Total frames written since the last flush. Lets a test assert nothing stale was queued.</summary>
    public long TotalWritten { get { lock (_gate) return _written; } }

    /// <summary>Times <see cref="Flush"/> was called — a seek must flush, and a stale generation must too.</summary>
    public int FlushCount { get; private set; }

    public event Action<string>? DeviceFailed;

    public IReadOnlyList<AudioDeviceInfo> Enumerate() => Devices;

    public void Open(string deviceId, AudioFormat format)
    {
        if (FailOpenWith is { } reason) throw new InvalidOperationException(reason);
        lock (_gate)
        {
            _format = format;
            _opened = true;
            _buffer.Clear();
            _presented = 0;
            _written = 0;
            _autoAdvanceOriginTicks = Environment.TickCount64;
        }
        SelectedDeviceId = deviceId;
    }

    public string? SelectedDeviceId { get; private set; }

    public AudioFormat Format { get { lock (_gate) return _format; } }

    public void Start()
    {
        lock (_gate)
        {
            if (!_opened) throw new InvalidOperationException("The fake device is not open.");
            _running = true;
            _autoAdvanceOriginTicks = Environment.TickCount64;
        }
    }

    public void Pause() { lock (_gate) { _running = false; SettleAutoAdvance(); } }

    public void Flush()
    {
        lock (_gate)
        {
            SettleAutoAdvance();
            _buffer.Clear();
            _presented = 0;
            _written = 0;
            _running = false;
            FlushCount++;
        }
    }

    public void Close() { lock (_gate) { _running = false; _opened = false; _buffer.Clear(); } }

    /// <summary>Releases the fake device. No native resources, so this only marks it closed.</summary>
    public void Dispose() => Close();

    public bool IsRunning { get { lock (_gate) return _running; } }

    public long PresentedFrames { get { lock (_gate) { SettleAutoAdvance(); return _presented; } } }

    public long QueuedFrames { get { lock (_gate) { SettleAutoAdvance(); return Math.Max(0, _written - _presented); } } }

    public double? ReportedLatencySeconds => 0.0;

    public int Write(ReadOnlySpan<byte> pcm, int frames)
    {
        if (FailWritesWith is { } reason)
        {
            DeviceFailed?.Invoke(reason);
            throw new InvalidOperationException(reason);
        }
        lock (_gate)
        {
            SettleAutoAdvance();
            int frameBytes = Math.Max(1, _format.BytesPerFrame);
            int bytes = Math.Min(pcm.Length, frames * frameBytes);
            for (int i = 0; i < bytes; i++) _buffer.Add(pcm[i]);
            _written += bytes / frameBytes;
            return bytes / frameBytes;
        }
    }

    public bool HasDrained { get { lock (_gate) { SettleAutoAdvance(); return QueuedFramesCore() <= 0; } } }

    /// <summary>Advances the presented clock by an exact number of frames, consuming buffered audio.</summary>
    /// <remarks>A stopped or paused device presents nothing, exactly as real hardware does.</remarks>
    public void AdvanceByFrames(long frames)
    {
        lock (_gate)
        {
            AutoAdvance = false;
            if (!_running) return;
            long queued = QueuedFramesCore();
            long advance = Math.Min(frames, queued);
            _presented += advance;
            Consume(advance);
        }
    }

    /// <summary>Advances the presented clock by a duration at the current format's rate.</summary>
    public void AdvanceBySeconds(double seconds)
    {
        lock (_gate) AutoAdvance = false;
        AdvanceByFrames((long)Math.Round(seconds * _format.SampleRate, MidpointRounding.AwayFromZero));
    }

    /// <summary>Drains everything still queued, as the end of a clip would.</summary>
    public void DrainAll()
    {
        lock (_gate)
        {
            AutoAdvance = false;
            if (!_running) return;
            long queued = QueuedFramesCore();
            _presented += queued;
            Consume(queued);
        }
    }

    private long QueuedFramesCore() => Math.Max(0, _written - _presented);

    private void Consume(long frames)
    {
        int frameBytes = Math.Max(1, _format.BytesPerFrame);
        int bytes = (int)Math.Min(frames * frameBytes, _buffer.Count);
        if (bytes > 0) _buffer.RemoveRange(0, bytes);
    }

    private void SettleAutoAdvance()
    {
        if (!AutoAdvance || !_running) return;
        long now = Environment.TickCount64;
        long elapsedMs = now - _autoAdvanceOriginTicks;
        _autoAdvanceOriginTicks = now;
        if (elapsedMs <= 0) return;
        long frames = elapsedMs * _format.SampleRate / 1000;
        long queued = QueuedFramesCore();
        long advance = Math.Min(frames, queued);
        _presented += advance;
        Consume(advance);
    }
}
