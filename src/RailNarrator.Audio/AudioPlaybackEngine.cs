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
/// <para>
/// Gapless: a clip queued with <see cref="QueueNextAsync"/> is rendered ahead of time and written
/// straight after the current clip's last frame, so the device never runs dry between passages.
/// Waiting for the drain and then writing the next clip cost the whole output latency (~330 ms on
/// the reference machine) at every boundary. The clock and the current segment switch over when the
/// device reports the boundary frame <i>presented</i>, not when it was written, and
/// <see cref="ClipAdvanced"/> says so.
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
    // The device's presented-frame count at which the current clip's _sessionOutputStart frame was
    // presented. Zero after an open or a flush (both reset the counter); after a gapless switch it is
    // the boundary, since the device keeps counting across clips.
    private long _deviceBase;
    // The clip queued to follow without a gap, and how far into it the writer has got.
    private QueuedClip? _next;
    private PlaybackRender _render = new([], 0);
    private string? _segmentId;
    private SpeechAsset? _asset;
    private double _rate = 1.0;
    // The rate last asked for, which a rate change applies when its turn comes: changes asked for
    // while one renders collapse into one render at the last of them.
    private double _requestedRate = 1.0;
    private PlaybackState _state = PlaybackState.Idle;
    private CancellationTokenSource? _writerCts;
    private Task? _writerTask;
    /// <summary>
    /// Writers being stopped and not yet exited. A second <see cref="StopWriterAsync"/> found no
    /// writer and returned at once while the first was still waiting for it, so a writer could still
    /// run after "stopped". Guarded by <see cref="_writerLock"/> with the two fields above.
    /// </summary>
    private Task? _retiringWriter;
    private readonly object _writerLock = new();
    /// <summary>
    /// Runs the operations that stop the writer or render a clip (stop, load, pause, seek, a rate
    /// change, queueing the next clip) one at a time and in the order they were asked. Two seeks in quick succession each stop the
    /// transport: the first one's stop awaited the writer and then flushed and emptied the engine,
    /// after the second had already loaded and started its clip, leaving the coordinator playing a
    /// transport that was idle (seen on Windows, where it was common; possible anywhere). This is
    /// not <see cref="_gate"/>: events are raised while it is held, which the coordinator handles
    /// without waiting on the engine (its work runs on <c>Task.Run</c>).
    /// </summary>
    private readonly SemaphoreSlim _operations = new(1, 1);
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
                return _render.SourceFrameAt(CurrentOutputFrameCore()) / _outputFormat.SampleRate;
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
                double position = CurrentOutputFrameCore();
                return Math.Max(0, _render.SourceFrameAt(_writeCursor) - _render.SourceFrameAt(position)) / _outputFormat.SampleRate;
            }
        }
    }

    /// <summary>The presented frame within the current clip's rendered output. Call with the lock held.</summary>
    private long CurrentOutputFrameCore()
        => Math.Min(_output.Length, _sessionOutputStart + Math.Max(0, _device.PresentedFrames - _deviceBase));

    public event Action<PlaybackState>? StateChanged;
    public event Action<string>? ClipCompleted;

    /// <summary>
    /// A queued clip took over without a gap: the first is (completed, the second) now playing.
    /// Raised outside the engine lock, once the boundary frame has been presented.
    /// </summary>
    public event Action<string, string>? ClipAdvanced;

    /// <summary>The segment whose audio the clock currently measures, or null.</summary>
    public string? CurrentSegmentId { get { lock (_gate) return _segmentId; } }

    /// <summary>The segment queued to follow without a gap, or null.</summary>
    public string? QueuedSegmentId { get { lock (_gate) return _next?.SegmentId; } }
    public event Action<string>? Underflow;
    public event Action<string>? DeviceFailed;

    public async Task LoadAsync(string segmentId, SpeechAsset asset, ReadOnlyMemory<byte> pcm, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(asset);
        ObjectDisposedException.ThrowIf(_disposed, this);

        await _operations.WaitAsync(CancellationToken.None).ConfigureAwait(false);
        // On the thread pool: decoding, resampling and time-stretching a long passage takes a tenth
        // of a second or more, and a caller on the UI thread whose awaits all completed at once
        // (Play with the passage already prepared) did all of it there, freezing the window.
        try { await Task.Run(() => LoadCoreAsync(segmentId, asset, pcm)).ConfigureAwait(false); }
        finally { _operations.Release(); }
    }

    private async Task LoadCoreAsync(string segmentId, SpeechAsset asset, ReadOnlyMemory<byte> pcm)
    {
        await StopWriterAsync().ConfigureAwait(false);

        // Decode to mono float at the clip's own rate. Conversion is exact; resampling is separate so
        // the two mappings never get conflated.
        float[] source = DecodeToMonoFloat(pcm.Span, asset.Format);
        int deviceRate = ResolveDeviceRate();
        var deviceFormat = new AudioFormat(deviceRate, 1, SampleFormat.Float32LittleEndian);

        float[] atDeviceRate = Resampler.Resample(source, asset.Format.SampleRate, deviceRate);
        // Outside the lock, which the window takes every frame to read the position. The rate
        // cannot change meanwhile: a rate change waits for this operation.
        double rate;
        lock (_gate) rate = _rate;
        var render = ApplyRate(atDeviceRate, deviceRate, rate);

        lock (_gate)
        {
            _segmentId = segmentId;
            _asset = asset;
            _sourceRate = asset.Format.SampleRate;
            _outputFormat = deviceFormat;
            _sourceFloat = source;
            _render = render;
            _output = _render.Samples;
            _writeCursor = 0;
            _sessionOutputStart = 0;
            _deviceBase = 0;
            _next = null;
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
    /// <para>
    /// The render takes a tenth of a second or more for a long passage, so it runs on the thread
    /// pool, in turn with the other operations, and outside the engine lock, which the window takes
    /// every frame to read the position. Done on the caller's thread inside the lock, every change
    /// of speed froze the window for 50-180 ms. Changes asked for while one renders are applied as
    /// one, at the last rate asked for.
    /// </para>
    /// </remarks>
    public async Task SetPlaybackRateAsync(double rate, CancellationToken ct = default)
    {
        if (!double.IsFinite(rate)) throw new ArgumentOutOfRangeException(nameof(rate));
        ObjectDisposedException.ThrowIf(_disposed, this);
        Interlocked.Exchange(ref _requestedRate, Math.Clamp(rate, WsolaTimeStretcher.MinRate, WsolaTimeStretcher.MaxRate));
        await _operations.WaitAsync(CancellationToken.None).ConfigureAwait(false);
        try { await Task.Run(ApplyRequestedRateAsync, CancellationToken.None).ConfigureAwait(false); }
        finally { _operations.Release(); }
    }

    private async Task ApplyRequestedRateAsync()
    {
        double rate = Volatile.Read(ref _requestedRate);
        if (Math.Abs(rate - PlaybackRate) < 1e-9) return;
        // Stop the producer before swapping buffers or flushing. A stale write after a flush would
        // otherwise queue old-rate samples into the new session even when its cursor is discarded.
        await StopWriterAsync().ConfigureAwait(false);
        // The device may have presented the queued clip's first frames before the writer, now
        // stopped, saw it: that clip is current. Left queued, it would be written again from its
        // start below, and the part already heard heard twice.
        if (TryPromoteQueued() is { } advanced) ClipAdvanced?.Invoke(advanced.Completed, advanced.Started);
        bool playing = State == PlaybackState.Playing;
        double sourceSeconds;
        float[] source;
        int sourceRate, outputRate;
        QueuedClip? queued;
        lock (_gate)
        {
            sourceSeconds = PositionSeconds;
            // Paused while the clips render, so the presented count, and with it the position the
            // window reads, holds still until the new render takes over.
            _device.Pause();
            source = _sourceFloat;
            sourceRate = _sourceRate;
            outputRate = _outputFormat.SampleRate;
            queued = _next;
        }
        // Nothing else changes the clips meanwhile: loading, stopping, seeking and queueing wait for
        // this operation, and the writer is stopped.
        var render = source.Length > 0 && outputRate > 0
            ? ApplyRate(Resampler.Resample(source, sourceRate, outputRate), outputRate, rate) : null;
        var nextRender = queued is not null && outputRate > 0
            ? ApplyRate(Resampler.Resample(queued.Source, queued.SourceRate, outputRate), outputRate, rate) : null;
        lock (_gate)
        {
            _device.Flush();
            _rate = rate;
            if (render is not null)
            {
                _render = render;
                _output = _render.Samples;
                _writeCursor = _render.OutputFrameAt(sourceSeconds * _outputFormat.SampleRate);
                _sessionOutputStart = _writeCursor;
                _deviceBase = 0;
                _clipCompletedRaised = false;
            }
            // The flush discarded whatever of the queued clip was written; it is written again from
            // its start after the current clip, at the new rate.
            if (queued is not null && nextRender is not null)
                _next = queued with { Render = nextRender, WriteCursor = 0, Boundary = null };
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

    /// <remarks>
    /// Play and Resume take their turn with the other operations. A Resume pressed while a speed
    /// change rendered started the device, and the change's flush then stopped it again: Playing,
    /// with no sound and nothing left to start the device.
    /// </remarks>
    public async Task PlayAsync(CancellationToken ct = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        await _operations.WaitAsync(CancellationToken.None).ConfigureAwait(false);
        try { PlayCore(); }
        finally { _operations.Release(); }
    }

    private void PlayCore()
    {
        bool nothingLoaded;
        lock (_gate) nothingLoaded = _segmentId is null || _output.Length == 0;
        // State events are raised outside the engine lock. A subscriber (the coordinator) takes its
        // own lock and reads this engine's position from other threads while holding it; raising
        // under the lock here is the other half of that inversion and deadlocked the UI thread.
        if (nothingLoaded)
        {
            SetState(PlaybackState.Buffering);
            return;
        }
        SetState(PlaybackState.Playing);
        _device.Start();
        StartWriter();
    }

    public async Task PauseAsync(CancellationToken ct = default)
    {
        await _operations.WaitAsync(CancellationToken.None).ConfigureAwait(false);
        try
        {
            if (State != PlaybackState.Playing) return;
            await StopWriterAsync().ConfigureAwait(false);
            _device.Pause();
            SetState(PlaybackState.Paused);
        }
        finally { _operations.Release(); }
    }

    /// <remarks>In turn with the other operations: see <see cref="PlayAsync"/>.</remarks>
    public async Task ResumeAsync(CancellationToken ct = default)
    {
        await _operations.WaitAsync(CancellationToken.None).ConfigureAwait(false);
        try
        {
            lock (_gate) if (_state != PlaybackState.Paused) return;
            _device.Start();
            SetState(PlaybackState.Playing);
            StartWriter();
        }
        finally { _operations.Release(); }
    }

    public async Task StopAsync(CancellationToken ct = default)
    {
        await _operations.WaitAsync(CancellationToken.None).ConfigureAwait(false);
        try { await StopCoreAsync().ConfigureAwait(false); }
        finally { _operations.Release(); }
    }

    private async Task StopCoreAsync()
    {
        await StopWriterAsync().ConfigureAwait(false);
        _device.Flush();
        lock (_gate)
        {
            _writeCursor = 0;
            _sessionOutputStart = 0;
            _deviceBase = 0;
            _next = null;
            _output = [];
            _render = new([], 0);
            _sourceFloat = [];
            _asset = null;
            _segmentId = null;
        }
        SetState(PlaybackState.Idle);   // outside the lock: see PlayAsync
    }

    public async Task SeekAsync(double seconds, CancellationToken ct = default)
    {
        await _operations.WaitAsync(CancellationToken.None).ConfigureAwait(false);
        try { await SeekCoreAsync(seconds).ConfigureAwait(false); }
        finally { _operations.Release(); }
    }

    private async Task SeekCoreAsync(double seconds)
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
            _deviceBase = 0;
            // A queued clip may already be partly written; the flush discarded that, so it is written
            // again from its start.
            if (_next is { } next) _next = next with { WriteCursor = 0, Boundary = null };
            _clipCompletedRaised = false;
        }
        if (State == PlaybackState.Playing) { _device.Start(); StartWriter(); }
    }

    /// <summary>
    /// Renders a clip to follow the current one without a gap. Decoding, resampling and time
    /// stretching happen here, off the boundary. Replaces any clip already queued that has not begun
    /// to be written. Returns false when it is too late: nothing is loaded, the current clip has
    /// already completed, or another queued clip is already being written.
    /// </summary>
    /// <remarks>
    /// In turn with the other operations: a clip rendered beside a rate change could be queued at
    /// the old rate after the change had re-rendered the clips it found.
    /// </remarks>
    public async Task<bool> QueueNextAsync(string segmentId, SpeechAsset asset, ReadOnlyMemory<byte> pcm, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(asset);
        ObjectDisposedException.ThrowIf(_disposed, this);
        await _operations.WaitAsync(CancellationToken.None).ConfigureAwait(false);
        try { return await Task.Run(() => QueueNextCore(segmentId, asset, pcm), CancellationToken.None).ConfigureAwait(false); }
        finally { _operations.Release(); }
    }

    private bool QueueNextCore(string segmentId, SpeechAsset asset, ReadOnlyMemory<byte> pcm)
    {
        int deviceRate;
        double rate;
        lock (_gate)
        {
            if (_segmentId is null || _clipCompletedRaised || _outputFormat.SampleRate <= 0) return false;
            if (_next is { Boundary: not null }) return false;
            deviceRate = _outputFormat.SampleRate;
            rate = _rate;
        }
        float[] source = DecodeToMonoFloat(pcm.Span, asset.Format);
        var render = ApplyRate(Resampler.Resample(source, asset.Format.SampleRate, deviceRate), deviceRate, rate);
        lock (_gate)
        {
            // Re-check: the clip may have completed, been stopped, or the rate or device changed while
            // this was rendering.
            if (_segmentId is null || _clipCompletedRaised || _outputFormat.SampleRate != deviceRate
                || Math.Abs(_rate - rate) > 1e-9 || _next is { Boundary: not null })
                return false;
            _next = new QueuedClip(segmentId, asset, source, asset.Format.SampleRate, render, 0, null);
        }
        _writerGate.Set();
        return true;
    }

    /// <summary>
    /// Forgets the queued clip (its words were edited, say). If the writer has already begun writing
    /// it, the device is flushed and the current clip's tail written again, so the stale clip is never
    /// heard.
    /// </summary>
    public async Task ClearQueuedAsync(CancellationToken ct = default)
    {
        bool written;
        lock (_gate)
        {
            if (_next is null) return;
            written = _next.Boundary is not null;
            if (!written) { _next = null; return; }
        }
        // Part of it is in the device. Re-seat at the current position, which flushes it.
        double position = PositionSeconds;
        lock (_gate) _next = null;
        await SeekAsync(position, ct).ConfigureAwait(false);
    }

    private double ClipDurationSecondsCore()
        => _sourceRate <= 0 ? 0 : _sourceFloat.Length / (double)_sourceRate;

    // ---- writer ------------------------------------------------------------------------

    private void StartWriter()
    {
        _writerGate.Set();
        lock (_writerLock)
        {
            if (_writerTask is { IsCompleted: false }) return;
            _writerCts = new CancellationTokenSource();
            var ct = _writerCts.Token;
            _writerTask = Task.Run(() => WriterLoopAsync(ct), ct);
        }
    }

    /// <summary>
    /// Stops the writer and returns once no writer is running: the one this call stopped and any
    /// another call is still stopping.
    /// </summary>
    private async Task StopWriterAsync()
    {
        _writerGate.Reset();
        CancellationTokenSource? cts;
        Task? wait;
        lock (_writerLock)
        {
            cts = _writerCts;
            var task = _writerTask;
            _writerCts = null;
            _writerTask = null;
            if (task is not null)
                _retiringWriter = _retiringWriter is { IsCompleted: false } retiring ? Task.WhenAll(retiring, task) : task;
            wait = _retiringWriter;
        }
        cts?.Cancel();
        if (wait is not null)
        {
            try { await wait.ConfigureAwait(false); }
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

            // A queued clip whose first frame the device has presented becomes the current one.
            if (TryPromoteQueued() is { } advanced)
            {
                ClipAdvanced?.Invoke(advanced.Completed, advanced.Started);
                continue;
            }

            if (cursor >= length && NextChunkOfQueued() is { } queued)
            {
                // The current clip is all written: carry straight on with the queued one, so the device
                // never runs dry at the boundary.
                if (!await WriteQueuedChunkAsync(queued, buffer, segmentId, ct).ConfigureAwait(false)) return;
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

    /// <summary>
    /// The queued clip and where its next chunk starts, once the current clip is entirely written.
    /// Marks the boundary (the device frame count at which the queued clip begins) on first use.
    /// </summary>
    private (QueuedClip Clip, long Cursor)? NextChunkOfQueued()
    {
        lock (_gate)
        {
            if (_next is not { } next) return null;
            if (next.Boundary is null)
            {
                // Device frames already counted for the current clip, plus what of it is still to be
                // presented: the frame count at which the queued clip's first frame will be presented.
                long boundary = _deviceBase + (_output.Length - _sessionOutputStart);
                _next = next = next with { Boundary = boundary };
            }
            if (next.WriteCursor >= next.Render.Samples.Length) return null;
            return (next, next.WriteCursor);
        }
    }

    private async Task<bool> WriteQueuedChunkAsync((QueuedClip Clip, long Cursor) queued, byte[] buffer, string? segmentId, CancellationToken ct)
    {
        var samples = queued.Clip.Render.Samples;
        int frames = (int)Math.Min(_options.WriteChunkFrames, samples.Length - queued.Cursor);
        for (int i = 0; i < frames; i++)
            BitConverter.TryWriteBytes(buffer.AsSpan(i * 4, 4), Math.Clamp(samples[queued.Cursor + i], -1f, 1f));
        int written;
        try { written = _device.Write(buffer.AsSpan(0, frames * 4), frames); }
        catch (Exception ex)
        {
            SetState(PlaybackState.Error);
            DeviceFailed?.Invoke(ex.Message);
            return false;
        }
        if (written <= 0)
        {
            if (segmentId is not null) Underflow?.Invoke(segmentId);
            SetState(PlaybackState.Buffering);
            await Task.Delay(_options.WriterPollMilliseconds * 2, ct).ConfigureAwait(false);
            return true;
        }
        lock (_gate)
        {
            // Advance only if this queued clip is still the one queued and nobody re-seated it.
            if (ReferenceEquals(_next?.Render, queued.Clip.Render) && _next!.WriteCursor == queued.Cursor)
                _next = _next with { WriteCursor = queued.Cursor + written };
        }
        return true;
    }

    /// <summary>
    /// Makes the queued clip current once the device has presented its first frame. Returns the
    /// segments to report, or null when the boundary has not been reached.
    /// </summary>
    private (string Completed, string Started)? TryPromoteQueued()
    {
        lock (_gate)
        {
            if (_next is not { Boundary: { } boundary } next || _segmentId is not { } completed) return null;
            if (_device.PresentedFrames < boundary) return null;
            _segmentId = next.SegmentId;
            _asset = next.Asset;
            _sourceRate = next.SourceRate;
            _sourceFloat = next.Source;
            _render = next.Render;
            _output = next.Render.Samples;
            _writeCursor = next.WriteCursor;
            _sessionOutputStart = 0;
            _deviceBase = boundary;
            _next = null;
            _clipCompletedRaised = false;
            return (completed, next.SegmentId);
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
                _writeCursor - _sessionOutputStart, presented - _deviceBase, Math.Max(0, _writeCursor - _sessionOutputStart - (presented - _deviceBase)),
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

/// <summary>A clip rendered and waiting to follow the current one without a gap.</summary>
/// <param name="WriteCursor">Frames of it already written to the device.</param>
/// <param name="Boundary">Device presented-frame count at which it starts; null until the writer reaches it.</param>
internal sealed record QueuedClip(
    string SegmentId, SpeechAsset Asset, float[] Source, int SourceRate, PlaybackRender Render, long WriteCursor, long? Boundary);

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
