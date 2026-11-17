namespace RailNarrator.Audio;

/// <summary>
/// Offline sample-rate conversion by windowed-sinc interpolation, plus the exact frame mapping
/// between the source and output rates.
/// </summary>
/// <remarks>
/// <para>
/// Conversion is required, not optional: on the reference machine PortAudio's ALSA host refuses to
/// open a 24 kHz stream on a 44.1 kHz device with <c>paInvalidSampleRate</c>, because that host API
/// does not resample. Kokoro synthesises at 24 kHz, so without this there is no audio at all.
/// </para>
/// <para>
/// It is offline and whole-clip because the design synthesises complete sentence and phrase clips
/// ahead of playback. That makes the conversion a pure function of the clip — the same bytes in, the
/// same bytes out — which is what lets a cached clip be replayed without recomputing it, and lets
/// the frame mapping be simple integer arithmetic instead of a streaming state machine that has to be
/// flushed correctly on every seek.
/// </para>
/// <para>
/// Linear interpolation would be shorter and would alias: a 24 kHz signal carries content to 12 kHz,
/// and up-sampling without an anti-image filter folds that energy back into the audible band.
/// </para>
/// </remarks>
public static class Resampler
{
    /// <summary>Filter half-length in source frames. 16 (32 taps) is a good quality/cost balance for speech.</summary>
    public const int DefaultHalfTaps = 16;

    /// <summary>Output frame count for a given input length. Exact rounding, no accumulation drift.</summary>
    public static long OutputFramesFor(long inputFrames, int inputRate, int outputRate)
        => (long)Math.Round(inputFrames * (double)outputRate / inputRate, MidpointRounding.AwayFromZero);

    /// <summary>
    /// The source frame an output frame was synthesised from. The media clock uses this rather than a
    /// wall clock, so resampling cannot make a highlight drift from the audio it belongs to.
    /// </summary>
    public static long SourceFrameForOutput(long outputFrame, int inputRate, int outputRate)
        => (long)Math.Floor(outputFrame * (double)inputRate / outputRate);

    /// <summary>The first output frame at which a source frame becomes audible.</summary>
    public static long OutputFrameForSource(long sourceFrame, int inputRate, int outputRate)
        => (long)Math.Ceiling(sourceFrame * (double)outputRate / inputRate);

    /// <summary>
    /// Resamples mono float audio.
    /// </summary>
    /// <param name="input">Source samples.</param>
    /// <param name="inputRate">Source rate.</param>
    /// <param name="outputRate">Target rate. Equal rates return a copy without filtering.</param>
    /// <param name="halfTaps">Filter half-length.</param>
    public static float[] Resample(ReadOnlySpan<float> input, int inputRate, int outputRate, int halfTaps = DefaultHalfTaps)
    {
        if (inputRate <= 0) throw new ArgumentOutOfRangeException(nameof(inputRate));
        if (outputRate <= 0) throw new ArgumentOutOfRangeException(nameof(outputRate));
        if (halfTaps < 2) throw new ArgumentOutOfRangeException(nameof(halfTaps));
        if (input.IsEmpty) return [];
        if (inputRate == outputRate) return input.ToArray();

        // Cut to the lower Nyquist, so up-sampling produces no images and down-sampling does not alias.
        double cutoff = Math.Min(1.0, (double)outputRate / inputRate) * 0.95;
        double step = (double)inputRate / outputRate;
        int outLength = (int)OutputFramesFor(input.Length, inputRate, outputRate);
        var output = new float[outLength];

        var window = BuildKaiserWindow(halfTaps);

        for (int n = 0; n < outLength; n++)
        {
            double srcPos = n * step;
            int centre = (int)Math.Floor(srcPos);
            double sum = 0, weightSum = 0;

            for (int k = -halfTaps + 1; k <= halfTaps; k++)
            {
                int index = centre + k;
                if (index < 0 || index >= input.Length) continue;
                double w = Sinc((index - srcPos) * cutoff) * cutoff * window[k + halfTaps - 1];
                sum += input[index] * w;
                weightSum += w;
            }
            // Normalising by the window's own sum gives exact unity DC gain at every fractional
            // position. Without it the filter's passband ripple would modulate speech loudness.
            output[n] = weightSum > 1e-9 ? (float)(sum / weightSum) : 0f;
        }
        return output;
    }

