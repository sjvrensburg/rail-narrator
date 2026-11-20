using System.Diagnostics;
using System.Globalization;
using RailNarrator.Domain;
using RailNarrator.Inference;
using RailNarrator.Pdf;

// Measures maths interpretation on a real document, with no cache, so every number is a model call.
// usage: rail-narrator-mathbench <pdf> <layout.onnx> [options]
//   --provider NAME      openai (default: any OpenAI-compatible endpoint) | claude (Anthropic, the SDK's own credentials)
//   --endpoint URL       base URL (default http://127.0.0.1:8088/v1; with claude, only for a gateway)
//   --model NAME         model id (default ornith-1.5-35b-a3b; with claude, claude-opus-5-5)
//   --pages A-B          zero-based page range (default all)
//   --only i,j,k         only these indices among the pending passages (as printed by an earlier run)
//   --thinking MODE      off | equations | always (default equations, the app's default)
//   --max-tokens N       completion budget (default 4000; with claude, 16000)
//   --no-images          text layer and context only
//   --no-reader          no transcription fallback when an interpretation is refused
//   --formula-model PATH the formula model (PP-FormulaNet_plus-M .onnx); its inference.yml is looked for beside it
//   --formula-config PATH its inference.yml, when it is elsewhere
//   --evidence WHAT      what the language model is given with a formula model: image | latex | both (default both)
//   --no-llm             no language model: display equations read by rule from the formula model
if (args.Length < 2)
{
    Console.Error.WriteLine("usage: rail-narrator-mathbench <pdf> <layout.onnx> [--provider openai|claude] [--endpoint URL] [--model NAME] [--pages A-B] [--only i,j] [--thinking off|equations|always] [--max-tokens N] [--no-images] [--no-reader] [--formula-model PATH [--formula-config PATH] [--evidence image|latex|both] [--no-llm]]");
    return 2;
}

string? Option(string name) => args.SkipWhile(a => a != name).Skip(1).FirstOrDefault();
string pdf = args[0];
// --provider claude: Anthropic's API with the SDK's own credentials (ANTHROPIC_API_KEY, or an
// `ant auth login` profile); --endpoint is then only for a gateway and --model defaults to Claude Opus.
bool claude = Option("--provider") is "claude" or "anthropic";
string? endpoint = Option("--endpoint") ?? (claude ? null : "http://127.0.0.1:8088/v1");
string? model = Option("--model") ?? (claude ? null : "ornith-1.5-35b-a3b");
int maxTokens = int.Parse(Option("--max-tokens") ?? (claude ? "16000" : "4000"), CultureInfo.InvariantCulture);
var thinking = (Option("--thinking") ?? "equations") switch
{
    "off" => MathThinking.Off,
    "always" => MathThinking.Always,
    _ => MathThinking.EquationsOnly,
};
var pages = (Option("--pages") ?? "0-100000").Split('-').Select(p => int.Parse(p, CultureInfo.InvariantCulture)).ToArray();
var only = (Option("--only") ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries).Select(i => int.Parse(i, CultureInfo.InvariantCulture)).ToHashSet();

using var source = new RailReaderExtractionSource();
source.WithBackend(ExtractionBackend.OnnxLayout(args[1]));
source.AttachDocument(pdf);
var snapshot = await source.ExtractAsync(new ExtractionRequest(pdf, null, null,
    new ExtractionOptions("pp-doclayout-v3", OcrPolicy.Off, 1024, false, ExtractorVersions.Current), NarrationFilterSettings.Default));
var units = new ReadingUnitBuilder().Build(snapshot);
var options = new NarrationPlannerOptions(MathMode: MathMode.Interpret, InterpretMaths: true);
var plan = await new NarrationPlanner(options: options).BuildPlanAsync(snapshot, units, NarrationFilterSettings.Default);

// The formula model, wrapped so each transcription and its time can be printed.
TimedFormulas? formulas = null;
if (Option("--formula-model") is { } formulaModel)
{
    string config = Option("--formula-config")
        ?? Path.Combine(Path.GetDirectoryName(Path.GetFullPath(formulaModel))!, Path.GetFileNameWithoutExtension(formulaModel) + "_inference.yml");
    var files = new FormulaModelFiles(formulaModel, config);
    if (!files.Exist) { Console.Error.WriteLine($"formula model not found: '{files.ModelPath}' with '{files.ConfigPath}'"); return 2; }
    formulas = new TimedFormulas(new PpFormulaNetRecogniser(files));
}
var evidence = (Option("--evidence") ?? "both") switch
{
    "image" => MathEvidence.Image,
    "latex" or "formula" => MathEvidence.Formula,
    _ => MathEvidence.Both,
};
bool noLlm = args.Contains("--no-llm");
if (noLlm && formulas is null) { Console.Error.WriteLine("--no-llm needs --formula-model"); return 2; }

