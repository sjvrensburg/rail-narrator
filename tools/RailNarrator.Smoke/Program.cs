using System.Diagnostics;
using System.Text;
using RailNarrator.Audio;
using RailNarrator.Domain;
using RailNarrator.Pdf;
using RailNarrator.Speech;
using RailNarrator.TestFixtures;

namespace RailNarrator.Smoke;

/// <summary>
/// Headless end-to-end harness.
/// </summary>
/// <remarks>
/// This exists because the interesting guarantees — real PDF in, real geometry out, real speech
/// synthesised, real timings measured — cannot be checked from a unit test without hardware or a
/// model, and cannot be checked from a GUI at all in CI. Every command prints what it measured rather
/// than asserting success, so "implemented and verified" is a claim backed by output.
/// </remarks>
internal static class Program
{
    private static async Task<int> Main(string[] args)
    {
        if (args.Length == 0) { Usage(); return 2; }
        try
        {
            return args[0].ToLowerInvariant() switch
            {
                "fixture" => Fixture(args.Skip(1).ToArray()),
                "extract" => await ExtractAsync(args.Skip(1).ToArray()).ConfigureAwait(false),
                "plan" => await PlanAsync(args.Skip(1).ToArray()).ConfigureAwait(false),
                "speak" => await SpeakAsync(args.Skip(1).ToArray()).ConfigureAwait(false),
                "audio" => Audio(),
                "providers" => await ProvidersAsync(args.Skip(1).ToArray()).ConfigureAwait(false),
                "all" => await AllAsync(args.Skip(1).ToArray()).ConfigureAwait(false),
                _ => Unknown(args[0]),
            };
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"FAILED: {ex.GetType().Name}: {ex.Message}");
            Console.Error.WriteLine(ex.StackTrace);
            return 1;
        }
    }

    private static void Usage()
    {
        Console.WriteLine("""
            rail-narrator-smoke — headless verification harness

              fixture <name> <out.pdf>     write a synthetic fixture PDF
              extract <pdf> [--pages N]    dump extraction: coverage, blocks, lines, char boxes
              plan    <pdf>                extract, build units and a narration plan, dump the script
              speak   <pdf> [--provider kokoro|zonos3|fake] [--model PATH] [--voice V]
                                           synthesise the plan with a real provider and report
                                           measured latency, RTF, peak and format
              audio   [--device N]         enumerate audio devices and report the measured clock
              providers [--kokoro-model PATH] [--zonos-url URL]
                                           probe each provider's capabilities and run a self-test
              all     [--kokoro-model PATH]
                                           run the whole chain and print a pass/fail summary
            """);
    }

    private static int Fail(string message) { Console.Error.WriteLine(message); return 1; }

    private static int Unknown(string command)
    {
        Console.Error.WriteLine($"Unknown command '{command}'.");
        Usage();
        return 2;
    }

    // ---- fixtures -----------------------------------------------------------------

    private static int Fixture(string[] args)
    {
        if (args.Length < 2) return Fail("usage: fixture <name> <out.pdf>");
        string name = args[0], path = args[1];
        switch (name)
        {
            case "single":
                SyntheticPdf.WriteSingleColumn(path,
                [
                    "Narration begins with the geometry of the page, not with the text alone.",
                    "Each sentence keeps the disjoint rectangles of the lines it occupies.",
                    "A highlight that covers the gutter between columns would be wrong.",
                ]);
                break;
            case "twocol":
                SyntheticPdf.WriteTwoColumn(path,
                    left: ["Alpha one in the left column.", "Alpha two in the left column.", "Alpha three in the left column."],
                    right: ["Bravo one in the right column.", "Bravo two in the right column.", "Bravo three in the right column."]);
                break;
            case "maths":
                SyntheticPdf.WriteWithDisplayMaths(path,
                    proseBefore: ["Consider the arithmetic mean of a sample of observations."],
                    equation: "x = (1/n) sum x",
                    proseAfter: ["The estimator is unbiased under the stated assumptions."]);
                break;
            case "hyphen":
                SyntheticPdf.WriteHyphenated(path,
                    ("The infor-", "mation retrieval pipeline was evaluated."),
                    ("A state-", "of-the-art baseline was used for comparison."));
                break;
            case "running":
                SyntheticPdf.WriteWithRunningElements(path,
                    header: "Journal of Reproducible Narration", footer: "Page",
                    bodyLines:
                    [
                        "The body text is what a listener actually wants to hear.",
                        "Running headers repeat on every page and carry no content.",
                        "Suppressing them is a rule the reader can inspect and reverse.",
                        "The rest of the paragraph continues here as usual.",
                    ],
                    pageCount: 4);
                break;
            case "crosspage":
                SyntheticPdf.WriteCrossPageSentence(path,
                    "This sentence begins on the first page and continues",
                    "without a break onto the second page of the document.");
                break;
            case "scan":
                SyntheticPdf.WriteTextless(path, pageCount: 2);
                break;
            default:
                return Fail($"Unknown fixture '{name}'. Try: single twocol maths hyphen running crosspage scan");
        }
        Console.WriteLine($"wrote {path} ({new FileInfo(path).Length} bytes)");
        return 0;
    }

    // ---- extraction ---------------------------------------------------------------

    private static async Task<int> ExtractAsync(string[] args)
    {
        if (args.Length < 1) return Fail("usage: extract <pdf> [--pages N]");
        string path = args[0];
        int? pageLimit = ArgInt(args, "--pages");
        if (!File.Exists(path)) return Fail($"No such file: {path}");

        var snapshot = await ExtractSnapshotAsync(path, pageLimit).ConfigureAwait(false);
        DumpSnapshot(snapshot);
        return 0;
    }

    private static async Task<ExtractionSnapshot> ExtractSnapshotAsync(string path, int? pageLimit = null)
    {
        var options = new ExtractionOptions("text-layout", OcrPolicy.Off, 1024, false, ExtractorVersions.Current);
        using var source = new RailReaderExtractionSource();
        source.WithBackend(ExtractionBackend.ModelFree());
        var sw = Stopwatch.StartNew();
        var snapshot = await source.ExtractAsync(new ExtractionRequest(
            path, null, pageLimit is { } n ? Enumerable.Range(0, n).ToList() : null,
            options, NarrationFilterSettings.Default));
        sw.Stop();
        Console.WriteLine($"extraction: {snapshot.PageCount} page(s) in {sw.ElapsedMilliseconds} ms " +
                          $"({(snapshot.PageCount > 0 ? sw.ElapsedMilliseconds / (double)snapshot.PageCount : 0):F0} ms/page)");
        return snapshot;
    }

    private static void DumpSnapshot(ExtractionSnapshot snapshot)
    {
        Console.WriteLine($"identity: {snapshot.Identity}");
        Console.WriteLine($"frame: {snapshot.Frame}");
        foreach (var w in snapshot.AllWarnings()) Console.WriteLine($"  WARNING {w}");

        foreach (var page in snapshot.Pages)
        {
            Console.WriteLine($"\n--- page {page.PageIndex} ({page.PageWidth:F0}x{page.PageHeight:F0} pt) ---");
            Console.WriteLine($"coverage: chars={page.Coverage.CharCount} native={page.Coverage.TextLayerPresent} " +
                              $"ocr={page.Coverage.OcrUsed} glyphCoverage={page.Coverage.GlyphCoverage:P2} sparse={page.Coverage.IsSparse}");
            Console.WriteLine($"blocks={page.Blocks.Count} lines={page.Lines.Count} chars={page.Chars.Count}");
            foreach (var b in page.OrderedBlocks)
            {
                Console.WriteLine($"  block {b.BlockId} kind={b.Kind} order={b.Order} " +
                                  $"bbox=({b.BBox.Left:F0},{b.BBox.Top:F0},{b.BBox.Right:F0},{b.BBox.Bottom:F0}) lines={b.LineIndices.Count}");
                foreach (int li in b.LineIndices)
                {
                    var line = page.Lines[li];
                    string text = page.TextOf(line.Text) ?? "";
                    Console.WriteLine($"    line {line.LineId} range={line.Text} " +
                                      $"rect=({line.Rect.Left:F0},{line.Rect.Top:F0},{line.Rect.Right:F0},{line.Rect.Bottom:F0}) ocr={line.FromOcr}");
                    Console.WriteLine($"      text=\"{Truncate(text.Replace("\n", "\\n"), 90)}\"");
                    // Diagnostic: the advances between marking glyphs, which is what word-space
                    // detection has to separate from ordinary letter spacing.
                    var marking = page.Chars
                        .Where(c => c.Index >= line.Text.Start && c.Index < line.Text.End && !c.Rect.IsEmpty
                                    && !char.IsWhiteSpace(SafeChar(page.Text, c.Index)))
                        .OrderBy(c => c.Rect.Left).ToList();
                    if (marking.Count > 1)
                    {
                        var advances = new List<float>();
                        var gaps = new List<float>();
                        for (int k = 1; k < marking.Count; k++)
                        {
                            advances.Add(marking[k].Rect.Left - marking[k - 1].Rect.Left);
                            gaps.Add(marking[k].Rect.Left - marking[k - 1].Rect.Right);
                        }
                        var sorted = advances.OrderBy(a => a).ToList();
                        var sortedGaps = gaps.OrderBy(a => a).ToList();
                        Console.WriteLine($"      marking glyphs={marking.Count} medianHeight={marking.Average(m => m.Rect.Height):F2}");
                        Console.WriteLine($"      advances: min={sorted[0]:F2} median={sorted[sorted.Count / 2]:F2} max={sorted[^1]:F2}");
                        Console.WriteLine($"      gaps:     min={sortedGaps[0]:F2} p50={sortedGaps[sortedGaps.Count / 2]:F2} p75={sortedGaps[sortedGaps.Count * 3 / 4]:F2} max={sortedGaps[^1]:F2}");
                        var pairs = gaps.Select((g, k) => (g, from: SafeChar(page.Text, marking[k].Index), to: SafeChar(page.Text, marking[k + 1].Index)))
                                        .OrderByDescending(p => p.g).Take(8).ToList();
                        Console.WriteLine($"      largest gaps: {string.Join("  ", pairs.Select(p => $"'{p.from}{p.to}'={p.g:F2}"))}");
                        Console.WriteLine($"      gap list: {string.Join(" ", gaps.Select(g => g.ToString("F1")))}");
                        Console.WriteLine($"      marking text: \"{new string(marking.Select(m => SafeChar(page.Text, m.Index)).ToArray())}\"");
                    }
                }
            }
            if (page.Chars.Count > 0)
            {
                var first = page.Chars[0];
                var last = page.Chars[^1];
                Console.WriteLine($"  first char idx={first.Index} '{SafeChar(page.Text, first.Index)}' rect=({first.Rect.Left:F1},{first.Rect.Top:F1},{first.Rect.Right:F1},{first.Rect.Bottom:F1})");
                Console.WriteLine($"  last  char idx={last.Index} '{SafeChar(page.Text, last.Index)}' rect=({last.Rect.Left:F1},{last.Rect.Top:F1},{last.Rect.Right:F1},{last.Rect.Bottom:F1})");
            }
        }
    }

    private static char SafeChar(string s, int i) => i >= 0 && i < s.Length ? s[i] : '?';

    // ---- plan ---------------------------------------------------------------------

    private static async Task<int> PlanAsync(string[] args)
    {
        if (args.Length < 1) return Fail("usage: plan <pdf>");
        var snapshot = await ExtractSnapshotAsync(args[0]).ConfigureAwait(false);

        var builder = new ReadingUnitBuilder(new ReadingUnitBuilderOptions(NarrationFilterSettings.Default));
        var units = builder.Build(snapshot, out var diagnostics);
        foreach (var d in diagnostics) Console.WriteLine($"  builder: {d}");
        Console.WriteLine($"units: {units.Count} ({units.Count(u => u.IsMaths)} mathematical)");

        var planner = new NarrationPlanner();
        var plan = await planner.BuildPlanAsync(snapshot, units, NarrationFilterSettings.Default).ConfigureAwait(false);
        Console.WriteLine($"plan: {plan.SegmentCount} segment(s), id={plan.Header.PlanId[..16]}");

        var map = new SourceMap(plan);
        int index = 0;
        foreach (var segment in plan.Segments)
        {
            var anchors = map.AnchorsForSegment(segment.Id);
            var pages = map.PagesFor(anchors);
            Console.WriteLine($"\n[{index++}] {segment.Id} kind={segment.Kind} prov={segment.Provenance} " +
                              $"links={segment.SourceLinks.Count} anchors={anchors.Count} pages=[{string.Join(",", pages)}]");
            Console.WriteLine($"    spoken: \"{Truncate(segment.SpokenText, 160)}\"");
            foreach (var link in segment.SourceLinks.Take(4))
                Console.WriteLine($"      link {link.SpokenRange} -> [{string.Join(", ", link.AnchorIds)}] " +
                                  $"\"{Truncate(link.SpokenRange.Length > 0 && link.SpokenRange.End <= segment.SpokenText.Length ? segment.SpokenText.Substring(link.SpokenRange.Start, link.SpokenRange.Length) : "", 60)}\"");
            foreach (var w in segment.Warnings) Console.WriteLine($"      WARN {w}");
        }
        return 0;
    }

    // ---- speak --------------------------------------------------------------------

    private static async Task<int> SpeakAsync(string[] args)
    {
        if (args.Length < 1) return Fail("usage: speak <pdf> [--provider kokoro|zonos3|fake] [--model PATH] [--voice V] [--url URL] [--segments N]");
        string path = args[0];
        string provider = ArgValue(args, "--provider") ?? "kokoro";
        string? model = ArgValue(args, "--model") ?? Environment.GetEnvironmentVariable("RAIL_NARRATOR_KOKORO_MODEL");
        string? voice = ArgValue(args, "--voice");
        string? url = ArgValue(args, "--url");
        int segments = ArgInt(args, "--segments") ?? 3;
        string? outDir = ArgValue(args, "--out");

        var snapshot = await ExtractSnapshotAsync(path).ConfigureAwait(false);
        var builder = new ReadingUnitBuilder(new ReadingUnitBuilderOptions(NarrationFilterSettings.Default));
        var units = builder.Build(snapshot);
        var planner = new NarrationPlanner();
        var plan = await planner.BuildPlanAsync(snapshot, units, NarrationFilterSettings.Default).ConfigureAwait(false);
        if (plan.SegmentCount == 0) return Fail("The plan has no segments; nothing to synthesise.");

        ITtsProvider tts = provider switch
        {
            "kokoro" => new KokoroTtsProvider(new KokoroProviderSettings(
                model ?? throw new ArgumentException("--model is required for the kokoro provider (or set RAIL_NARRATOR_KOKORO_MODEL)."))),
            "zonos3" => new ZonosHttpTtsProvider(new ZonosProviderSettings(BaseUrl: url ?? "http://127.0.0.1:19191")),
            "fake" => new FakeTtsProvider(),
            _ => throw new ArgumentException($"Unknown provider '{provider}'."),
        };

        await using (tts)
        {
            var caps = await tts.GetCapabilitiesAsync().ConfigureAwait(false);
            Console.WriteLine($"provider: {caps.ProviderId} rev={caps.ModelRevision} available={caps.IsAvailable}");
            if (!caps.IsAvailable) return Fail($"Provider unavailable: {caps.Unavailable}");
            Console.WriteLine($"  formats: {string.Join(", ", caps.Formats)}");
            Console.WriteLine($"  nativeTiming={caps.NativeTiming} streaming={caps.SupportsStreaming} maxChars={caps.MaxTextChars}");

            var voices = await tts.ListVoicesAsync().ConfigureAwait(false);
            Console.WriteLine($"  voices: {voices.Count} (first: {string.Join(", ", voices.Take(5).Select(v => v.Id))})");
            string voiceId = voice ?? voices.FirstOrDefault(v => v.IsDefault)?.Id ?? voices.FirstOrDefault()?.Id
                ?? throw new InvalidOperationException("The provider offers no voices.");
            var format = caps.PreferredFormat ?? AudioFormat.Mono44100Float32;

            if (outDir is { } dir) Directory.CreateDirectory(dir);
            double totalAudio = 0, totalWall = 0;
            int done = 0;

            foreach (var segment in plan.Segments.Take(segments))
            {
                var sw = Stopwatch.StartNew();
                var result = await tts.SynthesizeAsync(new TtsSynthesisRequest(
                    segment.SpokenText, voiceId, segment.Language, format), CancellationToken.None).ConfigureAwait(false);
                sw.Stop();

                float peak = Pcm.Peak(result.Pcm.Span, result.Format);
                float rms = Pcm.Rms(result.Pcm.Span, result.Format);
                totalAudio += result.DurationSeconds;
                totalWall += sw.Elapsed.TotalSeconds;
                done++;

                Console.WriteLine($"\n[{segment.Id}] {result.DurationSeconds:F2}s audio in {sw.ElapsedMilliseconds} ms " +
                                  $"(RTF {sw.Elapsed.TotalSeconds / Math.Max(0.001, result.DurationSeconds):F2})");
                Console.WriteLine($"  format={result.Format} frames={result.FrameCount} peak={peak:F4} rms={rms:F4} timings={result.Timings.Count} ({result.TimingProvenance})");
                Console.WriteLine($"  text: \"{Truncate(segment.SpokenText, 100)}\"");
                foreach (var w in result.Warnings) Console.WriteLine($"  WARN {w}");

                if (peak < 0.005f) Console.WriteLine("  *** EFFECTIVELY SILENT — this would be an inaudible reading session ***");
                if (outDir is { } d)
                {
                    string wavPath = Path.Combine(d, $"{segment.Id}.wav");
                    File.WriteAllBytes(wavPath, WavCodec.Write(result.Format, result.Pcm.Span));
                    Console.WriteLine($"  wrote {wavPath}");
                }
            }

            Console.WriteLine($"\nsummary: {done} clip(s), {totalAudio:F2}s of audio in {totalWall:F2}s wall " +
                              $"(aggregate RTF {totalWall / Math.Max(0.001, totalAudio):F2})");
            Console.WriteLine(totalAudio > 0 && totalWall / totalAudio < 1.0
                ? "preparation can keep up with playback at this rate"
                : "preparation CANNOT keep up with playback — buffering will be audible");
        }
        return 0;
    }

    // ---- audio --------------------------------------------------------------------

    private static int Audio()
    {
        string? version;
        try { version = PortAudioOutputDevice.Version; }
        catch (Exception ex) { return Fail($"PortAudio is not usable: {ex.Message}"); }
        Console.WriteLine($"PortAudio: {version}");

        string? initError = PortAudioOutputDevice.Initialise();
        Console.WriteLine($"initialise: {(initError ?? "ok")}");

        using var device = new PortAudioOutputDevice();
        var devices = device.Enumerate();
        Console.WriteLine($"output devices: {devices.Count}");
        foreach (var d in devices)
            Console.WriteLine($"  [{d.Id}] {d.Name} rate={d.PreferredSampleRate} ch={d.Channels} " +
                              $"latency={(d.OutputLatencySeconds is { } l ? $"{l * 1000:F1}ms" : "unknown")} default={d.IsDefault}");
        if (devices.Count == 0)
        {
            Console.WriteLine("No output device: a headless session has no sound server. Playback tests use the fake device.");
            return 0;
        }

        // Measure the clock on a real tone. This is the number the highlight accuracy depends on.
        var target = devices.FirstOrDefault(d => d.IsDefault) ?? devices[0];
        var format = new AudioFormat(target.PreferredSampleRate, Math.Min(2, Math.Max(1, target.Channels)), SampleFormat.Float32LittleEndian);
        try
        {
            device.Open(target.Id, format);
            Console.WriteLine($"\nopened device {target.Id} at {format.SampleRate} Hz / {format.Channels} ch");
            if (device is PortAudioOutputDevice pa) Console.WriteLine($"device buffer capacity: {pa.BufferCapacityFrames} frames ({pa.BufferCapacityFrames * 1000.0 / format.SampleRate:F1} ms)");

            device.Start();
            var sw = Stopwatch.StartNew();
            int total = 0;
            const int chunk = 512;
            var buffer = new byte[chunk * 4 * 2];
            var samples = new float[chunk * 2];
            while (sw.Elapsed.TotalSeconds < 0.75)
            {
                for (int i = 0; i < samples.Length; i++)
                    samples[i] = (float)(0.15 * Math.Sin(2 * Math.PI * 220 * i / format.SampleRate));
                for (int i = 0; i < samples.Length; i++)
                    BitConverter.TryWriteBytes(buffer.AsSpan(i * 4, 4), samples[i]);
                device.Write(buffer, chunk);
                total += chunk;
            }
            double wall = sw.Elapsed.TotalSeconds;
            long presented = device.PresentedFrames;
            Console.WriteLine($"wrote {total} frames ({total / (double)format.SampleRate:F3}s) in {wall:F3}s wall");
            Console.WriteLine($"presented {presented} frames ({presented / (double)format.SampleRate:F3}s); queued {device.QueuedFrames}");
            Console.WriteLine($"clock error vs wall: {(presented / (double)format.SampleRate - wall) * 1000:F1} ms (negative = clock behind wall, as expected by the queue depth)");
            device.Pause();
            Console.WriteLine($"HasDrained after pause: {device.HasDrained}");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"playback measurement failed: {ex.Message}");
        }
        return 0;
    }

    // ---- providers ----------------------------------------------------------------

    private static async Task<int> ProvidersAsync(string[] args)
    {
        string? kokoroModel = ArgValue(args, "--kokoro-model") ?? Environment.GetEnvironmentVariable("RAIL_NARRATOR_KOKORO_MODEL");
        string zonosUrl = ArgValue(args, "--zonos-url") ?? "http://127.0.0.1:19191";

        var registry = new SpeechProviderRegistry();
        registry.Register(new FakeTtsProvider());
        if (kokoroModel is not null) registry.Register(new KokoroTtsProvider(new KokoroProviderSettings(kokoroModel)));
        else Console.WriteLine("kokoro: skipped (no --kokoro-model and no RAIL_NARRATOR_KOKORO_MODEL)");
        registry.Register(new ZonosHttpTtsProvider(new ZonosProviderSettings(BaseUrl: zonosUrl)));

        foreach (var (id, caps) in await registry.ProbeAllAsync().ConfigureAwait(false))
        {
            Console.WriteLine($"\n{id}: available={caps.IsAvailable} rev={caps.ModelRevision}");
            if (!caps.IsAvailable) { Console.WriteLine($"  unavailable: {caps.Unavailable}"); continue; }
            Console.WriteLine($"  formats={string.Join(", ", caps.Formats)} timing={caps.NativeTiming} maxChars={caps.MaxTextChars}");
            var provider = registry.Get(id)!;
            var voices = await provider.ListVoicesAsync().ConfigureAwait(false);
            string voice = voices.FirstOrDefault(v => v.IsDefault)?.Id ?? voices.FirstOrDefault()?.Id ?? "";
            Console.WriteLine($"  voices={voices.Count}; self-testing '{voice}'…");
            var test = await provider.SelfTestAsync(voice).ConfigureAwait(false);
            Console.WriteLine($"  self-test: ok={test.Succeeded} {test.DurationSeconds:F2}s peak={test.PeakAmplitude:F4} in {test.ElapsedMilliseconds} ms" +
                              (test.Detail is null ? "" : $" — {test.Detail}"));
        }
        return 0;
    }

    // ---- all ----------------------------------------------------------------------

    private static async Task<int> AllAsync(string[] args)
    {
        string? kokoroModel = ArgValue(args, "--kokoro-model") ?? Environment.GetEnvironmentVariable("RAIL_NARRATOR_KOKORO_MODEL");
        var results = new List<(string Step, bool Ok, string Detail)>();

        string pdf = SyntheticPdf.TempPath("smoke-all");
        SyntheticPdf.WriteSingleColumn(pdf,
        [
            "This document exists to prove the whole chain works end to end.",
            "Extraction reads real geometry from a real PDF file.",
            "Planning links each spoken sentence back to its source lines.",
            "Synthesis turns that script into audio with a real speech engine.",
        ]);
        results.Add(("fixture", File.Exists(pdf) && new FileInfo(pdf).Length > 0, pdf));

        var snapshot = await ExtractSnapshotAsync(pdf).ConfigureAwait(false);
        results.Add(("extraction", snapshot.Pages.Count > 0 && snapshot.Pages[0]!.Coverage.CharCount > 50,
            $"{snapshot.Pages.Sum(p => p.Coverage.CharCount)} chars over {snapshot.PageCount} page(s)"));

        var builder = new ReadingUnitBuilder(new ReadingUnitBuilderOptions(NarrationFilterSettings.Default));
        var units = builder.Build(snapshot, out var diag);
        results.Add(("units", units.Count >= 2, $"{units.Count} units; {string.Join(" | ", diag.Take(2))}"));

        var planner = new NarrationPlanner();
        var plan = await planner.BuildPlanAsync(snapshot, units, NarrationFilterSettings.Default).ConfigureAwait(false);
        var map = new SourceMap(plan);
        bool linked = plan.Segments.Count > 0 && plan.Segments.All(s => map.AnchorsForSegment(s.Id).Count > 0);
        results.Add(("plan", linked, $"{plan.SegmentCount} segments, all source-linked = {linked}"));

        // Audio engine against the deterministic fake device: no sound card needed.
        var fake = new FakeAudioOutputDevice();
        await using (var engine = new AudioPlaybackEngine(fake, new AudioEngineOptions(PreferredDeviceRate: 24000)))
        {
            var fakeTts = new FakeTtsProvider();
            await using (fakeTts)
            {
                var result = await fakeTts.SynthesizeAsync(new TtsSynthesisRequest(
                    plan.Segments[0].SpokenText, "fake-default", "en-GB", AudioFormat.Mono24000Float32)).ConfigureAwait(false);
                var asset = new SpeechAsset("a1", plan.Segments[0].Id, "mem", result.Format, result.FrameCount,
                    "fake", "fake/1", "fake-default", "", [], TimingProvenance.ClipBoundary, 1);
                await engine.LoadAsync(asset.SegmentId, asset, result.Pcm).ConfigureAwait(false);
                await engine.PlayAsync().ConfigureAwait(false);
                fake.AdvanceBySeconds(result.DurationSeconds / 2);
                double mid = engine.PositionSeconds;
                fake.DrainAll();
                results.Add(("audio-clock", mid > 0 && mid < result.DurationSeconds,
                    $"mid-clip position {mid:F3}s of {result.DurationSeconds:F3}s"));
            }
        }

        if (kokoroModel is not null)
        {
            var tts = new KokoroTtsProvider(new KokoroProviderSettings(kokoroModel));
            await using (tts)
            {
                var test = await tts.SelfTestAsync("af_heart").ConfigureAwait(false);
                results.Add(("kokoro", test.Succeeded, $"{test.DurationSeconds:F2}s peak={test.PeakAmplitude:F3} in {test.ElapsedMilliseconds}ms {test.Detail}"));
            }
        }
        else results.Add(("kokoro", false, "skipped: no model path given"));

        Console.WriteLine("\n================ SUMMARY ================");
        int failures = 0;
        foreach (var (step, ok, detail) in results)
        {
            Console.WriteLine($"{(ok ? "PASS" : "FAIL")}  {step,-12} {detail}");
            if (!ok) failures++;
        }
        Console.WriteLine($"=========================================");
        return failures == 0 ? 0 : 1;
    }

    // ---- helpers ------------------------------------------------------------------

    private static string Truncate(string s, int max)
    {
        var oneLine = new StringBuilder(s.Replace("\r", " ")).ToString();
        return oneLine.Length <= max ? oneLine : oneLine[..max] + "…";
    }

    private static string? ArgValue(string[] args, string name)
    {
        for (int i = 0; i < args.Length - 1; i++) if (args[i] == name) return args[i + 1];
        return null;
    }

    private static int? ArgInt(string[] args, string name)
        => int.TryParse(ArgValue(args, name), out int v) ? v : null;
}
