using RailNarrator.Domain;

namespace RailNarrator.Audio;

/// <summary>Engine tuning.</summary>
/// <param name="DeviceId">Output device, or null for the system default.</param>
/// <param name="WriteChunkFrames">Frames per device write. Small enough to keep seeks responsive, large enough not to syscall per frame.</param>
/// <param name="PreferredDeviceRate">Override for the device rate. Null uses the device's own preferred rate.</param>
/// <param name="WriterPollMilliseconds">Writer loop poll interval while waiting for state changes.</param>
public sealed record AudioEngineOptions(
    string? DeviceId = null,
    int WriteChunkFrames = 1024,
    int? PreferredDeviceRate = null,
    int WriterPollMilliseconds = 10);

/// <summary>
/// Buffered clip playback with a media clock derived from what the device has actually presented.
/// </summary>
/// <remarks>
/// <para>
/// The clock is the point of this class. <see cref="PositionSeconds"/> is computed from the device's
/// presented-frame count mapped back through the rendered WSOLA correspondence and resampling — never from
/// elapsed wall time, bytes received from a provider, or samples handed to the device. Those three
/// all run ahead of what the listener hears, and a highlight driven by any of them visibly leads the
/// audio.
/// </para>
/// <para>
/// Clips are decoded, resampled and time-stretched once at load, then streamed. That is the buffered
/// sentence-clip design rather than a streaming one: it makes a seek a cursor reset, retains the
/// source position of each rendered frame, and means a playback-rate change re-renders audio instead of
/// changing a pitch. Streaming can be added later without changing this contract.
/// </para>
/// <para>
/// End of clip fires only after the device reports it has drained, so the last audible samples are
/// heard before the reading position advances. Firing on "buffer empty" instead would cut off the
/// final word of every sentence.
/// </para>
/// </remarks>
public sealed class AudioPlaybackEngine : IAudioTransport, IAsyncDisposable
{
    private readonly IAudioOutputDevice _device;
    private readonly AudioEngineOptions _options;
    private readonly object _gate = new();

    private float[] _output = [];
    private float[] _sourceFloat = [];
    private AudioFormat _outputFormat;
    private int _sourceRate;
    private long _writeCursor;
    // Source seconds already presented before the current device session. A flush resets the
    // device's presented counter to zero, so without this base a seek would read the position back
    // as zero and the highlight would jump to the start of the clip.
    private long _sessionOutputStart;
    private PlaybackRender _render = new([], 0);
    private string? _segmentId;
    private SpeechAsset? _asset;
    private double _rate = 1.0;
    private PlaybackState _state = PlaybackState.Idle;
    private CancellationTokenSource? _writerCts;
    private Task? _writerTask;
    private bool _disposed;
    private bool _clipCompletedRaised;
    private readonly ManualResetEventSlim _writerGate = new(false);

    public AudioPlaybackEngine(IAudioOutputDevice device, AudioEngineOptions? options = null)
    {
        _device = device ?? throw new ArgumentNullException(nameof(device));
        _options = options ?? new AudioEngineOptions();
        _device.DeviceFailed += OnDeviceFailed;
    }

    public PlaybackState State { get { lock (_gate) return _state; } }

    public AudioFormat? CurrentFormat { get { lock (_gate) return _outputFormat.IsValid ? _outputFormat : null; } }

    /// <summary>Rate of the loaded clip before any conversion, i.e. the timebase cues are counted in.</summary>
    public int SourceSampleRate { get { lock (_gate) return _sourceRate; } }

    public double PlaybackRate { get { lock (_gate) return _rate; } }

    public double PositionSeconds
    {
        get
        {
            lock (_gate)
            {
                if (_outputFormat.SampleRate <= 0) return 0;
                // presented output frames -> output seconds -> source seconds (a rate of R consumes R
                // source-seconds per output-second), plus whatever was presented before the last flush.
                return _render.SourceFrameAt(_sessionOutputStart + _device.PresentedFrames) / _outputFormat.SampleRate;
            }
        }
    }

