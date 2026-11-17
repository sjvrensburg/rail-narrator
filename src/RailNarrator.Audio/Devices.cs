using System.Runtime.InteropServices;
using RailNarrator.Domain;

namespace RailNarrator.Audio;

/// <summary>
/// P/Invoke bindings for PortAudio, restricted to the calls this product uses.
/// </summary>
/// <remarks>
/// <para>
/// The blocking interface is used rather than the callback interface, and that choice is measured
/// rather than stylistic. On the reference machine (PortAudio V19.7.0-devel, ALSA host, Fedora 44):
/// </para>
/// <list type="bullet">
/// <item><c>Pa_GetStreamTime</c> returns 0.0000 s throughout a callback stream that had already
/// delivered 0.39 s of audio, so it cannot serve as a media clock.</item>
/// <item><c>Pa_GetStreamLatency</c> is not exported by the shipped library at all
/// (<c>EntryPointNotFoundException</c>), so latency cannot be queried that way either.</item>
/// <item><c>Pa_GetStreamWriteAvailable</c> on a blocking stream tracks device consumption precisely,
/// and <c>Pa_WriteStream</c> blocks in real time — writing 0.44 s took 0.40 s of wall clock, exactly
/// the buffer depth of 46.4 ms less.</item>
/// </list>
/// <para>
/// So the presented position is derived as <c>written − (capacity − writeAvailable)</c>, which is a
/// genuine media position: not wall-clock time, not bytes received, and not samples merely queued.
/// </para>
/// <para>
/// Opening at the device's native rate is mandatory. The same spike showed
/// <c>Pa_OpenDefaultStream</c> at 24 kHz failing with <c>paInvalidSampleRate</c> on a device whose
/// default rate is 44.1 kHz, because the ALSA host does not resample.
/// </para>
/// </remarks>
internal static class PortAudioNative
{
    private const string Library = "libportaudio.so.2";

    /// <summary>paFloat32</summary>
    public const uint SampleFormatFloat32 = 0x00000001;

    /// <summary>paInt16</summary>
    public const uint SampleFormatInt16 = 0x00000008;

    /// <summary>paNoDevice</summary>
    public const int NoDevice = -1;

    [StructLayout(LayoutKind.Sequential)]
    public struct DeviceInfo
    {
        public int structVersion;
        public IntPtr name;
        public int hostApi;
        public int maxInputChannels;
        public int maxOutputChannels;
        public double defaultLowInputLatency;
        public double defaultLowOutputLatency;
        public double defaultHighInputLatency;
        public double defaultHighOutputLatency;
        public double defaultSampleRate;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct StreamParameters
    {
        public int device;
        public int channelCount;
        public uint sampleFormat;
        public double suggestedLatency;
        public IntPtr hostApiSpecificStreamInfo;
    }

    [DllImport(Library)] public static extern int Pa_Initialize();
    [DllImport(Library)] public static extern int Pa_Terminate();
    [DllImport(Library)] public static extern IntPtr Pa_GetVersionText();
    [DllImport(Library)] public static extern int Pa_GetDeviceCount();
    [DllImport(Library)] public static extern int Pa_GetDefaultOutputDevice();
    [DllImport(Library)] public static extern IntPtr Pa_GetDeviceInfo(int device);
    [DllImport(Library)] public static extern IntPtr Pa_GetErrorText(int error);
    [DllImport(Library)] public static extern int Pa_IsFormatSupported(IntPtr input, ref StreamParameters output, double sampleRate);
    [DllImport(Library)]
    public static extern int Pa_OpenStream(
        out IntPtr stream, IntPtr inputParameters, ref StreamParameters outputParameters,
        double sampleRate, uint framesPerBuffer, uint streamFlags, IntPtr callback, IntPtr userData);
    [DllImport(Library)] public static extern int Pa_StartStream(IntPtr stream);
    [DllImport(Library)] public static extern int Pa_StopStream(IntPtr stream);
    [DllImport(Library)] public static extern int Pa_AbortStream(IntPtr stream);
    [DllImport(Library)] public static extern int Pa_CloseStream(IntPtr stream);
    [DllImport(Library)] public static extern int Pa_IsStreamActive(IntPtr stream);
    [DllImport(Library)] public static extern int Pa_WriteStream(IntPtr stream, byte[] buffer, uint frames);
    [DllImport(Library)] public static extern int Pa_GetStreamWriteAvailable(IntPtr stream);

    public static string Describe(int code) => Marshal.PtrToStringAnsi(Pa_GetErrorText(code)) ?? $"error {code}";

