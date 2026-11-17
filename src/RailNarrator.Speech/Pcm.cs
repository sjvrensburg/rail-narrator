using System.Buffers.Binary;
using RailNarrator.Domain;

namespace RailNarrator.Speech;

/// <summary>
/// PCM sample conversion, WAV parsing, and the amplitude measurements used to tell real speech
/// from a silent decoder.
/// </summary>
/// <remarks>
/// Providers disagree about format — Kokoro emits 24 kHz float32, the ZONOS3 HTTP API emits 44.1 kHz
/// float32 and its CLI writes 44.1 kHz 16-bit WAV — so conversion lives here and every provider
/// reports the format it actually produced rather than the one that was asked for. A provider that
/// silently resampled would shift every later cue in time, which is why <see cref="ConvertTo"/>
/// refuses to change the sample rate: that is the audio engine's job, where the mapping is tracked.
/// </remarks>
public static class Pcm
{
    /// <summary>Converts interleaved samples between supported formats. Sample rate is preserved.</summary>
    public static byte[] ConvertTo(ReadOnlySpan<byte> pcm, AudioFormat from, SampleFormat to)
    {
        if (from.SampleFormat == to) return pcm.ToArray();

        int frameBytes = from.Channels * BytesPerSample(from.SampleFormat);
        if (frameBytes <= 0 || pcm.Length % frameBytes != 0)
            throw new ArgumentException($"Buffer length {pcm.Length} is not a multiple of the frame size {frameBytes}.", nameof(pcm));
        int frames = pcm.Length / frameBytes;

        var result = new byte[frames * from.Channels * BytesPerSample(to)];
        int srcStride = BytesPerSample(from.SampleFormat);
        int dstStride = BytesPerSample(to);

        for (int i = 0; i < frames * from.Channels; i++)
        {
            float sample = ReadSample(pcm, i * srcStride, from.SampleFormat);
            WriteSample(result, i * dstStride, to, sample);
        }
        return result;
    }

    /// <summary>Reads one little-endian sample as a float in [-1, 1].</summary>
    public static float ReadSample(ReadOnlySpan<byte> pcm, int byteOffset, SampleFormat format) => format switch
    {
        SampleFormat.Float32LittleEndian => BinaryPrimitives.ReadSingleLittleEndian(pcm.Slice(byteOffset, 4)),
        SampleFormat.Int16LittleEndian => BinaryPrimitives.ReadInt16LittleEndian(pcm.Slice(byteOffset, 2)) / 32768f,
        SampleFormat.Int32LittleEndian => BinaryPrimitives.ReadInt32LittleEndian(pcm.Slice(byteOffset, 4)) / 2147483648f,
        _ => throw new ArgumentOutOfRangeException(nameof(format)),
    };

