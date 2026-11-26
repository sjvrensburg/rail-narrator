using System.Diagnostics;
using System.Globalization;
using RailNarrator.Domain;
using RailNarrator.Inference;
using RailNarrator.Pdf;
using RailReader.Core.Models;

// Measures maths interpretation on a real document, with no cache, so every number is a model call.
// Each request to an OpenAI-compatible endpoint is printed (REQ: interpret, repair or transcribe,
// thinking or not, its time, tokens and finish) and totalled at the end by kind of passage and outcome.
// usage: rail-narrator-mathbench <pdf> <layout.onnx> [options]
//   --layout NAME        heron | pp-doclayout-v3 (default: heron when the model's file name says so, else pp-doclayout-v3)
//   --dump-crops DIR     write each crop the formula model is given as a PNG, named by passage
//   --provider NAME      openai (default: any OpenAI-compatible endpoint) | claude (Anthropic, the SDK's own credentials)
//   --endpoint URL       base URL (default http://127.0.0.1:8088/v1; with claude, only for a gateway)
//   --api-key-env NAME   the environment variable holding the endpoint's key (never printed)
//   --model NAME         model id (default ornith-1.5-35b-a3b; with claude, claude-opus-5-5)
//   --pages A-B          zero-based page range (default all)
//   --only i,j,k         only these indices among the pending passages (as printed by an earlier run)
//   --thinking MODE      off | equations | always (default equations, the app's default)
//   --max-tokens N       completion budget (default 4000; with claude, 16000)
//   --no-images          text layer and context only
//   --no-reader          no transcription fallback when an interpretation is refused
//   --repair-no-thinking the repair turn with thinking off even when the first attempt thought (the application lets it think)
//   --formula-model PATH the formula model (PP-FormulaNet_plus-M .onnx); its inference.yml is looked for beside it
//   --formula-config PATH its inference.yml, when it is elsewhere
//   --evidence WHAT      what the language model is given with a formula model: image | latex | both (default both)
//   --no-llm             no language model: display equations read by rule from the formula model
if (args.Length < 2)
{
    Console.Error.WriteLine("usage: rail-narrator-mathbench <pdf> <layout.onnx> [--layout heron|pp-doclayout-v3] [--provider openai|claude] [--endpoint URL] [--api-key-env NAME] [--model NAME] [--pages A-B] [--only i,j] [--thinking off|equations|always] [--max-tokens N] [--no-images] [--no-reader] [--repair-no-thinking] [--formula-model PATH [--formula-config PATH] [--evidence image|latex|both] [--no-llm] [--dump-crops DIR]]");
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

// Heron is what the application picks when installed; it has no class for an equation number.
string layout = Option("--layout") ?? (Path.GetFileName(args[1]).Contains("heron", StringComparison.OrdinalIgnoreCase) ? "heron" : "pp-doclayout-v3");
var architecture = layout == "heron" ? LayoutModelArchitecture.Heron : LayoutModelArchitecture.PPDocLayoutV3;
using var source = new RailReaderExtractionSource();
source.WithBackend(ExtractionBackend.OnnxLayout(new LayoutModelFiles(architecture, layout, args[1], args[1]), null, LayoutAcceleration.Cpu));
source.AttachDocument(pdf);
var snapshot = await source.ExtractAsync(new ExtractionRequest(pdf, null, null,
    new ExtractionOptions(layout, OcrPolicy.Off, 1024, false, ExtractorVersions.Current), NarrationFilterSettings.Default));
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
    formulas = new TimedFormulas(new PpFormulaNetRecogniser(files), Option("--dump-crops"));
}
var evidence = (Option("--evidence") ?? "both") switch
{
    "image" => MathEvidence.Image,
    "latex" or "formula" => MathEvidence.Formula,
    _ => MathEvidence.Both,
};
bool noLlm = args.Contains("--no-llm");
if (noLlm && formulas is null) { Console.Error.WriteLine("--no-llm needs --formula-model"); return 2; }

// Every request to an OpenAI-compatible endpoint, timed, so a passage's time can be split by call.
var trace = new TracingHandler();
IMathInterpreter? interpreter = noLlm ? null : claude
    ? new ClaudeMathInterpreter(new ClaudeEndpointSettings(model, BaseUrl: endpoint, MaxTokens: maxTokens), thinking: thinking)
    : new OpenAiCompatibleMathInterpreter(
        new MathEndpointSettings(endpoint!, model!, ApiKey: Option("--api-key-env") is { } keyVariable ? Environment.GetEnvironmentVariable(keyVariable) : null, Temperature: 0.2, MaxTokens: maxTokens, MaxRetries: 1, RequestTimeout: TimeSpan.FromMinutes(3)),
        new HttpClient(trace) { Timeout = TimeSpan.FromMinutes(3) },
        thinking: thinking) { ThinkOnRepair = !args.Contains("--repair-no-thinking") };
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
Console.WriteLine($"layout {layout}: {plan.Segments.Count} segments, {allPending.Count} pending; interpreting {pending.Count} (thinking {thinking}" +
    (formulas is null ? ")" : $"; formula model, {(noLlm ? "no language model" : "evidence " + evidence)})"));

var times = new List<double>();
var outcomes = new Dictionary<ScriptProvenance, int>();
foreach (var (segment, index) in pending)
{
    var unit = plan.Units.First(u => u.Id == segment.ReadingUnitIds[0]);
    formulas?.Read.Clear();
    trace.Calls.Clear();
    formulas?.Passage = index;
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
    foreach (var call in trace.Calls)
        Console.WriteLine(string.Create(CultureInfo.InvariantCulture,
            $"  REQ {call.Kind}{(call.Thinking ? " thinking" : "")}: {call.Seconds:F1}s, {call.CompletionTokens} tokens, {call.Finish ?? "error " + call.Status}"));
    trace.All.AddRange(trace.Calls.Select(c => (Call: c, result.Provenance, Index: index, Passage: segment.ReadingUnitIds.Count > 1 ? "lead-in" : unit.Kind == ReadingUnitKind.DisplayMath ? "equation" : "sentence")));
    foreach (var warning in result.Warnings.Where(w => !w.StartsWith("Interpreted by", StringComparison.Ordinal)))
        Console.WriteLine($"  !! {warning}");
}