    /// <summary>Normalised sinc, with the removable singularity at zero handled explicitly.</summary>
    public static double Sinc(double x)
    {
        if (Math.Abs(x) < 1e-9) return 1.0;
        double px = Math.PI * x;
        return Math.Sin(px) / px;
    }

    /// <summary>Kaiser window, beta 6 (about 44 dB stopband attenuation), over <c>[-halfTaps, halfTaps]</c>.</summary>
    public static double[] BuildKaiserWindow(int halfTaps)
    {
        const double beta = 6.0;
        double denominator = Bessel0(beta);
        var window = new double[halfTaps * 2];
        for (int k = -halfTaps + 1; k <= halfTaps; k++)
        {
            double r = (double)k / halfTaps;
            double arg = beta * Math.Sqrt(Math.Max(0, 1 - r * r));
            window[k + halfTaps - 1] = Bessel0(arg) / denominator;
        }
        return window;
    }

    /// <summary>Zeroth-order modified Bessel function of the first kind, by series expansion.</summary>
    private static double Bessel0(double x)
    {
        double sum = 1, term = 1;
        double y = x * x / 4.0;
        for (int k = 1; k < 30; k++)
        {
            term *= y / (k * (double)k);
            sum += term;
            if (term < sum * 1e-14) break;
        }
        return sum;
    }
}

/// <summary>
/// Pitch-preserving time scaling by WSOLA (waveform similarity overlap-add).
/// </summary>
/// <remarks>
/// <para>
/// Playback rate must not be implemented by changing the sample rate. That speeds the audio up and
/// raises its pitch together — 2× sounds like a chipmunk — and it silently breaks the source-to-output
/// frame mapping the highlights are built on, because the mapping assumes a rate change is accounted
/// for, not baked into the samples.
/// </para>
/// <para>
/// WSOLA keeps each analysis frame's own waveform and changes only the spacing between frames, so
/// pitch is preserved. Each frame's start is searched in a small window for the best match against
/// the previous frame's tail, which is what stops the fixed-overlap version from sounding phasy.
/// </para>
/// <para>
/// Rate 1.0 short-circuits to a copy: the common case pays nothing, and a pass-through is bit-exact.
/// </para>
/// </remarks>
public sealed class WsolaTimeStretcher
{
    private readonly int _frameSize;
    private readonly int _synthesisHop;
    private readonly int _tolerance;

    /// <param name="sampleRate">Rate of the audio being stretched.</param>
    /// <param name="frameMilliseconds">Analysis frame length. 30 ms stays inside a phoneme while remaining long enough to correlate.</param>
    public WsolaTimeStretcher(int sampleRate, int frameMilliseconds = 30)
    {
        if (sampleRate <= 0) throw new ArgumentOutOfRangeException(nameof(sampleRate));
        if (frameMilliseconds < 5) throw new ArgumentOutOfRangeException(nameof(frameMilliseconds));
        SampleRate = sampleRate;
        _frameSize = Math.Max(64, sampleRate * frameMilliseconds / 1000);
        _synthesisHop = _frameSize / 2;
        _tolerance = _synthesisHop / 3;
    }

    public int SampleRate { get; }

    /// <summary>Supported rate range. Outside it, quality degrades faster than the feature is worth.</summary>
    public const double MinRate = 0.5;
    public const double MaxRate = 2.5;

    /// <summary>Output length for a given input length at <paramref name="rate"/>.</summary>
    public long OutputLengthFor(long inputFrames, double rate)
    {
        double r = Math.Clamp(rate, MinRate, MaxRate);
        return (long)Math.Round(inputFrames / r, MidpointRounding.AwayFromZero);
    }