    public static string? VersionText() => Marshal.PtrToStringAnsi(Pa_GetVersionText());
}

/// <summary>
/// Real audio output through PortAudio.
/// </summary>
/// <remarks>
/// Native dependency: <c>libportaudio.so.2</c>. On Fedora it is <c>portaudio</c>; on Debian/Ubuntu
/// <c>libportaudio2</c>. Enumerating devices must not throw when there is no audio server — an
/// agent session or a headless container has none — so failures are reported as an empty device list
/// and a clear message on <see cref="Open"/>, not as an unhandled exception at startup.
/// </remarks>
public sealed class PortAudioOutputDevice : IAudioOutputDevice
{
    /// <summary>Frames per blocking write. 512 measured as the device's own period on the reference machine.</summary>
    public const int FramesPerBuffer = 512;

    private static readonly object InitLock = new();
    private static bool _initialised;
    private static string? _initError;

    private IntPtr _stream;
    private long _framesWritten;
    private int _bufferCapacityFrames;
    private AudioFormat _format;
    private bool _running;
    private bool _disposed;
    private readonly object _gate = new();

    public event Action<string>? DeviceFailed;

    /// <summary>Initialises PortAudio once per process. Returns the failure reason, or null.</summary>
    public static string? Initialise()
    {
        lock (InitLock)
        {
            if (_initialised) return _initError;
            try
            {
                int rc = PortAudioNative.Pa_Initialize();
                _initError = rc == 0 ? null : $"Pa_Initialize failed: {PortAudioNative.Describe(rc)}";
            }
            catch (DllNotFoundException ex)
            {
                _initError = "libportaudio.so.2 was not found. Install the 'portaudio' package.";
                _ = ex;
            }
            catch (EntryPointNotFoundException ex)
            {
                _initError = $"The installed libportaudio is too old or incompatible: {ex.Message}";
            }
            _initialised = true;
            return _initError;
        }
    }

    /// <summary>The PortAudio version string, for diagnostics.</summary>
    public static string? Version => PortAudioNative.VersionText();

    public IReadOnlyList<AudioDeviceInfo> Enumerate()
    {
        if (Initialise() is { } error) { _ = error; return []; }

        var devices = new List<AudioDeviceInfo>();
        int count = PortAudioNative.Pa_GetDeviceCount();
        int defaultDevice = PortAudioNative.Pa_GetDefaultOutputDevice();
        for (int i = 0; i < count; i++)
        {
            IntPtr infoPtr = PortAudioNative.Pa_GetDeviceInfo(i);
            if (infoPtr == IntPtr.Zero) continue;
            var info = Marshal.PtrToStructure<PortAudioNative.DeviceInfo>(infoPtr);
            // An input-only device cannot play anything; listing it would offer a choice that fails.
            if (info.maxOutputChannels <= 0) continue;
            devices.Add(new AudioDeviceInfo(
                Id: i.ToString(System.Globalization.CultureInfo.InvariantCulture),
                Name: Marshal.PtrToStringAnsi(info.name) ?? $"device {i}",
                IsDefault: i == defaultDevice,
                PreferredSampleRate: info.defaultSampleRate > 0 ? (int)info.defaultSampleRate : 44100,
                Channels: info.maxOutputChannels,
                OutputLatencySeconds: info.defaultLowOutputLatency > 0 ? info.defaultLowOutputLatency : null));
        }
        return devices;
    }

    public void Open(string deviceId, AudioFormat format)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (Initialise() is { } error) throw new InvalidOperationException(error);

        lock (_gate)
        {
            CloseCore();

            int device = int.TryParse(deviceId, out int parsed) ? parsed : PortAudioNative.Pa_GetDefaultOutputDevice();
            if (device == PortAudioNative.NoDevice)
                throw new InvalidOperationException(
                    "No audio output device is available. On a headless session there is no sound server to open; start one or choose a device.");

            uint sampleFormat = format.SampleFormat switch
            {
                SampleFormat.Float32LittleEndian => PortAudioNative.SampleFormatFloat32,
                SampleFormat.Int16LittleEndian => PortAudioNative.SampleFormatInt16,
                _ => throw new NotSupportedException($"PortAudio output does not support {format.SampleFormat}."),
            };

            var output = new PortAudioNative.StreamParameters
            {
                device = device,
                channelCount = Math.Max(1, format.Channels),
                sampleFormat = sampleFormat,
                // Ask for the low-latency figure the device advertises; the host clamps to what it
                // can actually deliver, and the resulting buffer depth is read back below.
                suggestedLatency = 0.05,
                hostApiSpecificStreamInfo = IntPtr.Zero,
            };

            int rc = PortAudioNative.Pa_OpenStream(
                out _stream, IntPtr.Zero, ref output, format.SampleRate, FramesPerBuffer,
                0 /* paNoFlag */, IntPtr.Zero, IntPtr.Zero);
            if (rc != 0)
                throw new InvalidOperationException(
                    $"Cannot open audio device {device} at {format.SampleRate} Hz: {PortAudioNative.Describe(rc)}. " +
                    "Devices commonly reject rates other than their own, so open at the device's preferred rate and resample.");

            _format = format;
            _framesWritten = 0;
            // Capacity is whatever the host actually allocated, not what was requested.
            int available = PortAudioNative.Pa_GetStreamWriteAvailable(_stream);
            _bufferCapacityFrames = available > 0 ? available : FramesPerBuffer * 4;
        }
    }

