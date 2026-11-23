using System.Diagnostics;
using System.Globalization;
using System.Text;
using RailNarrator.Domain;

namespace RailNarrator.Infrastructure;

/// <summary>The engines a whole-document narration uses: those the application reads with.</summary>
public sealed record NarrationEngines(
    ITtsProvider Speech,
    ISpeechAssetStore? Assets,
    IForcedAligner? Aligner,
    ISegmentResolver? Resolver,
    NarrationCoordinatorOptions Options);

/// <summary>
/// Narrates a whole document to a file beside it: every maths passage interpreted, every passage
/// spoken and, with an aligner, its words timed. What it makes is played later with nothing else.
/// </summary>
public static class NarrationJob
{
    /// <summary>
    /// Runs on a coordinator of its own with no audio device, so a document being listened to is
    /// not disturbed; the engines are shared and take one job at a time. The clips also land in
    /// the ordinary speech cache, so a run that was stopped does not start from nothing.
    /// </summary>
    /// <param name="allowUninterpreted">
    /// Carry on when the maths model cannot be asked, saving its passages as notation or as
    /// printed. By default the narration stops there with the reason: what is saved is what the
    /// reader will hear every time, and a refused key should not be baked into it unnoticed.
    /// </param>
    /// <returns>The manifest of the file written at <paramref name="outputPath"/>.</returns>
    public static async Task<SavedNarrationManifest> RunAsync(
        NarrationPlan plan, string documentName, string outputPath, NarrationEngines engines,
        IProgress<NarrationProgress>? progress = null, CancellationToken ct = default, bool allowUninterpreted = false)
    {
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentNullException.ThrowIfNull(engines);
        if (plan.SegmentCount == 0) throw new InvalidOperationException("The document has nothing to narrate.");

        using var stop = CancellationTokenSource.CreateLinkedTokenSource(ct);
        string? endpointProblem = null;
        void OnEndpointProblem(string reason)
        {
            if (allowUninterpreted || Interlocked.CompareExchange(ref endpointProblem, reason, null) is not null) return;
            stop.Cancel();
        }
        var maths = engines.Resolver as MathInterpretationResolver;
        if (maths is not null) maths.EndpointProblem += OnEndpointProblem;
        try
        {
            return await NarrateAsync(plan, documentName, outputPath, engines, progress, stop.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (endpointProblem is not null && !ct.IsCancellationRequested)
        {
            throw new MathsNotInterpretedException(
                $"The maths model could not be asked ({endpointProblem.TrimEnd('.')}), so the narration was stopped and nothing was saved. " +
                "Put the endpoint right in Settings ▸ Maths, or narrate with the maths left uninterpreted.");
        }
        finally
        {
            if (maths is not null) maths.EndpointProblem -= OnEndpointProblem;
        }
    }

    private static async Task<SavedNarrationManifest> NarrateAsync(
        NarrationPlan plan, string documentName, string outputPath, NarrationEngines engines,
        IProgress<NarrationProgress>? progress, CancellationToken ct)
    {
        using var writer = new SavedNarrationWriter(outputPath);
        // Whole-document work is not bounded by the lookahead, and its words are timed.
        var options = engines.Options with { RequestTimings = true };
        await using var coordinator = new NarrationCoordinator(
            engines.Speech, new NullAudioTransport(), engines.Assets, options: options, aligner: engines.Aligner, resolver: engines.Resolver);
        await coordinator.LoadPlanAsync(plan, ct: ct).ConfigureAwait(false);

        // Encoding and writing go on beside the speaking of the next passage, in order, a few
        // passages deep at most: the engine does not wait for the file, nor memory for the engine.
        var pending = System.Threading.Channels.Channel.CreateBounded<(NarrationSegment Segment, SpeechAsset Asset, byte[] Pcm)>(
            new System.Threading.Channels.BoundedChannelOptions(4) { SingleReader = true, SingleWriter = true });
        var writing = Task.Run(async () =>
        {
            await foreach (var (segment, asset, pcm) in pending.Reader.ReadAllAsync(ct).ConfigureAwait(false))
                writer.Add(segment, asset, pcm);
        }, ct);
        try
        {
            await coordinator.NarrateAllAsync(async (segment, asset, pcm, token) =>
            {
                // A copy: the coordinator lets go of the clip once it is handed over.
                await pending.Writer.WriteAsync((segment, asset, pcm.ToArray()), token).ConfigureAwait(false);
                // A failure in the writing ends the narration at the next passage, not at the end.
                if (writing.IsFaulted) await writing.ConfigureAwait(false);
            }, progress, ct).ConfigureAwait(false);
            pending.Writer.Complete();
        }
        catch (Exception ex)
        {
            pending.Writer.TryComplete(ex);
            try { await writing.ConfigureAwait(false); } catch (Exception) { /* the narration's own failure is the one to report */ }
            throw;
        }
        await writing.ConfigureAwait(false);
        // The plan as it was given: what it left for a model is counted against what was spoken.
        return writer.Complete(plan, documentName);
    }
}

/// <summary>A narration was stopped because the maths model could not be asked; nothing was saved.</summary>
public sealed class MathsNotInterpretedException(string message) : InvalidOperationException(message);

/// <summary>A chapter of an exported audiobook.</summary>
public sealed record AudiobookChapter(string Title, double StartSeconds);

/// <summary>
/// Exports a saved narration as one audiobook file (M4B: AAC with chapters) for listening anywhere.
/// Encoding is ffmpeg's, which must be installed; playback inside the application never needs it.
/// </summary>
public static class AudiobookExporter
{
    /// <summary>The ffmpeg on the PATH, or null.</summary>
    public static string? FindFfmpeg()
    {
        foreach (string dir in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            string candidate = Path.Combine(dir, OperatingSystem.IsWindows() ? "ffmpeg.exe" : "ffmpeg");
            if (File.Exists(candidate)) return candidate;
        }
        return null;
    }

    /// <summary>
    /// A chapter at each heading that is read, named by it; one chapter for the whole when the
    /// document has none. Times are where each heading's passage starts in the recording.
    /// </summary>
    public static IReadOnlyList<AudiobookChapter> Chapters(NarrationPlan plan, IReadOnlyDictionary<string, double> segmentStarts, string documentTitle)
    {
        ArgumentNullException.ThrowIfNull(plan);
        ArgumentNullException.ThrowIfNull(segmentStarts);
        var units = plan.Units.ToDictionary(u => u.Id);
        var chapters = new List<AudiobookChapter>();
        foreach (var segment in plan.Segments)
        {
            if (!segmentStarts.TryGetValue(segment.Id, out double start)) continue;
            if (segment.ReadingUnitIds.Count == 0 || !units.TryGetValue(segment.ReadingUnitIds[0], out var unit)) continue;
            if (unit.Kind is not (ReadingUnitKind.Heading or ReadingUnitKind.Title)) continue;
            string title = string.Join(' ', unit.OriginalText.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
            if (title.Length == 0) continue;
            if (title.Length > 120) title = title[..120].TrimEnd() + "…";
            // A heading right at another's start (a title over a section) keeps the first.
            if (chapters.Count > 0 && start - chapters[^1].StartSeconds < 1.0) continue;
            chapters.Add(new AudiobookChapter(title, start));
        }
        if (chapters.Count == 0 || chapters[0].StartSeconds > 0.5) chapters.Insert(0, new AudiobookChapter(documentTitle, 0));
        return chapters;
    }

    /// <summary>ffmpeg's metadata file for a title and chapters, times in milliseconds.</summary>
    public static string Metadata(string title, string? author, IReadOnlyList<AudiobookChapter> chapters, double totalSeconds)
    {
        static string Escape(string s) => s.Replace("\\", "\\\\").Replace("=", "\\=").Replace(";", "\\;").Replace("#", "\\#").Replace("\n", " ");
        var sb = new StringBuilder(";FFMETADATA1\n");
        sb.Append("title=").Append(Escape(title)).Append('\n');
        sb.Append("album=").Append(Escape(title)).Append('\n');
        if (!string.IsNullOrWhiteSpace(author)) sb.Append("artist=").Append(Escape(author)).Append('\n');
        sb.Append("genre=Audiobook\n");
        for (int i = 0; i < chapters.Count; i++)
        {
            long start = (long)Math.Round(chapters[i].StartSeconds * 1000);
            long end = (long)Math.Round((i + 1 < chapters.Count ? chapters[i + 1].StartSeconds : totalSeconds) * 1000);
            sb.Append("[CHAPTER]\nTIMEBASE=1/1000\n");
            sb.Append(CultureInfo.InvariantCulture, $"START={start}\nEND={Math.Max(end, start + 1)}\n");
            sb.Append("title=").Append(Escape(chapters[i].Title)).Append('\n');
        }
        return sb.ToString();
    }

    /// <summary>
    /// Writes the recording as an M4B at <paramref name="outputPath"/>.
    /// </summary>
    /// <exception cref="InvalidOperationException">ffmpeg is missing or failed, or the recording is incomplete; the message says which.</exception>
    public static async Task<IReadOnlyList<AudiobookChapter>> ExportAsync(
        IRecordedNarration recording, string title, string? author, string outputPath,
        IProgress<(int Done, int Total)>? progress = null, CancellationToken ct = default, string? ffmpegPath = null)
    {
        ArgumentNullException.ThrowIfNull(recording);
        string ffmpeg = ffmpegPath ?? FindFfmpeg()
            ?? throw new InvalidOperationException("Exporting an audiobook needs ffmpeg, which was not found on the PATH. Install it (for example 'sudo apt install ffmpeg') and try again.");
        var plan = recording.Plan;

        // First pass: lengths, for the chapters, which ffmpeg wants before the audio. From what
        // is recorded about each clip, not from its audio, which would be decoded twice.
        var starts = new Dictionary<string, double>(StringComparer.Ordinal);
        AudioFormat? format = null;
        double total = 0;
        foreach (var segment in plan.Segments)
        {
            var asset = await recording.TryGetAssetAsync(segment, ct).ConfigureAwait(false)
                ?? throw new InvalidOperationException($"The saved narration is missing the passage '{segment.Id}'.");
            if (format is { } f && (f.SampleRate != asset.Format.SampleRate || f.Channels != asset.Format.Channels))
                throw new InvalidOperationException("The saved narration's passages are not all in one audio format.");
            format ??= asset.Format;
            starts[segment.Id] = total;
            total += asset.DurationSeconds;
        }
        if (format is not { } audio) throw new InvalidOperationException("The saved narration has no passages.");
        var chapters = Chapters(plan, starts, title);

        string metadata = Path.Combine(Path.GetTempPath(), $"rail-narrator-{Guid.NewGuid():N}.ffmeta");
        string temp = outputPath + "." + Guid.NewGuid().ToString("N") + ".tmp.m4b";
        await File.WriteAllTextAsync(metadata, Metadata(title, author, chapters, total), new UTF8Encoding(false), ct).ConfigureAwait(false);
        try
        {
            var start = new ProcessStartInfo(ffmpeg)
            {
                RedirectStandardInput = true, RedirectStandardError = true, RedirectStandardOutput = true, UseShellExecute = false,
            };
            foreach (string a in (string[])
                     ["-hide_banner", "-loglevel", "error", "-y",
                      "-f", "f32le", "-ar", audio.SampleRate.ToString(CultureInfo.InvariantCulture), "-ac", audio.Channels.ToString(CultureInfo.InvariantCulture), "-i", "pipe:0",
                      "-f", "ffmetadata", "-i", metadata, "-map", "0:a", "-map_metadata", "1", "-map_chapters", "1",
                      "-c:a", "aac", "-b:a", audio.Channels > 1 ? "96k" : "64k", "-movflags", "+faststart", "-f", "mp4", temp])
                start.ArgumentList.Add(a);
            using var process = Process.Start(start) ?? throw new InvalidOperationException("ffmpeg could not be started.");
            var errors = process.StandardError.ReadToEndAsync(ct);
            _ = process.StandardOutput.ReadToEndAsync(ct);
            try
            {
                int done = 0;
                foreach (var segment in plan.Segments)
                {
                    ct.ThrowIfCancellationRequested();
                    var clip = await recording.TryGetAsync(segment, ct).ConfigureAwait(false)
                        ?? throw new InvalidOperationException($"The saved narration's audio for the passage '{segment.Id}' could not be read.");
                    await process.StandardInput.BaseStream.WriteAsync(ToFloat32(clip), ct).ConfigureAwait(false);
                    progress?.Report((++done, plan.SegmentCount));
                }
                process.StandardInput.Close();
                await process.WaitForExitAsync(ct).ConfigureAwait(false);
            }
            catch (Exception) when (!process.HasExited)
            {
                try { process.Kill(entireProcessTree: true); } catch (InvalidOperationException) { }
                throw;
            }
            catch (IOException)
            {
                // ffmpeg closed its input: its own message, below, says why.
                await process.WaitForExitAsync(ct).ConfigureAwait(false);
            }
            if (process.ExitCode != 0)
            {
                string said = (await errors.ConfigureAwait(false)).Trim();
                throw new InvalidOperationException("ffmpeg could not write the audiobook: " + (said.Length > 400 ? said[..400] + "…" : said.Length > 0 ? said : $"exit code {process.ExitCode}"));
            }
            File.Move(temp, outputPath, overwrite: true);
            return chapters;
        }
        finally
        {
            try { File.Delete(metadata); } catch (IOException) { }
            try { File.Delete(temp); } catch (IOException) { }
        }
    }

    private static ReadOnlyMemory<byte> ToFloat32(RecordedClip clip)
    {
        var format = clip.Asset.Format;
        if (format.SampleFormat == SampleFormat.Float32LittleEndian) return clip.Pcm;
        int samples = clip.Pcm.Length / (format.BytesPerFrame / format.Channels);
        var bytes = new byte[samples * 4];
        int width = format.BytesPerFrame / format.Channels;
        for (int i = 0; i < samples; i++)
            BitConverter.TryWriteBytes(bytes.AsSpan(i * 4), AudioSamples.ReadSample(clip.Pcm.Span, i * width, format.SampleFormat));
        return bytes;
    }
}