    /// <summary>
    /// Stretches mono audio. <paramref name="rate"/> greater than 1 shortens it (faster speech);
    /// less than 1 lengthens it. Pitch is unchanged either way.
    /// </summary>
    public float[] Process(ReadOnlySpan<float> input, double rate)
    {
        double r = Math.Clamp(rate, MinRate, MaxRate);
        if (Math.Abs(r - 1.0) < 1e-6) return input.ToArray();
        if (input.Length < _frameSize * 2) return input.ToArray();

        int analysisHop = Math.Max(1, (int)Math.Round(_synthesisHop * r));
        int estimated = (int)OutputLengthFor(input.Length, r) + _frameSize;
        var output = new float[estimated];
        int outPos = 0;
        int inPos = 0;

        var fade = HannHalf(_synthesisHop);

        while (inPos + _frameSize <= input.Length && outPos + _frameSize < output.Length)
        {
            int best = FindBestOffset(input, inPos, analysisHop);

            // Cross-fade the incoming frame's head against the previous frame's tail so the seam is
            // continuous. The fade-in curve is applied to the new frame and its complement to what is
            // already in the output buffer.
            if (outPos >= _synthesisHop)
            {
                int overlapStart = outPos - _synthesisHop;
                for (int i = 0; i < _synthesisHop; i++)
                    output[overlapStart + i] = output[overlapStart + i] * (1f - fade[i]) + input[best + i] * fade[i];
                CopyFrom(input, best + _synthesisHop, output, outPos, _synthesisHop);
            }
            else
            {
                CopyFrom(input, best, output, outPos, _frameSize);
            }

            outPos += _synthesisHop;
            inPos += analysisHop;
        }

        // Flush whatever remains of the input so the last word is not truncated. A dropped tail is
        // audible as a clipped final consonant and would also shorten the clip the cues were built for.
        int tail = Math.Min(_frameSize, input.Length - Math.Max(0, inPos - analysisHop));
        if (tail > 0 && outPos + tail < output.Length)
        {
            CopyFrom(input, Math.Max(0, input.Length - tail), output, outPos, tail);
            outPos += tail;
        }

        return outPos == output.Length ? output : output.AsSpan(0, Math.Max(0, outPos)).ToArray();
    }

    private static void CopyFrom(ReadOnlySpan<float> input, int inputOffset, float[] output, int outputOffset, int count)
    {
        for (int i = 0; i < count; i++)
        {
            int src = inputOffset + i;
            output[outputOffset + i] = src >= 0 && src < input.Length ? input[src] : 0f;
        }
    }

    /// <summary>Raised-cosine fade over half a frame, used for both the fade-in and its complement.</summary>
    private static float[] HannHalf(int length)
    {
        var fade = new float[length];
        for (int i = 0; i < length; i++) fade[i] = 0.5f - 0.5f * (float)Math.Cos(Math.PI * i / Math.Max(1, length - 1));
        return fade;
    }

    /// <summary>
    /// Chooses the input offset whose first half-frame best matches the waveform already written to
    /// the output, within a small search window. Correlation on the overlap is what makes WSOLA sound
    /// continuous where fixed-overlap OLA sounds phasy.
    /// </summary>
    private int FindBestOffset(ReadOnlySpan<float> input, int nominal, int analysisHop)
    {
        int overlapStart = Math.Max(0, nominal - _synthesisHop);
        if (overlapStart + _synthesisHop > input.Length) return Math.Clamp(nominal, 0, Math.Max(0, input.Length - _frameSize));

        int best = nominal;
        float bestScore = float.MinValue;
        for (int delta = -_tolerance; delta <= _tolerance; delta++)
        {
            int candidate = nominal + delta - _synthesisHop;
            if (candidate < 0 || candidate + _synthesisHop > input.Length) continue;
            float score = 0;
            for (int i = 0; i < _synthesisHop; i++) score += input[candidate + i] * input[overlapStart + i];
            if (score > bestScore) { bestScore = score; best = candidate; }
        }
        return Math.Clamp(best, 0, Math.Max(0, input.Length - _frameSize));
    }
}
