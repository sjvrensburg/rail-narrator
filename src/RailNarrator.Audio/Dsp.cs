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
    public float[] Process(ReadOnlySpan<float> input, double rate) => Render(input, rate).Samples;

    /// <summary>
    /// Produces samples and their source-frame map together. Overlap regions have multiple source
    /// contributions; the map uses their window-weighted position, made monotonic for cue lookup.
    /// </summary>
    public PlaybackRender Render(ReadOnlySpan<float> input, double rate)
    {
        if (!double.IsFinite(rate)) throw new ArgumentOutOfRangeException(nameof(rate));
        double r = Math.Clamp(rate, MinRate, MaxRate);
        if (input.Length == 0 || Math.Abs(r - 1) < 1e-6) return new PlaybackRender(input.ToArray(), input.Length);
        int length = Math.Max(1, (int)OutputLengthFor(input.Length, r));
        var output = new float[length];
        var weights = new double[length];
        var positions = new double[length + 1];
        // Keep separate onset and tail grains even when a very short clip is sped up.
        int window = Math.Min(_frameSize, Math.Min(input.Length, Math.Max(1, length / 2)));
        int hop = Math.Max(1, window / 2);
        int previous = 0;
        for (int target = 0; ; target = Math.Min(target + hop, length - window))
        {
            bool last = target == length - window;
            // Anchor the final complete grain to the actual clip tail. Partial trailing grains
            // otherwise discard the final consonant at faster rates or repeat it at slower rates.
            int nominal = last ? input.Length - window
                : Math.Clamp((int)Math.Round(target * r), 0, Math.Max(0, input.Length - window));
            int best = nominal;
            double bestScore = double.NegativeInfinity;
            if (target > 0 && !last)
            {
                // Correlate against the actual output overlap, not another slice of the input.
                for (int candidate = Math.Max(previous, nominal - _tolerance);
                    candidate <= Math.Min(input.Length - window, nominal + _tolerance); candidate++)
                {
                    double dot = 0, a = 0, b = 0;
                    for (int i = 0; i < hop && target + i < length; i++)
                    {
                        double x = weights[target + i] > 0 ? output[target + i] / weights[target + i] : 0;
                        double y = input[candidate + i];
                        dot += x * y; a += x * x; b += y * y;
                    }
                    double score = dot / Math.Sqrt(Math.Max(1e-20, a * b)) - Math.Abs(candidate - nominal) * 1e-8;
                    if (score > bestScore) { bestScore = score; best = candidate; }
                }
            }
            best = Math.Max(previous, best);
            previous = best;
            for (int i = 0; i < window && target + i < length; i++)
            {
                double weight = Math.Max(1e-6, 0.5 - 0.5 * Math.Cos(2 * Math.PI * (i + 0.5) / window));
                output[target + i] += (float)(input[best + i] * weight);
                weights[target + i] += weight;
                positions[target + i] += (best + i) * weight;
            }
            if (last) break;
        }
        for (int i = 0; i < length; i++)
        {
            if (weights[i] > 0) { output[i] = (float)(output[i] / weights[i]); positions[i] /= weights[i]; }
            if (i > 0) positions[i] = Math.Max(positions[i - 1], positions[i]);
        }
        positions[0] = 0;
        positions[length] = input.Length;
        return new PlaybackRender(output, input.Length, positions);
    }
}

/// <summary>One rendered clip and its monotonic output-to-source correspondence.</summary>
public sealed class PlaybackRender(float[] samples, int sourceFrames, double[]? positions = null)
{
    public float[] Samples { get; } = samples;
    public int SourceFrames { get; } = sourceFrames;
    public double SourceFrameAt(double outputFrame)
    {
        if (Samples.Length == 0) return 0;
        double frame = Math.Clamp(outputFrame, 0, Samples.Length);
        if (positions is null) return frame * SourceFrames / Samples.Length;
        int i = (int)frame;
        return i >= Samples.Length ? SourceFrames : positions[i] + (positions[i + 1] - positions[i]) * (frame - i);
    }
    public long OutputFrameAt(double sourceFrame)
    {
        double source = Math.Clamp(sourceFrame, 0, SourceFrames);
        if (positions is null) return SourceFrames == 0 ? 0 : (long)Math.Round(source * Samples.Length / SourceFrames);
        int lo = 0, hi = Samples.Length;
        while (lo < hi)
        {
            int mid = lo + (hi - lo) / 2;
            if (positions[mid] < source) lo = mid + 1; else hi = mid;
        }
        return lo;
    }
}
