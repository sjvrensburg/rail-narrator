using System.Buffers.Binary;
using System.IO.Compression;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Concentus;
using Concentus.Enums;
using RailNarrator.Domain;

namespace RailNarrator.Infrastructure;

/// <summary>What a saved narration says about itself, readable without opening the rest.</summary>
/// <param name="Format">Version of the file's layout; a reader refuses one it does not know.</param>
/// <param name="DocumentContentHash">SHA-256 of the PDF it narrates. A narration never plays over another document.</param>
/// <param name="ByRule">Maths passages read as notation by rule, because no model interpreted them.</param>
/// <param name="Unread">Passages announced as unreadable.</param>
/// <param name="AsPrinted">Sentences with maths read as the page prints them, because no model interpreted them.</param>
public sealed record SavedNarrationManifest(
    int Format,
    string DocumentContentHash,
    string DocumentName,
    DateTimeOffset Created,
    string ProviderId,
    string ModelRevision,
    string VoiceId,
    int Passages,
    double Seconds,
    int Interpreted,
    int ByRule,
    int Unread,
    int AsPrinted,
    int WordTimed)
{
    public const int CurrentFormat = 1;
}

/// <summary>
/// One passage's audio as stored in a saved narration: Opus when the sample rate is one Opus has,
/// 16-bit PCM otherwise.
/// </summary>
/// <remarks>
/// Not an Ogg file: packets with their lengths behind a small header that also records how many
/// frames the clip has and how many the codec delays it by, so decoding gives back exactly the
/// clip's length with its samples where they were. Timings are in samples of the clip.
/// </remarks>
public static class NarrationAudio
{
    private static readonly byte[] OpusMagic = "RNO1"u8.ToArray();
    private static readonly byte[] PcmMagic = "RNP1"u8.ToArray();

    /// <summary>Bits a second for one channel of speech; transparent for a synthetic voice at 24 kHz.</summary>
    public const int BitrateMono = 40_000;

    public static bool OpusSupports(int sampleRate) => sampleRate is 8000 or 12000 or 16000 or 24000 or 48000;

    public static byte[] Encode(ReadOnlySpan<byte> pcm, AudioFormat format)
    {
        int channels = format.Channels;
        long frames = pcm.Length / format.BytesPerFrame;
        var samples = new float[frames * channels];
        int bytesPerSample = format.BytesPerFrame / channels;
        for (int i = 0; i < samples.Length; i++)
            samples[i] = Math.Clamp(AudioSamples.ReadSample(pcm, i * bytesPerSample, format.SampleFormat), -1f, 1f);

        using var output = new MemoryStream();
        Span<byte> header = stackalloc byte[28];
        if (!OpusSupports(format.SampleRate) || channels > 2)
        {
            PcmMagic.CopyTo(header);
            BinaryPrimitives.WriteInt32LittleEndian(header[4..], format.SampleRate);
            BinaryPrimitives.WriteInt32LittleEndian(header[8..], channels);
            BinaryPrimitives.WriteInt64LittleEndian(header[12..], frames);
            output.Write(header[..20]);
            Span<byte> two = stackalloc byte[2];
            foreach (float s in samples)
            {
                BinaryPrimitives.WriteInt16LittleEndian(two, (short)Math.Round(s * 32767f));
                output.Write(two);
            }
            return output.ToArray();
        }

        var encoder = OpusCodecFactory.CreateEncoder(format.SampleRate, channels, OpusApplication.OPUS_APPLICATION_AUDIO);
        encoder.Bitrate = BitrateMono * channels;
        encoder.Complexity = 5;   // the managed encoder is slow at its highest setting, and speech at this bitrate does not need it
        encoder.SignalType = OpusSignal.OPUS_SIGNAL_VOICE;
        int frameSize = format.SampleRate / 50;   // 20 ms
        int delay = encoder.Lookahead;
        OpusMagic.CopyTo(header);
        BinaryPrimitives.WriteInt32LittleEndian(header[4..], format.SampleRate);
        BinaryPrimitives.WriteInt32LittleEndian(header[8..], channels);
        BinaryPrimitives.WriteInt64LittleEndian(header[12..], frames);
        BinaryPrimitives.WriteInt32LittleEndian(header[20..], delay);
        BinaryPrimitives.WriteInt32LittleEndian(header[24..], frameSize);
        output.Write(header);

        // Enough whole packets to carry the clip and the codec's delay after it.
        long packets = (frames + delay + frameSize - 1) / frameSize;
        var frame = new float[frameSize * channels];
        var packet = new byte[4000];
        Span<byte> length = stackalloc byte[2];
        for (long p = 0; p < packets; p++)
        {
            long from = p * frameSize * channels;
            Array.Clear(frame);
            if (from < samples.Length)
                samples.AsSpan((int)from, (int)Math.Min(frame.Length, samples.Length - from)).CopyTo(frame);
            int written = encoder.Encode(frame, frameSize, packet, packet.Length);
            BinaryPrimitives.WriteUInt16LittleEndian(length, (ushort)written);
            output.Write(length);
            output.Write(packet, 0, written);
        }
        return output.ToArray();
    }