    public double ClipDurationSeconds
    {
        get
        {
            lock (_gate)
            {
                if (_outputFormat.SampleRate <= 0) return 0;
                return _sourceRate > 0 ? _sourceFloat.Length / (double)_sourceRate : 0;
            }
        }
    }

    public double BufferedAheadSeconds
    {
        get
        {
            lock (_gate)
            {
                if (_outputFormat.SampleRate <= 0) return 0;
                double position = _sessionOutputStart + _device.PresentedFrames;
                return Math.Max(0, _render.SourceFrameAt(_writeCursor) - _render.SourceFrameAt(position)) / _outputFormat.SampleRate;
            }
        }
    }

    public event Action<PlaybackState>? StateChanged;
    public event Action<string>? ClipCompleted;
    public event Action<string>? Underflow;
    public event Action<string>? DeviceFailed;

    public async Task LoadAsync(string segmentId, SpeechAsset asset, ReadOnlyMemory<byte> pcm, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(asset);
        ObjectDisposedException.ThrowIf(_disposed, this);

        await StopWriterAsync().ConfigureAwait(false);

        // Decode to mono float at the clip's own rate. Conversion is exact; resampling is separate so
        // the two mappings never get conflated.
        float[] source = DecodeToMonoFloat(pcm.Span, asset.Format);
        int deviceRate = ResolveDeviceRate();
        var deviceFormat = new AudioFormat(deviceRate, 1, SampleFormat.Float32LittleEndian);

        float[] atDeviceRate = Resampler.Resample(source, asset.Format.SampleRate, deviceRate);

        lock (_gate)
        {
            _segmentId = segmentId;
            _asset = asset;
            _sourceRate = asset.Format.SampleRate;
            _outputFormat = deviceFormat;
            _sourceFloat = source;
            _render = ApplyRate(atDeviceRate, deviceRate, _rate);
            _output = _render.Samples;
            _writeCursor = 0;
            _sessionOutputStart = 0;
            _clipCompletedRaised = false;
        }

        EnsureDeviceOpen(deviceFormat);
        SetState(PlaybackState.Buffering);
    }

    /// <summary>
    /// Changes the playback rate, preserving pitch and the reading position.
    /// </summary>
    /// <remarks>
    /// The clip is re-rendered from the stored source samples rather than stretched twice: compounding
    /// WSOLA passes add artifacts, and re-rendering keeps one monotonic source-to-output map
    /// that the media clock can invert. The position is preserved in <i>source</i>
    /// seconds, which is the quantity a cue is expressed in, so the highlight stays on the same words
    /// across the change.
    /// </remarks>
    public void SetPlaybackRate(double rate)
    {
        if (!double.IsFinite(rate)) throw new ArgumentOutOfRangeException(nameof(rate));
        double clamped = Math.Clamp(rate, WsolaTimeStretcher.MinRate, WsolaTimeStretcher.MaxRate);
        if (Math.Abs(clamped - PlaybackRate) < 1e-9) return;
        // Stop the producer before swapping buffers or flushing. A stale write after a flush would
        // otherwise queue old-rate samples into the new session even when its cursor is discarded.
        StopWriterAsync().GetAwaiter().GetResult();
        bool playing = State == PlaybackState.Playing;
        lock (_gate)
        {
            double sourceSeconds = PositionSeconds;
            _device.Pause();
            _device.Flush();
            _rate = clamped;
            if (_sourceFloat.Length > 0 && _outputFormat.SampleRate > 0)
            {
                var source = Resampler.Resample(_sourceFloat, _sourceRate, _outputFormat.SampleRate);
                _render = ApplyRate(source, _outputFormat.SampleRate, clamped);
                _output = _render.Samples;
                _writeCursor = _render.OutputFrameAt(sourceSeconds * _outputFormat.SampleRate);
                _sessionOutputStart = _writeCursor;
                _clipCompletedRaised = false;
            }
        }
        if (playing) { _device.Start(); StartWriter(); }
    }

    private static PlaybackRender ApplyRate(float[] atDeviceRate, int deviceRate, double rate)
        => new WsolaTimeStretcher(deviceRate).Render(atDeviceRate, rate);

