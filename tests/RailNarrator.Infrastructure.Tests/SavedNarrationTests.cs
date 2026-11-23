using System.Buffers.Binary;
using RailNarrator.Domain;
using RailNarrator.Infrastructure;
using RailNarrator.Speech;
using RailNarrator.TestFixtures;
using Xunit;

namespace RailNarrator.Infrastructure.Tests;

/// <summary>
/// A whole document narrated to a file beside it, and that file played back with no language model
/// and no speech engine.
/// </summary>
public sealed class SavedNarrationTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "rn-saved-" + Guid.NewGuid().ToString("N"));

    public SavedNarrationTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { }
    }

    private static NarrationPlan PlanWith(params (ReadingUnitKind Kind, string Text)[] passages)
    {
        var units = passages.Select((p, i) =>
        {
            var anchor = SyntheticNarration.Anchor($"a{i}", 0, new PageRect(72, 100 + i * 16, 300, 112 + i * 16));
            return SyntheticNarration.ProseUnit($"u{i}", p.Text, anchor) with { Kind = p.Kind };
        }).ToList();
        return SyntheticNarration.Plan(units);
    }

    private static NarrationPlan Paper() => PlanWith(
        (ReadingUnitKind.Title, "A paper about sums"),
        (ReadingUnitKind.Prose, "This sentence opens the paper and says what it is about."),
        (ReadingUnitKind.Heading, "1 Introduction"),
        (ReadingUnitKind.Prose, "Here the introduction says a little more than the opening did."),
        (ReadingUnitKind.Heading, "2 Method"),
        (ReadingUnitKind.Prose, "And the method is described in one last sentence."));

    private static NarrationEngines Engines(ITtsProvider tts) => new(tts, null, null, null, NarrationCoordinatorOptions.Default);

    // ---- the audio ---------------------------------------------------------------------

    private static float[] Floats(byte[] pcm)
    {
        var samples = new float[pcm.Length / 4];
        for (int i = 0; i < samples.Length; i++) samples[i] = BinaryPrimitives.ReadSingleLittleEndian(pcm.AsSpan(i * 4));
        return samples;
    }

    [Fact]
    public void AClip_ComesBackAtItsOwnLength_WithItsSoundWhereItWas()
    {
        // Silence, then a tone from an exact sample: timings are in samples of the clip, so a
        // codec that shifted the audio would move every highlight.
        const int rate = 24000, onset = 6000, frames = 14321;
        var pcm = new byte[frames * 4];
        for (int i = onset; i < frames; i++)
            BinaryPrimitives.WriteSingleLittleEndian(pcm.AsSpan(i * 4), (float)(0.4 * Math.Sin(2 * Math.PI * 330 * (i - onset) / rate)));

        byte[] stored = NarrationAudio.Encode(pcm, AudioFormat.Mono24000Float32);
        var decoded = NarrationAudio.Decode(stored);

        Assert.NotNull(decoded);
        Assert.Equal(AudioFormat.Mono24000Float32, decoded.Value.Format);
        var samples = Floats(decoded.Value.Pcm);
        Assert.Equal(frames, samples.Length);
        Assert.True(stored.Length < pcm.Length / 8, $"{stored.Length} bytes for {pcm.Length} of PCM is not compressed");
        // Quiet before the onset, sounding after it, the edge within 5 ms.
        int heard = Array.FindIndex(samples, s => Math.Abs(s) > 0.1f);
        Assert.InRange(heard, onset - 120, onset + 120);
        double before = samples.Take(onset - 240).Max(s => Math.Abs(s));
        double after = Math.Sqrt(samples.Skip(onset + 240).Average(s => (double)s * s));
        Assert.True(before < 0.02, $"peak {before:F3} before the tone");
        Assert.InRange(after, 0.2, 0.36);   // a 0.4 sine has an RMS of 0.283
    }

    [Fact]
    public void ARateOpusDoesNotHave_IsKeptAsPcm()
    {
        var format = new AudioFormat(22050, 1, SampleFormat.Float32LittleEndian);
        byte[] pcm = SyntheticNarration.TonePcm(2205, 22050);

        var decoded = NarrationAudio.Decode(NarrationAudio.Encode(pcm, format));

        Assert.NotNull(decoded);
        Assert.Equal(22050, decoded.Value.Format.SampleRate);
        var before = Floats(pcm);
        var after = Floats(decoded.Value.Pcm);
        Assert.Equal(before.Length, after.Length);
        Assert.All(before.Zip(after), p => Assert.InRange(p.Second - p.First, -0.0001f, 0.0001f));
    }

    [Fact]
    public void DataThatIsNotAClip_IsNotDecoded()
    {
        Assert.Null(NarrationAudio.Decode([]));
        Assert.Null(NarrationAudio.Decode(new byte[64]));
        byte[] stored = NarrationAudio.Encode(SyntheticNarration.TonePcm(4800), AudioFormat.Mono24000Float32);
        Assert.Null(NarrationAudio.Decode(stored.AsSpan(0, stored.Length / 2)));   // cut short
    }

    // ---- the file ----------------------------------------------------------------------

    [Fact]
    public async Task ADocumentIsNarratedToAFile_AndPlayedFromItWithNoEngine()
    {
        var plan = Paper();
        string path = Path.Combine(_dir, "paper.narration");
        var progress = new List<int>();

        await using var tts = new FakeTtsProvider();
        var manifest = await NarrationJob.RunAsync(plan, "paper.pdf", path, Engines(tts),
            new SynchronousProgress<NarrationProgress>(p => progress.Add(p.Done)));

        Assert.Equal(6, manifest.Passages);
        Assert.Equal(Enumerable.Range(1, 6), progress);
        Assert.True(manifest.Seconds > 5);
        Assert.Equal("fake", manifest.ProviderId);

        using var saved = SavedNarration.TryOpen(path, plan.Header.Extraction.DocumentContentHash, out string? problem);
        Assert.Null(problem);
        Assert.NotNull(saved);
        Assert.Equal(plan.Segments.Select(s => s.SpokenText), saved.Plan.Segments.Select(s => s.SpokenText));

        // An engine that cannot speak: everything heard comes from the file.
        await using var broken = new FakeTtsProvider(new FakeTtsOptions(FailWith: "no engine is installed"));
        var transport = new FakeAudioTransport();
        await using var coordinator = new NarrationCoordinator(broken, transport);
        await coordinator.LoadPlanAsync(saved.Plan, recording: saved);
        Assert.True(coordinator.PlaysFromRecording);
        await coordinator.PlayAsync();
        await WaitUntil(() => transport.PlayCount > 0, "playback to start");

        Assert.NotEqual(PlaybackState.Error, coordinator.Snapshot.State);
        var loaded = Assert.Single(transport.Loaded);
        Assert.Equal(saved.Plan.Segments[0].Id, loaded.SegmentId);
    }

    [Fact]
    public async Task APassageTheReaderRewrote_IsNotServedFromTheRecording()
    {
        var plan = Paper();
        string path = Path.Combine(_dir, "paper.narration");
        await using var tts = new FakeTtsProvider();
        await NarrationJob.RunAsync(plan, "paper.pdf", path, Engines(tts));
        using var saved = SavedNarration.TryOpen(path, plan.Header.Extraction.DocumentContentHash, out _);
        Assert.NotNull(saved);

        var asRecorded = await saved.TryGetAsync(saved.Plan.Segments[1]);
        var rewritten = await saved.TryGetAsync(saved.Plan.Segments[1] with { SpokenText = "Something else entirely." });

        Assert.NotNull(asRecorded);
        Assert.Equal(asRecorded.Asset.FrameCount * 4, asRecorded.Pcm.Length);
        Assert.Null(rewritten);
    }

    [Fact]
    public async Task ANarrationOfAnotherDocument_IsRefusedWithAReason()
    {
        var plan = Paper();
        string path = Path.Combine(_dir, "paper.narration");
        await using var tts = new FakeTtsProvider();
        await NarrationJob.RunAsync(plan, "paper.pdf", path, Engines(tts));

        var other = SavedNarration.TryOpen(path, "0123456789abcdef", out string? problem);
        Assert.Null(other);
        Assert.Contains("different version of the document", problem, StringComparison.Ordinal);

        Assert.Null(SavedNarration.TryOpen(Path.Combine(_dir, "absent.narration"), "x", out problem));
        Assert.Null(problem);   // no file is not a problem to report

        File.WriteAllText(Path.Combine(_dir, "junk.narration"), "not a zip archive");
        Assert.Null(SavedNarration.TryOpen(Path.Combine(_dir, "junk.narration"), "x", out problem));
        Assert.NotNull(problem);
    }

    [Fact]
    public async Task ANarrationThatFails_LeavesNoFile_AndSaysWhichPassage()
    {
        var plan = Paper();
        string path = Path.Combine(_dir, "paper.narration");
        await using var tts = new FakeTtsProvider(new FakeTtsOptions(FailWith: "the voice model went away"));

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => NarrationJob.RunAsync(plan, "paper.pdf", path, Engines(tts)));

        Assert.Contains("Passage 1 of 6", ex.Message, StringComparison.Ordinal);
        Assert.Contains("the voice model went away", ex.Message, StringComparison.Ordinal);
        Assert.Empty(Directory.GetFiles(_dir));
    }

    // ---- maths the model could not be asked about ---------------------------------------

    private sealed class Unreachable : IMathInterpreter
    {
        public string Revision => "fake/1";
        public string? UnavailableReason => "API key refused (401) by maths.example.";
        public Task<bool> IsAvailableAsync(CancellationToken ct = default) => Task.FromResult(false);
        public Task<MathInterpretationResult> InterpretAsync(MathInterpretationRequest request, CancellationToken ct = default)
            => throw new InvalidOperationException("an unavailable interpreter must not be asked");
    }

    private static (NarrationPlan Plan, NarrationEngines Engines) PaperWithMathsAndNoModel(ITtsProvider tts)
    {
        var plan = Paper();
        // The planner leaves a sentence with maths for the model, holding the sentence as printed.
        var segments = plan.Segments.Select((s, i) => i == 3 ? s with { Provenance = ScriptProvenance.PendingInterpretation } : s).ToList();
        plan = plan with { Segments = segments };
        var resolver = new MathInterpretationResolver(new Unreachable());
        return (plan, new NarrationEngines(tts, null, null, resolver, NarrationCoordinatorOptions.Default));
    }

    [Fact]
    public async Task WhenTheMathsModelCannotBeAsked_TheNarrationStopsAndSaysWhy()
    {
        // What is saved is heard every time: a refused key must not be baked into it unnoticed.
        await using var tts = new FakeTtsProvider();
        var (plan, engines) = PaperWithMathsAndNoModel(tts);
        string path = Path.Combine(_dir, "paper.narration");

        var ex = await Assert.ThrowsAsync<MathsNotInterpretedException>(() => NarrationJob.RunAsync(plan, "paper.pdf", path, engines));

        Assert.Contains("API key refused (401) by maths.example", ex.Message, StringComparison.Ordinal);
        Assert.Empty(Directory.GetFiles(_dir));
    }

    [Fact]
    public async Task ToldToCarryOn_ItSavesTheMathsAsPrinted_AndCountsIt()
    {
        await using var tts = new FakeTtsProvider();
        var (plan, engines) = PaperWithMathsAndNoModel(tts);
        string path = Path.Combine(_dir, "paper.narration");

        var manifest = await NarrationJob.RunAsync(plan, "paper.pdf", path, engines, allowUninterpreted: true);

        Assert.Equal(6, manifest.Passages);
        Assert.Equal(0, manifest.Interpreted);
        Assert.Equal(1, manifest.AsPrinted);
        using var saved = SavedNarration.TryOpen(path, plan.Header.Extraction.DocumentContentHash, out _);
        Assert.NotNull(saved);
        // Saved as spoken, so playing it back asks no model.
        Assert.DoesNotContain(saved.Plan.Segments, s => s.Provenance == ScriptProvenance.PendingInterpretation);
    }

    [Fact]
    public void ThePathIsBesideTheDocument()
        => Assert.Equal(Path.Combine(_dir, "paper.narration"), SavedNarration.PathFor(Path.Combine(_dir, "paper.pdf")));

    // ---- the audiobook -----------------------------------------------------------------

    [Fact]
    public void ChaptersStartAtTheHeadingsThatAreRead()
    {
        var plan = Paper();
        var starts = plan.Segments.Select((s, i) => (s.Id, Start: i * 10.0)).ToDictionary(x => x.Id, x => x.Start);

        var chapters = AudiobookExporter.Chapters(plan, starts, "paper");

        Assert.Equal(["A paper about sums", "1 Introduction", "2 Method"], chapters.Select(c => c.Title));
        Assert.Equal([0.0, 20.0, 40.0], chapters.Select(c => c.StartSeconds));
    }

    [Fact]
    public void ADocumentWithNoHeadings_IsOneChapter()
    {
        var plan = PlanWith((ReadingUnitKind.Prose, "Only a sentence."), (ReadingUnitKind.Prose, "And another."));
        var starts = plan.Segments.Select((s, i) => (s.Id, Start: i * 3.0)).ToDictionary(x => x.Id, x => x.Start);

        var chapter = Assert.Single(AudiobookExporter.Chapters(plan, starts, "notes"));
        Assert.Equal(new AudiobookChapter("notes", 0), chapter);
    }

    [Fact]
    public void TheMetadataGivesEachChapterItsSpan_AndEscapesWhatFfmpegReads()
    {
        string metadata = AudiobookExporter.Metadata("Sums; a = b", "A. Author",
            [new AudiobookChapter("Start", 0), new AudiobookChapter("2 Method #1", 12.5)], 30);

        Assert.StartsWith(";FFMETADATA1\n", metadata, StringComparison.Ordinal);
        Assert.Contains("title=Sums\\; a \\= b\n", metadata, StringComparison.Ordinal);
        Assert.Contains("artist=A. Author\n", metadata, StringComparison.Ordinal);
        Assert.Contains("START=0\nEND=12500\ntitle=Start\n", metadata, StringComparison.Ordinal);
        Assert.Contains("START=12500\nEND=30000\ntitle=2 Method \\#1\n", metadata, StringComparison.Ordinal);
    }

    [Fact]
    public async Task WithFfmpeg_TheRecordingBecomesAnAudiobookWithChapters()
    {
        if (AudiobookExporter.FindFfmpeg() is null) return;   // encoding is ffmpeg's; without it there is nothing to run
        var plan = Paper();
        string path = Path.Combine(_dir, "paper.narration");
        await using var tts = new FakeTtsProvider();
        var manifest = await NarrationJob.RunAsync(plan, "paper.pdf", path, Engines(tts));
        using var saved = SavedNarration.TryOpen(path, plan.Header.Extraction.DocumentContentHash, out _);
        string book = Path.Combine(_dir, "paper.m4b");

        var chapters = await AudiobookExporter.ExportAsync(saved!, "A paper about sums", null, book);

        Assert.Equal(3, chapters.Count);
        var bytes = File.ReadAllBytes(book);
        Assert.True(bytes.Length > 2000);
        Assert.Equal("ftyp"u8.ToArray(), bytes[4..8]);
        Assert.Contains("1 Introduction", System.Text.Encoding.Latin1.GetString(bytes), StringComparison.Ordinal);
        Assert.Single(Directory.GetFiles(_dir, "*.m4b"));
        Assert.True(manifest.Seconds > 0);
    }

    [Fact]
    public async Task WithoutFfmpeg_TheExportSaysWhatIsMissing()
    {
        var plan = Paper();
        string path = Path.Combine(_dir, "paper.narration");
        await using var tts = new FakeTtsProvider();
        await NarrationJob.RunAsync(plan, "paper.pdf", path, Engines(tts));
        using var saved = SavedNarration.TryOpen(path, plan.Header.Extraction.DocumentContentHash, out _);

        var ex = await Assert.ThrowsAnyAsync<Exception>(() =>
            AudiobookExporter.ExportAsync(saved!, "t", null, Path.Combine(_dir, "out.m4b"), ffmpegPath: Path.Combine(_dir, "no-such-ffmpeg")));

        Assert.Empty(Directory.GetFiles(_dir, "*.m4b"));
        Assert.NotNull(ex.Message);
    }

    private sealed class SynchronousProgress<T>(Action<T> report) : IProgress<T>
    {
        public void Report(T value) => report(value);
    }

    private static async Task WaitUntil(Func<bool> condition, string what, int timeoutMs = 5000)
    {
        long deadline = Environment.TickCount64 + timeoutMs;
        while (Environment.TickCount64 < deadline)
        {
            if (condition()) return;
            await Task.Delay(10);
        }
        Assert.Fail($"Timed out waiting for {what}.");
    }
}