    /// <summary>Device buffer depth in frames, measured after <see cref="Open"/>. Used to convert queued frames into latency.</summary>
    public int BufferCapacityFrames { get { lock (_gate) return _bufferCapacityFrames; } }

    public void Start()
    {
        lock (_gate)
        {
            if (_stream == IntPtr.Zero) throw new InvalidOperationException("The audio device is not open.");
            if (_running) return;
            int rc = PortAudioNative.Pa_StartStream(_stream);
            if (rc != 0) throw new InvalidOperationException($"Cannot start audio playback: {PortAudioNative.Describe(rc)}");
            _running = true;
        }
    }

    public void Pause()
    {
        lock (_gate)
        {
            if (_stream == IntPtr.Zero || !_running) return;
            // Stop (not abort) so already-queued audio finishes rather than being cut mid-word.
            PortAudioNative.Pa_StopStream(_stream);
            _running = false;
        }
    }

    public void Flush()
    {
        lock (_gate)
        {
            if (_stream == IntPtr.Zero) return;
            // Abort discards the buffer immediately. A seek must not leave the previous position's
            // audio playing over the new one.
            PortAudioNative.Pa_AbortStream(_stream);
            _framesWritten = 0;
            _running = false;
        }
    }

    public void Close()
    {
        lock (_gate) CloseCore();
    }

    private void CloseCore()
    {
        if (_stream == IntPtr.Zero) return;
        try
        {
            if (_running) PortAudioNative.Pa_AbortStream(_stream);
            PortAudioNative.Pa_CloseStream(_stream);
        }
        catch { /* a device that vanished mid-close is already gone */ }
        _stream = IntPtr.Zero;
        _running = false;
        _framesWritten = 0;
    }

    public bool IsRunning { get { lock (_gate) return _running; } }

    public long PresentedFrames
    {
        get
        {
            lock (_gate)
            {
                if (_stream == IntPtr.Zero) return _framesWritten;
                int available = PortAudioNative.Pa_GetStreamWriteAvailable(_stream);
                long queued = available >= 0 ? Math.Max(0, _bufferCapacityFrames - available) : 0;
                return Math.Max(0, _framesWritten - queued);
            }
        }
    }

    public long QueuedFrames
    {
        get
        {
            lock (_gate)
            {
                if (_stream == IntPtr.Zero) return 0;
                int available = PortAudioNative.Pa_GetStreamWriteAvailable(_stream);
                return available >= 0 ? Math.Max(0, _bufferCapacityFrames - available) : 0;
            }
        }
    }

    /// <summary>
    /// Null: this PortAudio build does not export <c>Pa_GetStreamLatency</c>, so latency is estimated
    /// from the device's advertised figure and its measured buffer depth instead of claimed exactly.
    /// Pretending to a precision the backend cannot supply would hide real scheduling error.
    /// </summary>
    public double? ReportedLatencySeconds => null;

    public int Write(ReadOnlySpan<byte> pcm, int frames)
    {
        if (frames <= 0) return 0;
        byte[] buffer = pcm.ToArray();
        int rc;
        lock (_gate)
        {
            if (_stream == IntPtr.Zero || !_running) return 0;
            rc = PortAudioNative.Pa_WriteStream(_stream, buffer, (uint)frames);
        }
        if (rc != 0)
        {
            string detail = PortAudioNative.Describe(rc);
            DeviceFailed?.Invoke($"Write failed: {detail}");
            throw new InvalidOperationException($"Audio write failed: {detail}");
        }
        Interlocked.Add(ref _framesWritten, frames);
        return frames;
    }

    public bool HasDrained
    {
        get
        {
            lock (_gate)
            {
                if (_stream == IntPtr.Zero) return true;
                int available = PortAudioNative.Pa_GetStreamWriteAvailable(_stream);
                return available >= 0 && available >= _bufferCapacityFrames;
            }
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        Close();
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
    public void AdvanceByFrames(long frames)
    {
        lock (_gate)
        {
            AutoAdvance = false;
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