    private static float[] DecodeToMonoFloat(ReadOnlySpan<byte> pcm, AudioFormat format)
    {
        int stride = format.BytesPerFrame;
        if (stride <= 0) return [];
        int frames = pcm.Length / stride;
        int sampleBytes = PcmBytesPerSample(format.SampleFormat);
        var result = new float[frames];
        for (int f = 0; f < frames; f++)
        {
            float sum = 0f;
            for (int c = 0; c < format.Channels; c++)
                sum += ReadSample(pcm, f * stride + c * sampleBytes, format.SampleFormat);
            result[f] = format.Channels > 1 ? sum / format.Channels : sum;
        }
        return result;
    }

    private static int PcmBytesPerSample(SampleFormat format) => format switch
    {
        SampleFormat.Float32LittleEndian => 4,
        SampleFormat.Int16LittleEndian => 2,
        SampleFormat.Int32LittleEndian => 4,
        _ => throw new ArgumentOutOfRangeException(nameof(format)),
    };

    private static float ReadSample(ReadOnlySpan<byte> pcm, int offset, SampleFormat format)
        => format switch
        {
            SampleFormat.Float32LittleEndian => System.Buffers.Binary.BinaryPrimitives.ReadSingleLittleEndian(pcm.Slice(offset, 4)),
            SampleFormat.Int16LittleEndian => System.Buffers.Binary.BinaryPrimitives.ReadInt16LittleEndian(pcm.Slice(offset, 2)) / 32768f,
            SampleFormat.Int32LittleEndian => System.Buffers.Binary.BinaryPrimitives.ReadInt32LittleEndian(pcm.Slice(offset, 4)) / 2147483648f,
            _ => 0f,
        };

    private int ResolveDeviceRate()
    {
        if (_options.PreferredDeviceRate is { } forced) return forced;
        var devices = _device.Enumerate();
        var chosen = _options.DeviceId is { } id ? devices.FirstOrDefault(d => d.Id == id) : devices.FirstOrDefault(d => d.IsDefault);
        chosen ??= devices.FirstOrDefault();
        // A device that reports no preferred rate still needs one; 44.1 kHz is what the reference
        // machine's devices report and what the speech providers natively produce or convert to.
        return chosen is { PreferredSampleRate: > 0 } c ? c.PreferredSampleRate : 44100;
    }

    private void EnsureDeviceOpen(AudioFormat format)
    {
        try
        {
            _device.Open(_options.DeviceId ?? _device.Enumerate().FirstOrDefault(d => d.IsDefault)?.Id ?? "0", format);
        }
        catch (Exception ex)
        {
            SetState(PlaybackState.Error);
            DeviceFailed?.Invoke(ex.Message);
            throw;
        }
    }

    public Task PlayAsync(CancellationToken ct = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        lock (_gate)
        {
            if (_segmentId is null || _output.Length == 0)
            {
                SetState(PlaybackState.Buffering);
                return Task.CompletedTask;
            }
            SetState(PlaybackState.Playing);
        }
        _device.Start();
        StartWriter();
        return Task.CompletedTask;
    }

    public async Task PauseAsync(CancellationToken ct = default)
    {
        if (State != PlaybackState.Playing) return;
        await StopWriterAsync().ConfigureAwait(false);
        _device.Pause();
        SetState(PlaybackState.Paused);
    }

    public Task ResumeAsync(CancellationToken ct = default)
    {
        lock (_gate) if (_state != PlaybackState.Paused) return Task.CompletedTask;
        _device.Start();
        SetState(PlaybackState.Playing);
        StartWriter();
        return Task.CompletedTask;
    }

    public async Task StopAsync(CancellationToken ct = default)
    {
        await StopWriterAsync().ConfigureAwait(false);
        _device.Flush();
        lock (_gate)
        {
            _writeCursor = 0;
            _sessionOutputStart = 0;
            _output = [];
            _render = new([], 0);
            _sourceFloat = [];
            _asset = null;
            _segmentId = null;
            SetState(PlaybackState.Idle);
        }
    }