if (times.Count == 0) return 0;
var sorted = times.Order().ToList();
Console.WriteLine(string.Create(CultureInfo.InvariantCulture,
    $"\n{string.Join(", ", outcomes.Select(o => $"{o.Key} {o.Value}"))}; median {sorted[sorted.Count / 2]:F1}s, max {sorted[^1]:F1}s, total {times.Sum():F0}s"));
foreach (var group in trace.All.GroupBy(c => (Kind: c.Passage, c.Provenance)).OrderBy(g => g.Key.Kind).ThenBy(g => g.Key.Provenance.ToString()))
{
    var calls = group.Select(g => g.Call).ToList();
    int passages = trace.All.Where(c => c.Passage == group.Key.Kind && c.Provenance == group.Key.Provenance).Select(c => c.Index).Distinct().Count();
    Console.WriteLine(FormattableString.Invariant(
        $"{group.Key.Kind,-8} {passages} {group.Key.Provenance}: {calls.Count} requests, {calls.Sum(c => c.Seconds):F0}s; ") +
        string.Join(", ", calls.GroupBy(c => (c.Kind, c.Thinking, c.Finish)).Select(k => FormattableString.Invariant($"{k.Key.Kind}{(k.Key.Thinking ? "+think" : "")}/{k.Key.Finish ?? "error"} {k.Count()} in {k.Sum(c => c.Seconds):F0}s"))));
}
if (formulas is { All.Count: > 0 })
{
    var formulaTimes = formulas.All.Select(f => f.Seconds).Order().ToList();
    Console.WriteLine(string.Create(CultureInfo.InvariantCulture,
        $"formula model: {formulas.All.Count} equations, {formulas.All.Count(f => !f.Read.Succeeded)} unread; median {formulaTimes[formulaTimes.Count / 2]:F2}s, max {formulaTimes[^1]:F2}s, total {formulaTimes.Sum():F0}s"));
}
return 0;

/// <summary>A formula model that keeps what it read and how long each equation took.</summary>
internal sealed class TimedFormulas(PpFormulaNetRecogniser inner, string? dumpDirectory = null) : IFormulaRecogniser, IDisposable
{
    /// <summary>The passage being resolved, for the names of dumped crops.</summary>
    public int Passage { get; set; }

    public List<(FormulaRecognition Read, double Seconds)> Read { get; } = [];
    public List<(FormulaRecognition Read, double Seconds)> All { get; } = [];

    public string Revision => inner.Revision;

    public async Task<FormulaRecognition> RecogniseAsync(ReadOnlyMemory<byte> png, CancellationToken ct = default)
    {
        if (dumpDirectory is not null)
        {
            Directory.CreateDirectory(dumpDirectory);
            await File.WriteAllBytesAsync(Path.Combine(dumpDirectory, $"p{Passage:D3}-{All.Count:D3}.png"), png, ct);
        }
        var clock = Stopwatch.StartNew();
        var read = await inner.RecogniseAsync(png, ct);
        Read.Add((read, clock.Elapsed.TotalSeconds));
        All.Add((read, clock.Elapsed.TotalSeconds));
        return read;
    }

    public void Dispose() => inner.Dispose();
}

/// <summary>Times each chat request and notes what it was: an interpretation, a repair or a transcription, thinking or not.</summary>
internal sealed class TracingHandler() : DelegatingHandler(new HttpClientHandler())
{
    public sealed record Call(string Kind, bool Thinking, double Seconds, int CompletionTokens, string? Finish, int Status);

    public List<Call> Calls { get; } = [];
    public List<(Call Call, ScriptProvenance Provenance, int Index, string Passage)> All { get; } = [];

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        if (request.Content is null || !request.RequestUri!.AbsolutePath.EndsWith("chat/completions", StringComparison.Ordinal))
            return await base.SendAsync(request, ct);
        string body = await request.Content.ReadAsStringAsync(ct);
        var sent = System.Text.Json.Nodes.JsonNode.Parse(body)!;
        var messages = sent["messages"]!.AsArray();
        string system = messages[0]!["content"]!.GetValue<string>();
        string kind = system.StartsWith("You transcribe", StringComparison.Ordinal) ? "transcribe" : messages.Count > 2 ? "repair" : "interpret";
        bool thinking = sent["chat_template_kwargs"]?["enable_thinking"]?.GetValue<bool>() != false;
        var clock = Stopwatch.StartNew();
        var response = await base.SendAsync(request, ct);
        string text = await response.Content.ReadAsStringAsync(ct);
        int tokens = 0; string? finish = null;
        if (response.IsSuccessStatusCode)
        {
            var answer = System.Text.Json.Nodes.JsonNode.Parse(text)!;
            tokens = answer["usage"]?["completion_tokens"]?.GetValue<int>() ?? 0;
            finish = answer["choices"]?[0]?["finish_reason"]?.GetValue<string>();
        }
        Calls.Add(new Call(kind, thinking, clock.Elapsed.TotalSeconds, tokens, finish, (int)response.StatusCode));
        var copy = new HttpResponseMessage(response.StatusCode) { Content = new StringContent(text, System.Text.Encoding.UTF8, "application/json") };
        response.Dispose();
        return copy;
    }
}
