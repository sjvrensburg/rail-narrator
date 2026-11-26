using System.Globalization;
using RailNarrator.Domain;
using RailNarrator.Infrastructure;

namespace RailNarrator.App.Composition;

/// <summary>
/// The application without its window: narrate a whole document to a file beside it, or export a
/// saved narration as an audiobook. Uses the reader's own settings, engines and caches.
/// </summary>
/// <remarks>
/// <code>
/// rail-narrator narrate paper.pdf [--out paper.narration] [--voice ID] [--m4b [book.m4b]] [--force] [--allow-uninterpreted]
/// rail-narrator export  paper.pdf [--m4b book.m4b]
/// </code>
/// Nothing here may touch Avalonia: it runs before the desktop runtime starts, and on a machine
/// with no display. On Windows the application is a GUI program with no console, so these commands
/// are run as <c>rail-narrator-cli</c> (the RailNarrator.Cli project), which calls this class.
/// </remarks>
public static class HeadlessNarration
{
    /// <summary>The command a reader types: <c>rail-narrator</c>, or <c>rail-narrator-cli</c> on Windows.</summary>
    public static string CommandName
        => Path.GetFileNameWithoutExtension(Environment.ProcessPath) is { Length: > 0 } name && name.StartsWith("rail-narrator", StringComparison.Ordinal)
            ? name : "rail-narrator";

    public static string Usage =>
        $"usage: {CommandName} narrate <document.pdf> [--out FILE.narration] [--voice ID] [--m4b [FILE.m4b]] [--force] [--allow-uninterpreted]\n" +
        $"       {CommandName} export  <document.pdf> [--m4b FILE.m4b]\n" +
        "narrate: interprets the maths, speaks every passage and times its words, and saves the result beside\n" +
        "         the document, where Rail Narrator plays it with no language model and no speech engine.\n" +
        "         It stops if the maths model cannot be asked, unless --allow-uninterpreted is given.\n" +
        "export:  writes a saved narration as an M4B audiobook with a chapter at each heading (needs ffmpeg).";

    public static bool IsCommand(string[] args) => args.Length > 0 && args[0] is "narrate" or "export";