    public async Task SeekAsync(double seconds, CancellationToken ct = default)
    {
        SpeechAsset? asset;
        lock (_gate) asset = _asset;
        if (asset is null) return;

        await StopWriterAsync().ConfigureAwait(false);
        // Flush before repositioning: without it the audio from the old position is still queued and
        // plays over the new one, which is the audible form of a stale-generation bug.
        _device.Flush();

        lock (_gate)
        {
            double clamped = Math.Clamp(seconds, 0, Math.Max(0, ClipDurationSecondsCore()));
            long outputFrame = _render.OutputFrameAt(clamped * _outputFormat.SampleRate);
            _writeCursor = Math.Clamp(outputFrame, 0, _output.Length);
            // The flush below zeroes the device's presented count, so the seek target becomes the new
            // base for the clock rather than being lost.
            _sessionOutputStart = _writeCursor;
            _clipCompletedRaised = false;
        }
        if (State == PlaybackState.Playing) { _device.Start(); StartWriter(); }
    }

    private double ClipDurationSecondsCore()
        => _sourceRate <= 0 ? 0 : _sourceFloat.Length / (double)_sourceRate;

    // ---- writer ------------------------------------------------------------------------

    private void StartWriter()
    {
        _writerGate.Set();
        if (_writerTask is { IsCompleted: false }) return;
        _writerCts = new CancellationTokenSource();
        var ct = _writerCts.Token;
        _writerTask = Task.Run(() => WriterLoopAsync(ct), ct);
    }

    private async Task StopWriterAsync()
    {
        _writerGate.Reset();
        var cts = _writerCts;
        var task = _writerTask;
        _writerCts = null;
        _writerTask = null;
        cts?.Cancel();
        if (task is not null)
        {
            try { await task.ConfigureAwait(false); }
            catch (OperationCanceledException) { }
            catch (Exception) { /* a writer failure is surfaced through DeviceFailed, not here */ }
        }
        cts?.Dispose();
    }