    /// <summary>Writes one little-endian sample, clipping float input to the representable range.</summary>
    public static void WriteSample(Span<byte> pcm, int byteOffset, SampleFormat format, float sample)
    {
        switch (format)
        {
            case SampleFormat.Float32LittleEndian:
                BinaryPrimitives.WriteSingleLittleEndian(pcm.Slice(byteOffset, 4), sample);
                break;
            case SampleFormat.Int16LittleEndian:
                BinaryPrimitives.WriteInt16LittleEndian(pcm.Slice(byteOffset, 2),
                    (short)Math.Clamp((int)Math.Round(Math.Clamp(sample, -1f, 1f) * 32767f), short.MinValue, short.MaxValue));
                break;
            case SampleFormat.Int32LittleEndian:
                BinaryPrimitives.WriteInt32LittleEndian(pcm.Slice(byteOffset, 4),
                    (int)Math.Clamp((long)Math.Round(Math.Clamp(sample, -1f, 1f) * 2147483647.0), int.MinValue, int.MaxValue));
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(format));
        }
    }

    public static int BytesPerSample(SampleFormat format) => format switch
    {
        SampleFormat.Float32LittleEndian => 4,
        SampleFormat.Int16LittleEndian => 2,
        SampleFormat.Int32LittleEndian => 4,
        _ => throw new ArgumentOutOfRangeException(nameof(format)),
    };

    /// <summary>Peak absolute sample value. Used to detect a decoder that produced silence.</summary>
    public static float Peak(ReadOnlySpan<byte> pcm, AudioFormat format)
    {
        int stride = BytesPerSample(format.SampleFormat);
        if (stride <= 0 || pcm.Length < stride) return 0f;
        float peak = 0f;
        for (int i = 0; i + stride <= pcm.Length; i += stride)
            peak = Math.Max(peak, Math.Abs(ReadSample(pcm, i, format.SampleFormat)));
        return peak;
    }

    /// <summary>Root-mean-square amplitude. Complements <see cref="Peak"/>: a click has a peak but no energy.</summary>
    public static float Rms(ReadOnlySpan<byte> pcm, AudioFormat format)
    {
        int stride = BytesPerSample(format.SampleFormat);
        if (stride <= 0 || pcm.Length < stride) return 0f;
        double sum = 0;
        int n = 0;
        for (int i = 0; i + stride <= pcm.Length; i += stride)
        {
            float s = ReadSample(pcm, i, format.SampleFormat);
            sum += s * s;
            n++;
        }
        return n == 0 ? 0f : (float)Math.Sqrt(sum / n);
    }

    /// <summary>
    /// Number of leading and trailing frames whose amplitude is below <paramref name="threshold"/>.
    /// </summary>
    /// <remarks>
    /// Trimming leading silence matters for cue accuracy: a clip's first sample is the start of its
    /// highlight, and a half-second of silence would hold the previous highlight on screen while
    /// nothing is being said. Trailing silence is left alone by default because it is the natural
    /// pause between sentences, and removing it makes narration sound rushed.
    /// </remarks>
    public static (int LeadingFrames, int TrailingFrames) MeasureSilence(
        ReadOnlySpan<byte> pcm, AudioFormat format, float threshold = 0.005f)
    {
        int stride = BytesPerSample(format.SampleFormat) * format.Channels;
        if (stride <= 0) return (0, 0);
        int frames = pcm.Length / stride;

        int leading = 0;
        while (leading < frames && FrameBelow(pcm, leading, stride, format.SampleFormat, threshold)) leading++;
        int trailing = 0;
        while (trailing < frames - leading && FrameBelow(pcm, frames - 1 - trailing, stride, format.SampleFormat, threshold)) trailing++;
        return (leading, trailing);
    }

    private static bool FrameBelow(ReadOnlySpan<byte> pcm, int frame, int stride, SampleFormat format, float threshold)
    {
        int offset = frame * stride;
        for (int c = 0; c < stride; c += BytesPerSample(format))
            if (Math.Abs(ReadSample(pcm, offset + c, format)) >= threshold) return false;
        return true;
    }

    /// <summary>Removes leading (and optionally trailing) silence, returning the trimmed buffer and frames removed.</summary>
    public static (byte[] Pcm, int LeadingFramesTrimmed) TrimLeadingSilence(
        ReadOnlySpan<byte> pcm, AudioFormat format, float threshold = 0.005f, bool trimTrailing = false)
    {
        var (leading, trailing) = MeasureSilence(pcm, format, threshold);
        int stride = BytesPerSample(format.SampleFormat) * format.Channels;
        if (stride <= 0) return (pcm.ToArray(), 0);
        int frames = pcm.Length / stride;
        int keep = frames - leading - (trimTrailing ? trailing : 0);
        if (keep <= 0) return (pcm.ToArray(), 0);
        return (pcm.Slice(leading * stride, keep * stride).ToArray(), leading);
    }
}

/// <summary>A parsed WAV file: its format and its raw sample bytes.</summary>
public sealed record WavData(AudioFormat Format, byte[] Pcm, long FrameCount);

/// <summary>
/// Minimal RIFF/WAVE reader and writer.
/// </summary>
/// <remarks>
/// Hand-rolled rather than taken from a dependency because the only requirement is to read what the
/// speech providers emit and to write cache files. It deliberately rejects anything it cannot
/// represent exactly — an unsupported codec is an error, not a guess — because decoding a format
/// wrongly produces noise that still "plays".
/// </remarks>
public static class WavCodec
{
    private const int FormatPcm = 0x0001;
    private const int FormatIeeeFloat = 0x0003;
    private const int FormatExtensible = 0xFFFE;