    public static async Task<int> RunAsync(string[] args, TextWriter output, TextWriter errors, CancellationToken ct = default)
    {
        string command = args[0];
        if (!TryParse(args, out var parsed, out string? parseError))
        {
            errors.WriteLine(parseError is null ? Usage : parseError + "\n" + Usage);
            return 2;
        }
        string? Option(string name) => parsed.Values.GetValueOrDefault(name);
        bool Flag(string name) => parsed.Flags.Contains(name);
        if (parsed.Document is null || Flag("--help")) { errors.WriteLine(Usage); return 2; }
        string document = Path.GetFullPath(parsed.Document);
        if (!File.Exists(document)) { errors.WriteLine($"No such file: {document}"); return 2; }

        string narrationPath = Option("--out") is { } o ? Path.GetFullPath(o) : SavedNarration.PathFor(document);
        bool wantBook = Flag("--m4b") || command == "export";
        string bookPath = Option("--m4b") is { } b ? Path.GetFullPath(b) : Path.ChangeExtension(document, ".m4b");
        foreach (var (what, path) in new[] { ("--out", narrationPath), ("--m4b", bookPath) })
            if (string.Equals(path, document, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal))
            {
                errors.WriteLine($"{what} would write over the document itself ({document}).");
                return 2;
            }
        string title = Path.GetFileNameWithoutExtension(document);

        try
        {
            string hash = CacheKey.Sha256HexOfFile(document);
            if (command == "narrate")
            {
                if (File.Exists(narrationPath) && !Flag("--force"))
                {
                    errors.WriteLine($"{narrationPath} already exists. Pass --force to narrate the document again and replace it.");
                    return 1;
                }
                await using var services = ServiceRegistry.Create();
                foreach (string note in services.StartupNotes) errors.WriteLine("note: " + note);
                var problems = new HashSet<string>(StringComparer.Ordinal);
                if (services.MathResolver is { } maths)
                    maths.EndpointProblem += reason => { lock (problems) if (problems.Add(reason)) errors.WriteLine("maths is not being interpreted: " + reason); };

                output.WriteLine($"Reading {Path.GetFileName(document)}");
                var plan = await BuildPlanAsync(services, document, ct).ConfigureAwait(false);
                output.WriteLine($"{plan.SegmentCount} passages, {plan.Segments.Count(s => s.Provenance == ScriptProvenance.PendingInterpretation)} of them maths to interpret");

                var clock = System.Diagnostics.Stopwatch.StartNew();
                long lastReport = -10_000;
                NarrationProgress? last = null;
                var manifest = await NarrationJob.RunAsync(plan, Path.GetFileName(document), narrationPath, services.NarrationEngines(Option("--voice")),
                    new Immediate<NarrationProgress>(p =>
                    {
                        last = p;
                        if (p.Done != p.Total && clock.ElapsedMilliseconds - lastReport < 5000) return;
                        lastReport = clock.ElapsedMilliseconds;
                        output.WriteLine(string.Create(CultureInfo.InvariantCulture,
                            $"  {p.Done,5} of {p.Total}  {Minutes(p.AudioSeconds)} of audio  {Minutes(clock.Elapsed.TotalSeconds)} elapsed"));
                    }), ct, allowUninterpreted: Flag("--allow-uninterpreted")).ConfigureAwait(false);

                output.WriteLine(Summary(manifest, narrationPath));
                if (last is not null)
                    output.WriteLine(string.Create(CultureInfo.InvariantCulture,
                        $"  time: {clock.Elapsed.TotalSeconds:F0} s in all; interpreting and speaking {last.PrepareSeconds:F0} s, waiting for the aligner {last.AlignSeconds:F0} s, encoding and writing {last.SinkSeconds:F0} s"));
            }

            if (wantBook)
            {
                using var saved = SavedNarration.TryOpen(narrationPath, hash, out string? problem);
                if (saved is null)
                {
                    errors.WriteLine(problem is null
                        ? $"There is no saved narration at {narrationPath}. Run '{CommandName} narrate' on the document first."
                        : $"The saved narration at {narrationPath} cannot be used: {problem}.");
                    return 1;
                }
                var chapters = await AudiobookExporter.ExportAsync(saved, title, null, bookPath, ct: ct).ConfigureAwait(false);
                output.WriteLine(string.Create(CultureInfo.InvariantCulture,
                    $"Audiobook: {bookPath} ({Megabytes(bookPath)}, {Minutes(saved.Manifest.Seconds)}, {chapters.Count} chapter{(chapters.Count == 1 ? "" : "s")})"));
            }
            return 0;
        }
        catch (OperationCanceledException)
        {
            errors.WriteLine("Stopped. Nothing was written; what was spoken so far is in the cache and will not be spoken again.");
            return 130;
        }
        catch (Exception ex) when (ex is InvalidOperationException or IOException or UnauthorizedAccessException)
        {
            errors.WriteLine(ex.Message);
            return 1;
        }
    }

    /// <summary>Extraction, reading units and plan for a document, as opening it in the window does.</summary>
    public static async Task<NarrationPlan> BuildPlanAsync(ServiceRegistry services, string path, CancellationToken ct)
    {
        services.Extraction.AttachDocument(path);
        var snapshot = await services.CachedExtraction.ExtractAsync(services.CreateExtractionRequest(path), null, ct).ConfigureAwait(false);
        if (snapshot.Pages.All(p => p.Coverage.CharCount == 0))
            throw new InvalidOperationException("This document has no readable text.");
        var units = services.CreateUnitBuilder([]).Build(snapshot, out _);
        if (units.Count == 0) throw new InvalidOperationException("Nothing narratable was found in the document.");
        return await services.CreatePlanner().BuildPlanAsync(snapshot, units, services.CurrentSettings.Filters, ct: ct).ConfigureAwait(false);
    }