    /// <summary>The clip as 32-bit float PCM in its own rate and channels; null when the data is not a clip.</summary>
    public static (byte[] Pcm, AudioFormat Format)? Decode(ReadOnlySpan<byte> data)
    {
        if (data.Length < 20) return null;
        int rate = BinaryPrimitives.ReadInt32LittleEndian(data[4..]);
        int channels = BinaryPrimitives.ReadInt32LittleEndian(data[8..]);
        long frames = BinaryPrimitives.ReadInt64LittleEndian(data[12..]);
        if (rate <= 0 || channels is < 1 or > 8 || frames < 0 || frames > int.MaxValue / (4 * channels)) return null;
        var format = new AudioFormat(rate, channels, SampleFormat.Float32LittleEndian);
        var pcm = new byte[frames * channels * 4];

        if (data[..4].SequenceEqual(PcmMagic))
        {
            var body = data[20..];
            if (body.Length < frames * channels * 2) return null;
            for (int i = 0; i < frames * channels; i++)
                BinaryPrimitives.WriteSingleLittleEndian(pcm.AsSpan(i * 4), BinaryPrimitives.ReadInt16LittleEndian(body[(i * 2)..]) / 32768f);
            return (pcm, format);
        }
        if (!data[..4].SequenceEqual(OpusMagic) || data.Length < 28 || !OpusSupports(rate) || channels > 2) return null;
        int delay = BinaryPrimitives.ReadInt32LittleEndian(data[20..]);
        int frameSize = BinaryPrimitives.ReadInt32LittleEndian(data[24..]);
        if (delay < 0 || frameSize <= 0 || frameSize > rate) return null;

        var decoder = OpusCodecFactory.CreateDecoder(rate, channels);
        var frame = new float[frameSize * channels];
        long position = -delay;   // in frames of the clip; the first samples out are the codec's delay
        int at = 28;
        while (at + 2 <= data.Length && position < frames)
        {
            int size = BinaryPrimitives.ReadUInt16LittleEndian(data[at..]);
            at += 2;
            if (size == 0 || at + size > data.Length) return null;
            int got = decoder.Decode(data.Slice(at, size), frame, frameSize, false);
            at += size;
            for (int i = 0; i < got; i++, position++)
            {
                if (position < 0 || position >= frames) continue;
                for (int c = 0; c < channels; c++)
                    BinaryPrimitives.WriteSingleLittleEndian(pcm.AsSpan((int)(position * channels + c) * 4), frame[i * channels + c]);
            }
        }
        return position >= frames ? (pcm, format) : null;
    }
}

internal sealed record SavedClip(string SegmentId, string ScriptHash, SpeechAsset Asset);

internal static class SavedNarrationJson
{
    public static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = false,
        Converters = { new FollowModeJsonConverter(), new JsonStringEnumConverter() },
    };

    public static string ScriptHash(NarrationSegment segment) => CacheKey.Sha256Hex(segment.SpokenText);

    public static string EntryName(string segmentId)
    {
        var sb = new StringBuilder(segmentId.Length);
        foreach (char c in segmentId) sb.Append(char.IsAsciiLetterOrDigit(c) || c is '-' or '_' ? c : '_');
        return sb.ToString();
    }
}

/// <summary>
/// Writes a saved narration: passages as they are narrated, then the plan and the manifest, and
/// only then the file's name. A narration that was cancelled or failed leaves nothing behind.
/// </summary>
public sealed class SavedNarrationWriter : IDisposable
{
    private readonly string _path;
    private readonly string _temp;
    private readonly FileStream _stream;
    private readonly ZipArchive _zip;
    private readonly List<NarrationSegment> _segments = [];
    private SpeechAsset? _first;
    private double _seconds;
    private int _wordTimed;
    private bool _completed;

