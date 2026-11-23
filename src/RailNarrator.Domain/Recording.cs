namespace RailNarrator.Domain;

/// <summary>One passage of a narration made earlier: its audio and what is known about it.</summary>
/// <param name="Asset">Format, length, voice and timings. Its timings belong to the saved script.</param>
/// <param name="Pcm">Raw samples in the asset's format.</param>
public sealed record RecordedClip(SpeechAsset Asset, ReadOnlyMemory<byte> Pcm);

/// <summary>
/// A narration of a whole document made earlier and kept with it, played without a language model
/// or a speech engine.
/// </summary>
/// <remarks>
/// The recording comes with the plan it was made from, script and all: its maths is already
/// interpreted and its passages already spoken. A passage is served only as it was scripted; one
/// the reader has since edited is not in the recording and goes to the engine, if there is one.
/// </remarks>
public interface IRecordedNarration
{
    /// <summary>The plan the recording was made from, with every passage's final script.</summary>
    NarrationPlan Plan { get; }

    /// <summary>The voice heard, for the reader ("Maeve, Phonon").</summary>
    string VoiceDescription { get; }

    /// <summary>The passage's clip, or null when it is not recorded with this script.</summary>
    Task<RecordedClip?> TryGetAsync(NarrationSegment segment, CancellationToken ct = default);
}

/// <summary>How far a whole-document narration has come.</summary>
/// <param name="Done">Passages finished.</param>
/// <param name="Total">Passages in the document.</param>
/// <param name="Segment">The passage just finished, as it will be read.</param>
/// <param name="AudioSeconds">Listening time narrated so far.</param>
public sealed record NarrationProgress(int Done, int Total, NarrationSegment Segment, double AudioSeconds);

/// <summary>
/// A transport that plays nothing: for a coordinator that narrates a document to a file instead of
/// to a device.
/// </summary>
public sealed class NullAudioTransport : IAudioTransport
{
    public PlaybackState State => PlaybackState.Idle;
    public double PositionSeconds => 0;
    public double ClipDurationSeconds => 0;
    public double BufferedAheadSeconds => 0;
    public double PlaybackRate => 1;
    public Task LoadAsync(string segmentId, SpeechAsset asset, ReadOnlyMemory<byte> pcm, CancellationToken ct = default) => Task.CompletedTask;
    public Task PlayAsync(CancellationToken ct = default) => Task.CompletedTask;
    public Task PauseAsync(CancellationToken ct = default) => Task.CompletedTask;
    public Task ResumeAsync(CancellationToken ct = default) => Task.CompletedTask;
    public Task StopAsync(CancellationToken ct = default) => Task.CompletedTask;
    public Task SeekAsync(double seconds, CancellationToken ct = default) => Task.CompletedTask;
    public void SetPlaybackRate(double rate) { }
    public event Action<PlaybackState>? StateChanged { add { } remove { } }
    public event Action<string>? ClipCompleted { add { } remove { } }
    public event Action<string>? Underflow { add { } remove { } }
    public event Action<string>? DeviceFailed { add { } remove { } }
}