    private async Task WriterLoopAsync(CancellationToken ct)
    {
        var buffer = new byte[_options.WriteChunkFrames * 4];
        try
        {
        while (!ct.IsCancellationRequested)
        {
            PlaybackState state;
            long cursor;
            float[] output;
            string? segmentId;
            // Snapshot the buffer and its cursor together: a rate change swaps _output and
            // re-seats _writeCursor under this same lock, so indexing the field outside the lock
            // could run past a shorter new buffer and then advance the new cursor by a stale chunk.
            lock (_gate)
            {
                state = _state;
                cursor = _writeCursor;
                output = _output;
                segmentId = _segmentId;
            }
            int length = output.Length;

            if (state is not PlaybackState.Playing)
            {
                _writerGate.Wait(TimeSpan.FromMilliseconds(_options.WriterPollMilliseconds), ct);
                continue;
            }

            if (cursor >= length)
            {
                // Everything is queued. Wait for the device to finish presenting it before declaring
                // the clip complete, so the last audible samples are not cut off.
                if (_device.HasDrained)
                {
                    bool raise;
                    lock (_gate) { raise = !_clipCompletedRaised && segmentId is not null; _clipCompletedRaised = true; }
                    if (raise && segmentId is not null)
                    {
                        SetState(PlaybackState.Completed);
                        ClipCompleted?.Invoke(segmentId);
                    }
                    return;
                }
                await Task.Delay(_options.WriterPollMilliseconds, ct).ConfigureAwait(false);
                continue;
            }

            int frames = (int)Math.Min(_options.WriteChunkFrames, length - cursor);
            int bytes = frames * 4;
            for (int i = 0; i < frames; i++)
            {
                float s = Math.Clamp(output[cursor + i], -1f, 1f);
                BitConverter.TryWriteBytes(buffer.AsSpan(i * 4, 4), s);
            }

            int written;
            try
            {
                written = _device.Write(buffer.AsSpan(0, bytes), frames);
            }
            catch (Exception ex)
            {
                SetState(PlaybackState.Error);
                DeviceFailed?.Invoke(ex.Message);
                return;
            }

            if (written <= 0)
            {
                // The device accepted nothing while we still had audio to give: that is an underrun,
                // and the reading position must freeze rather than drift on without sound.
                if (segmentId is not null) Underflow?.Invoke(segmentId);
                SetState(PlaybackState.Buffering);
                await Task.Delay(_options.WriterPollMilliseconds * 2, ct).ConfigureAwait(false);
                continue;
            }

            // Advance only if this buffer is still current and no one re-seated the cursor:
            // otherwise the chunk just written was stale (flushed by the rate change) and the new
            // cursor must not be moved by it.
            lock (_gate)
            {
                if (ReferenceEquals(_output, output) && _writeCursor == cursor)
                    _writeCursor += written;
            }
        }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
        catch (Exception ex)
        {
            // A writer fault that is not a device Write failure must still reach the user;
            // swallowing it in StopWriterAsync left the state stuck at Playing with no audio.
            if (!ct.IsCancellationRequested && !_disposed)
            {
                SetState(PlaybackState.Error);
                DeviceFailed?.Invoke(ex.Message);
            }
        }
    }

    private void SetState(PlaybackState state)
    {
        bool changed;
        lock (_gate) { changed = _state != state; _state = state; }
        if (changed) StateChanged?.Invoke(state);
    }

    private void OnDeviceFailed(string detail)
    {
        SetState(PlaybackState.Error);
        DeviceFailed?.Invoke(detail);
    }

    /// <summary>
    /// Diagnostics for the measured clock, so scheduling error can be reported rather than assumed
    /// away. Residual uncertainty is one device period plus the hardware buffer, because this
    /// backend timestamps are estimates of DAC presentation; loopback measurement is still needed.
    /// </summary>
    public AudioClockDiagnostics GetClockDiagnostics()
    {
        lock (_gate)
        {
            long presented = _device.PresentedFrames;
            return new AudioClockDiagnostics(
                _segmentId, _sourceRate, _outputFormat.SampleRate, _rate,
                _writeCursor - _sessionOutputStart, presented, Math.Max(0, _writeCursor - _sessionOutputStart - presented),
                _device.ReportedLatencySeconds, _output.Length - _sessionOutputStart)
            { SourcePositionSeconds = PositionSeconds };
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        _disposed = true;
        await StopWriterAsync().ConfigureAwait(false);
        _device.DeviceFailed -= OnDeviceFailed;
        _writerGate.Dispose();
    }
}

/// <summary>A measured snapshot of the playback clock, for diagnostics and sync-error measurement.</summary>
/// <param name="WrittenOutputFrames">Frames handed to the device.</param>
/// <param name="PresentedOutputFrames">Frames the device reports as consumed. The clock's only input.</param>
/// <param name="QueuedOutputFrames">Written but not yet presented. This is the latency the clock corrects for.</param>
/// <param name="ReportedLatencySeconds">Backend-reported latency, or null when the backend cannot report one.</param>
/// <param name="TotalOutputFrames">Length of the rendered clip at the device rate.</param>
public sealed record AudioClockDiagnostics(
    string? SegmentId,
    int SourceSampleRate,
    int OutputSampleRate,
    double PlaybackRate,
    long WrittenOutputFrames,
    long PresentedOutputFrames,
    long QueuedOutputFrames,
    double? ReportedLatencySeconds,
    long TotalOutputFrames)
{
    public double PresentedOutputSeconds => OutputSampleRate <= 0 ? 0 : PresentedOutputFrames / (double)OutputSampleRate;
    public double? SourcePositionSeconds { get; init; }
    public double PositionInSourceSeconds => SourcePositionSeconds ?? PresentedOutputSeconds * PlaybackRate;
    public double QueuedSeconds => OutputSampleRate <= 0 ? 0 : QueuedOutputFrames / (double)OutputSampleRate;
    public bool IsComplete => TotalOutputFrames > 0 && PresentedOutputFrames >= TotalOutputFrames;
}