    public SavedNarrationWriter(string path)
    {
        _path = Path.GetFullPath(path);
        _temp = _path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        _stream = new FileStream(_temp, FileMode.CreateNew, FileAccess.ReadWrite);
        _zip = new ZipArchive(_stream, ZipArchiveMode.Create, leaveOpen: true);
    }

    public double Seconds => _seconds;

    public void Add(NarrationSegment segment, SpeechAsset asset, ReadOnlySpan<byte> pcm)
    {
        ArgumentNullException.ThrowIfNull(segment);
        ArgumentNullException.ThrowIfNull(asset);
        string name = SavedNarrationJson.EntryName(segment.Id);
        // Already compressed: stored, not deflated again.
        var audio = _zip.CreateEntry($"clips/{name}.audio", CompressionLevel.NoCompression);
        using (var s = audio.Open()) s.Write(NarrationAudio.Encode(pcm, asset.Format));
        var meta = _zip.CreateEntry($"clips/{name}.json", CompressionLevel.Optimal);
        using (var s = meta.Open())
            JsonSerializer.Serialize(s, new SavedClip(segment.Id, SavedNarrationJson.ScriptHash(segment), asset), SavedNarrationJson.Options);
        _segments.Add(segment);
        _first ??= asset;
        _seconds += asset.DurationSeconds;
        if (asset.TimingProvenance == TimingProvenance.ForcedAlignment && asset.Timings.Count > 0) _wordTimed++;
    }

    /// <summary>
    /// Finishes the file. <paramref name="plan"/> is the plan narrated; its passages are replaced by
    /// the scripts that were spoken, in the order they were added.
    /// </summary>
    public SavedNarrationManifest Complete(NarrationPlan plan, string documentName)
    {
        ArgumentNullException.ThrowIfNull(plan);
        var spoken = _segments.ToDictionary(s => s.Id);
        var final = plan with { Segments = plan.Segments.Select(s => spoken.GetValueOrDefault(s.Id, s)).ToList() };
        var manifest = new SavedNarrationManifest(
            SavedNarrationManifest.CurrentFormat, plan.Header.Extraction.DocumentContentHash, documentName, DateTimeOffset.UtcNow,
            _first?.ProviderId ?? "", _first?.ModelRevision ?? "", _first?.VoiceId ?? "", _segments.Count, _seconds,
            Interpreted: _segments.Count(s => s.Provenance == ScriptProvenance.ModelGenerated),
            ByRule: _segments.Count(s => s.Provenance == ScriptProvenance.LocalRule),
            Unread: _segments.Count(s => s.Provenance == ScriptProvenance.FailureAnnouncement),
            // Maths the plan left for a model that came back as the page prints it.
            AsPrinted: plan.Segments.Count(s => s.Provenance == ScriptProvenance.PendingInterpretation
                && spoken.TryGetValue(s.Id, out var said) && said.Provenance is ScriptProvenance.SourceVerbatim or ScriptProvenance.PendingInterpretation),
            WordTimed: _wordTimed);
        using (var s = _zip.CreateEntry("plan.json", CompressionLevel.Optimal).Open())
            JsonSerializer.Serialize(s, final, SavedNarrationJson.Options);
        using (var s = _zip.CreateEntry("manifest.json", CompressionLevel.Optimal).Open())
            JsonSerializer.Serialize(s, manifest, SavedNarrationJson.Options);
        _zip.Dispose();
        _stream.Flush(flushToDisk: true);
        _stream.Dispose();
        File.Move(_temp, _path, overwrite: true);
        _completed = true;
        return manifest;
    }

    public void Dispose()
    {
        if (_completed) return;
        try { _zip.Dispose(); } catch (Exception) { /* a half-written archive is being thrown away */ }
        _stream.Dispose();
        try { File.Delete(_temp); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }
}

/// <summary>
/// A narration saved beside its PDF ("paper.pdf" has "paper.narration"): the script with its maths
/// already interpreted, every passage's audio and its timings. Played with no model and no engine.
/// </summary>
public sealed class SavedNarration : IRecordedNarration, IDisposable
{
    private readonly ZipArchive _zip;
    private readonly object _gate = new();
    private readonly Dictionary<string, string> _scriptHashes;