    /// <summary>The arguments after the command, read apart: the document, options with a value, flags.</summary>
    public sealed record Arguments(string? Document, IReadOnlyDictionary<string, string> Values, IReadOnlySet<string> Flags);

    /// <summary>
    /// Reads the arguments after the command. <c>--out</c> and <c>--voice</c> always take the next
    /// argument; <c>--m4b</c> takes it only when it names an <c>.m4b</c> file, so that
    /// <c>--m4b paper.pdf</c> means "also an audiobook" and never "write the audiobook over the PDF".
    /// An option's value is never taken as the document, and an unknown option or a second document
    /// is an error rather than being ignored.
    /// </summary>
    public static bool TryParse(string[] args, out Arguments parsed, out string? error)
    {
        var values = new Dictionary<string, string>(StringComparer.Ordinal);
        var flags = new HashSet<string>(StringComparer.Ordinal);
        string? document = null;
        error = null;
        parsed = new Arguments(null, values, flags);
        for (int i = 1; i < args.Length; i++)
        {
            string a = args[i];
            bool hasNext = i + 1 < args.Length && !args[i + 1].StartsWith("--", StringComparison.Ordinal);
            switch (a)
            {
                case "--out":
                    if (!hasNext) { error = "--out needs a file name."; return false; }
                    if (!args[i + 1].EndsWith(".narration", StringComparison.OrdinalIgnoreCase)) { error = $"--out must name a .narration file, not {args[i + 1]}."; return false; }
                    values[a] = args[++i];
                    break;
                case "--voice":
                    if (!hasNext) { error = "--voice needs a voice id."; return false; }
                    values[a] = args[++i];
                    break;
                case "--m4b":
                    flags.Add(a);
                    if (hasNext && args[i + 1].EndsWith(".m4b", StringComparison.OrdinalIgnoreCase)) values[a] = args[++i];
                    break;
                case "--force" or "--allow-uninterpreted" or "--help":
                    flags.Add(a);
                    break;
                default:
                    if (a.StartsWith("--", StringComparison.Ordinal)) { error = $"Unknown option {a}."; return false; }
                    if (document is not null) { error = $"One document at a time: {document} and {a} were both given."; return false; }
                    document = a;
                    break;
            }
        }
        parsed = new Arguments(document, values, flags);
        return true;
    }

    /// <summary>What was saved, with what a listener should know about its maths.</summary>
    public static string Summary(SavedNarrationManifest manifest, string path)
    {
        var lines = new List<string>
        {
            string.Create(CultureInfo.InvariantCulture,
                $"Saved: {path} ({Megabytes(path)}, {manifest.Passages} passages, {Minutes(manifest.Seconds)}, voice {manifest.VoiceId} on {manifest.ProviderId})"),
            $"  maths interpreted by the model: {manifest.Interpreted}; read as notation by rule: {manifest.ByRule}; read as printed: {manifest.AsPrinted}; announced as unreadable: {manifest.Unread}",
            $"  passages with word timing: {manifest.WordTimed} of {manifest.Passages}",
        };
        return string.Join(Environment.NewLine, lines);
    }

    public static string Minutes(double seconds)
        => seconds < 90 ? string.Create(CultureInfo.InvariantCulture, $"{seconds:F0} s") : string.Create(CultureInfo.InvariantCulture, $"{seconds / 60:F0} min");

    public static string Megabytes(string path)
        => File.Exists(path) ? string.Create(CultureInfo.InvariantCulture, $"{new FileInfo(path).Length / 1_048_576.0:F1} MB") : "no file";

    private sealed class Immediate<T>(Action<T> report) : IProgress<T>
    {
        public void Report(T value) => report(value);
    }
}