IMathInterpreter? interpreter = noLlm ? null : claude
    ? new ClaudeMathInterpreter(new ClaudeEndpointSettings(model, BaseUrl: endpoint, MaxTokens: maxTokens), thinking: thinking)
    : new OpenAiCompatibleMathInterpreter(
        new MathEndpointSettings(endpoint!, model!, Temperature: 0.2, MaxTokens: maxTokens, MaxRetries: 1, RequestTimeout: TimeSpan.FromMinutes(3)),
        thinking: thinking);
using var disposeInterpreter = interpreter as IDisposable;
using var disposeFormulas = formulas;
// With a formula model the crops are needed even without images: it reads its own.
var resolver = new MathInterpretationResolver(interpreter, args.Contains("--no-images") && formulas is null ? null : source, null, options,
    reader: args.Contains("--no-reader") ? null : interpreter as IMathReader,
    formulas: formulas, formulaReader: formulas is null ? null : new FormulaMathReader(),
    evidence: args.Contains("--no-images") ? MathEvidence.Formula : evidence);

var allPending = plan.Segments.Where(s => s.Provenance == ScriptProvenance.PendingInterpretation).ToList();
var pending = allPending
    .Where(s => plan.Units.First(u => u.Id == s.ReadingUnitIds[0]).PageIndex is var p && p >= pages[0] && p <= pages[^1])
    .Select((s, i) => (Segment: s, Index: i))
    .Where(x => only.Count == 0 || only.Contains(x.Index))
    .ToList();
Console.WriteLine($"{plan.Segments.Count} segments, {allPending.Count} pending; interpreting {pending.Count} (thinking {thinking}" +
    (formulas is null ? ")" : $"; formula model, {(noLlm ? "no language model" : "evidence " + evidence)})"));

var times = new List<double>();
var outcomes = new Dictionary<ScriptProvenance, int>();
foreach (var (segment, index) in pending)
{
    var unit = plan.Units.First(u => u.Id == segment.ReadingUnitIds[0]);
    formulas?.Read.Clear();
    var clock = Stopwatch.StartNew();
    var result = await resolver.ResolveAsync(plan, segment);
    times.Add(clock.Elapsed.TotalSeconds);
    outcomes[result.Provenance] = outcomes.GetValueOrDefault(result.Provenance) + 1;
    string kind = (segment.ReadingUnitIds.Count > 1 ? "LEAD-IN+EQ " : "") + unit.Kind + (unit.ContainsMaths ? "+m" : "");
    string printed = string.Join(" ⏎ ", segment.ReadingUnitIds.Select(id => plan.Units.First(u => u.Id == id).OriginalText));
    Console.WriteLine(string.Create(CultureInfo.InvariantCulture,
        $"\n#{index} [{kind} p{unit.PageIndex + 1}, {clock.Elapsed.TotalSeconds:F1}s, {result.Provenance}]\n  SRC: {printed}\n  SAY: {result.SpokenText}"));
    foreach (var (read, seconds) in formulas?.Read ?? [])
        Console.WriteLine(string.Create(CultureInfo.InvariantCulture, $"  TEX ({seconds:F2}s): {read.Latex ?? "— " + read.Problem}"));
    foreach (var warning in result.Warnings.Where(w => !w.StartsWith("Interpreted by", StringComparison.Ordinal)))
        Console.WriteLine($"  !! {warning}");
}

if (times.Count == 0) return 0;
var sorted = times.Order().ToList();
Console.WriteLine(string.Create(CultureInfo.InvariantCulture,
    $"\n{string.Join(", ", outcomes.Select(o => $"{o.Key} {o.Value}"))}; median {sorted[sorted.Count / 2]:F1}s, max {sorted[^1]:F1}s, total {times.Sum():F0}s"));
if (formulas is { All.Count: > 0 })
{
    var formulaTimes = formulas.All.Select(f => f.Seconds).Order().ToList();
    Console.WriteLine(string.Create(CultureInfo.InvariantCulture,
        $"formula model: {formulas.All.Count} equations, {formulas.All.Count(f => !f.Read.Succeeded)} unread; median {formulaTimes[formulaTimes.Count / 2]:F2}s, max {formulaTimes[^1]:F2}s, total {formulaTimes.Sum():F0}s"));
}
return 0;

/// <summary>A formula model that keeps what it read and how long each equation took.</summary>
internal sealed class TimedFormulas(PpFormulaNetRecogniser inner) : IFormulaRecogniser, IDisposable
{
    public List<(FormulaRecognition Read, double Seconds)> Read { get; } = [];
    public List<(FormulaRecognition Read, double Seconds)> All { get; } = [];

    public string Revision => inner.Revision;

    public async Task<FormulaRecognition> RecogniseAsync(ReadOnlyMemory<byte> png, CancellationToken ct = default)
    {
        var clock = Stopwatch.StartNew();
        var read = await inner.RecogniseAsync(png, ct);
        Read.Add((read, clock.Elapsed.TotalSeconds));
        All.Add((read, clock.Elapsed.TotalSeconds));
        return read;
    }

    public void Dispose() => inner.Dispose();
}