    private SavedNarration(string path, ZipArchive zip, SavedNarrationManifest manifest, NarrationPlan plan)
    {
        FilePath = path;
        _zip = zip;
        Manifest = manifest;
        Plan = plan;
        _scriptHashes = plan.Segments.ToDictionary(s => s.Id, SavedNarrationJson.ScriptHash);
    }

    public string FilePath { get; }

    public SavedNarrationManifest Manifest { get; }

    public NarrationPlan Plan { get; }

    public string VoiceDescription => string.IsNullOrEmpty(Manifest.VoiceId) ? Manifest.ProviderId : $"{Manifest.VoiceId}, {Manifest.ProviderId}";

    /// <summary>Where a document's saved narration is kept: beside it, with the extension changed.</summary>
    public static string PathFor(string documentPath) => Path.ChangeExtension(Path.GetFullPath(documentPath), ".narration");

    /// <summary>
    /// Opens the narration saved for a document. Null with nothing said when there is no file; null
    /// with <paramref name="problem"/> when there is one that cannot be used (another document's,
    /// a newer layout, damage).
    /// </summary>
    public static SavedNarration? TryOpen(string path, string documentContentHash, out string? problem)
    {
        problem = null;
        if (!File.Exists(path)) return null;
        ZipArchive? zip = null;
        try
        {
            zip = new ZipArchive(new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read), ZipArchiveMode.Read);
            var manifest = Read<SavedNarrationManifest>(zip, "manifest.json");
            if (manifest is null) { problem = "it has no manifest"; return Fail(); }
            if (manifest.Format != SavedNarrationManifest.CurrentFormat)
            {
                problem = $"it was written in layout {manifest.Format}, and this version reads layout {SavedNarrationManifest.CurrentFormat}";
                return Fail();
            }
            if (!string.Equals(manifest.DocumentContentHash, documentContentHash, StringComparison.OrdinalIgnoreCase))
            {
                problem = "it was made from a different version of the document";
                return Fail();
            }
            var plan = Read<NarrationPlan>(zip, "plan.json");
            if (plan is null || plan.SegmentCount == 0) { problem = "it has no script"; return Fail(); }
            return new SavedNarration(path, zip, manifest, plan);
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or JsonException or UnauthorizedAccessException)
        {
            problem = "it could not be read: " + ex.Message;
            return Fail();
        }

        SavedNarration? Fail()
        {
            zip?.Dispose();
            return null;
        }
    }

    private static T? Read<T>(ZipArchive zip, string name) where T : class
    {
        var entry = zip.GetEntry(name);
        if (entry is null) return null;
        using var stream = entry.Open();
        return JsonSerializer.Deserialize<T>(stream, SavedNarrationJson.Options);
    }

    public Task<RecordedClip?> TryGetAsync(NarrationSegment segment, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(segment);
        // Only as scripted: a passage the reader has rewritten is not what was recorded.
        if (!_scriptHashes.TryGetValue(segment.Id, out string? hash) || hash != SavedNarrationJson.ScriptHash(segment))
            return Task.FromResult<RecordedClip?>(null);
        return Task.Run(() => Load(segment.Id, hash), ct);
    }

    private RecordedClip? Load(string segmentId, string scriptHash)
    {
        string name = SavedNarrationJson.EntryName(segmentId);
        SavedClip? clip;
        byte[] audio;
        lock (_gate)   // a ZipArchive reads one entry at a time
        {
            clip = Read<SavedClip>(_zip, $"clips/{name}.json");
            var entry = _zip.GetEntry($"clips/{name}.audio");
            if (clip is null || entry is null || clip.ScriptHash != scriptHash) return null;
            using var stream = entry.Open();
            using var buffer = new MemoryStream();
            stream.CopyTo(buffer);
            audio = buffer.ToArray();
        }
        if (NarrationAudio.Decode(audio) is not { } decoded) return null;
        long frames = decoded.Pcm.Length / decoded.Format.BytesPerFrame;
        // Timings are in samples of the clip: only a clip of the recorded length may carry them.
        if (frames != clip.Asset.FrameCount) return null;
        return new RecordedClip(clip.Asset with { Format = decoded.Format, PcmHash = null }, decoded.Pcm);
    }

    public void Dispose()
    {
        lock (_gate) _zip.Dispose();
    }
}