    /// <summary>Parses a WAV buffer. Throws <see cref="InvalidDataException"/> on anything unsupported.</summary>
    public static WavData Parse(ReadOnlySpan<byte> wav)
    {
        if (wav.Length < 44) throw new InvalidDataException($"Buffer is {wav.Length} bytes, too short for a WAV header.");
        if (!Tag(wav, 0, "RIFF")) throw new InvalidDataException("Not a RIFF file.");
        if (!Tag(wav, 8, "WAVE")) throw new InvalidDataException("Not a WAVE file.");

        int pos = 12;
        int formatTag = 0, channels = 0, sampleRate = 0, bitsPerSample = 0;
        bool haveFmt = false, haveData = false;
        byte[]? dataCopy = null;

        // Chunks are walked rather than assumed to be in the canonical order: a "LIST" or "fact"
        // chunk before "data" is common and skipping it by fixed offset would misread the file.
        while (pos + 8 <= wav.Length)
        {
            int size = (int)BinaryPrimitives.ReadUInt32LittleEndian(wav.Slice(pos + 4, 4));
            if (size < 0 || pos + 8 + size > wav.Length) size = wav.Length - pos - 8;
            var body = wav.Slice(pos + 8, Math.Max(0, size));

            if (Tag(wav, pos, "fmt "))
            {
                if (body.Length < 16) throw new InvalidDataException("fmt chunk is too short.");
                formatTag = BinaryPrimitives.ReadUInt16LittleEndian(body.Slice(0, 2));
                channels = BinaryPrimitives.ReadUInt16LittleEndian(body.Slice(2, 2));
                sampleRate = (int)BinaryPrimitives.ReadUInt32LittleEndian(body.Slice(4, 4));
                bitsPerSample = BinaryPrimitives.ReadUInt16LittleEndian(body.Slice(14, 2));
                haveFmt = true;
            }
            else if (Tag(wav, pos, "data"))
            {
                dataCopy = body.ToArray();
                haveData = true;
            }
            // Chunks are word-aligned; an odd size carries a pad byte.
            pos += 8 + size + (size % 2);
        }

        if (!haveFmt) throw new InvalidDataException("WAV has no fmt chunk.");
        if (!haveData || dataCopy is null) throw new InvalidDataException("WAV has no data chunk.");
        if (channels <= 0 || sampleRate <= 0) throw new InvalidDataException($"Invalid channel count {channels} or sample rate {sampleRate}.");

        SampleFormat sampleFormat = (formatTag, bitsPerSample) switch
        {
            (FormatPcm, 16) => SampleFormat.Int16LittleEndian,
            (FormatPcm, 32) => SampleFormat.Int32LittleEndian,
            (FormatIeeeFloat, 32) => SampleFormat.Float32LittleEndian,
            (FormatExtensible, 16) => SampleFormat.Int16LittleEndian,
            (FormatExtensible, 32) => SampleFormat.Float32LittleEndian,
            _ => throw new InvalidDataException($"Unsupported WAV encoding: format tag 0x{formatTag:X4}, {bitsPerSample} bits per sample."),
        };

        var format = new AudioFormat(sampleRate, channels, sampleFormat);
        int frameBytes = format.BytesPerFrame;
        // A data chunk can be padded; only whole frames are audio.
        long frames = dataCopy.Length / frameBytes;
        if (frames * frameBytes != dataCopy.Length)
            dataCopy = dataCopy[..(int)(frames * frameBytes)];

        return new WavData(format, dataCopy, frames);
    }

    /// <summary>Wraps raw PCM in a WAV header.</summary>
    public static byte[] Write(AudioFormat format, ReadOnlySpan<byte> pcm)
    {
        if (!format.IsValid) throw new ArgumentException("Invalid audio format.", nameof(format));
        int frameBytes = format.BytesPerFrame;
        int dataLen = pcm.Length / frameBytes * frameBytes;
        var buffer = new byte[44 + dataLen];
        var span = buffer.AsSpan();

        WriteTag(span, 0, "RIFF");
        BinaryPrimitives.WriteUInt32LittleEndian(span.Slice(4, 4), (uint)(36 + dataLen));
        WriteTag(span, 8, "WAVE");
        WriteTag(span, 12, "fmt ");
        BinaryPrimitives.WriteUInt32LittleEndian(span.Slice(16, 4), 16);
        BinaryPrimitives.WriteUInt16LittleEndian(span.Slice(20, 2),
            (ushort)(format.SampleFormat == SampleFormat.Float32LittleEndian ? FormatIeeeFloat : FormatPcm));
        BinaryPrimitives.WriteUInt16LittleEndian(span.Slice(22, 2), (ushort)format.Channels);
        BinaryPrimitives.WriteUInt32LittleEndian(span.Slice(24, 4), (uint)format.SampleRate);
        BinaryPrimitives.WriteUInt32LittleEndian(span.Slice(28, 4), (uint)(format.SampleRate * frameBytes));
        BinaryPrimitives.WriteUInt16LittleEndian(span.Slice(32, 2), (ushort)frameBytes);
        BinaryPrimitives.WriteUInt16LittleEndian(span.Slice(34, 2), (ushort)(Pcm.BytesPerSample(format.SampleFormat) * 8));
        WriteTag(span, 36, "data");
        BinaryPrimitives.WriteUInt32LittleEndian(span.Slice(40, 4), (uint)dataLen);
        pcm.Slice(0, dataLen).CopyTo(span.Slice(44));
        return buffer;
    }

    private static bool Tag(ReadOnlySpan<byte> buf, int offset, string tag)
    {
        if (offset + 4 > buf.Length) return false;
        for (int i = 0; i < 4; i++) if (buf[offset + i] != (byte)tag[i]) return false;
        return true;
    }

    private static void WriteTag(Span<byte> buf, int offset, string tag)
    {
        for (int i = 0; i < 4; i++) buf[offset + i] = (byte)tag[i];
    }
}
