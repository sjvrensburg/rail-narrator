using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using RailReader.Core.Models;
using RailReader.Core.Services;
using RailNarrator.Domain;

namespace RailNarrator.Inference;

/// <summary>Configuration for an OpenAI-compatible text endpoint.</summary>
/// <param name="BaseUrl">Endpoint root, e.g. <c>http://127.0.0.1:11434/v1</c>. Local by default; a remote host is opt-in.</param>
/// <param name="Model">Model name.</param>
/// <param name="ApiKey">Optional. Held in memory only and never written to a log.</param>
/// <param name="Temperature">Sampling temperature. Low, because faithfulness matters more than variety.</param>
/// <param name="MaxTokens">Cap on the response.</param>
/// <param name="MaxRetries">Bounded retries for malformed output. Unbounded retrying hides a model that cannot do the job.</param>
/// <param name="RequestTimeout">Per-request timeout.</param>
/// <param name="ContextCharLimit">Bound on how much surrounding document text is sent. Context is a hint, not the document.</param>
/// <param name="UseStructuredOutput">Request <c>response_format</c> json_schema when the endpoint supports it.</param>
/// <param name="DisableThinking">
/// Ask a reasoning model not to think first (<c>chat_template_kwargs.enable_thinking = false</c>, understood
/// by llama.cpp and vLLM). Measured on Ornith-35B: 1.3 s instead of 5.4 s per equation for a comparable
/// reading. An endpoint that rejects the field is asked again without it.
/// </param>
public sealed record MathEndpointSettings(
    string BaseUrl,
    string Model,
    string? ApiKey = null,
    double Temperature = 0.1,
    int MaxTokens = 700,
    int MaxRetries = 2,
    TimeSpan? RequestTimeout = null,
    int ContextCharLimit = 400,
    bool UseStructuredOutput = true,
    bool DisableThinking = false)
{
    public TimeSpan Timeout => RequestTimeout ?? TimeSpan.FromSeconds(120);
}

/// <summary>
/// Verbalises recognised mathematics through an OpenAI-compatible chat endpoint.
/// </summary>
/// <remarks>
/// <para>
/// Speaks plain HTTP rather than an SDK so any OpenAI-compatible server works — Ollama, vLLM,
/// llama.cpp, LM Studio or a hosted endpoint — and so the request and response are fully inspectable
/// in tests. Nothing here uploads a document unless the user has configured a remote base URL, and the
/// API key is sent as a bearer header and never logged.
/// </para>
/// <para>
/// Every response goes through <see cref="MathResponseValidator"/>. Malformed output is retried a
/// bounded number of times with the validator's complaint fed back, and then rejected: a recoverable
/// error in the UI is better than arbitrary JSON becoming speech.
/// </para>
/// </remarks>
public sealed class OpenAiCompatibleMathVerbaliser : IMathVerbaliser, IDisposable
{
    private static readonly JsonSerializerOptions JsonOptions = new() { DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull };

    private readonly MathEndpointSettings _settings;
    private readonly HttpClient _http;
    private readonly bool _ownsClient;
    private bool? _available;

    public OpenAiCompatibleMathVerbaliser(MathEndpointSettings settings, HttpClient? httpClient = null)
    {
        _settings = settings ?? throw new ArgumentNullException(nameof(settings));
        if (httpClient is not null) { _http = httpClient; _ownsClient = false; }
        else
        {
            _http = new HttpClient { Timeout = settings.Timeout };
            _ownsClient = true;
        }
        if (_http.BaseAddress is null) _http.BaseAddress = NormaliseBaseUrl(settings.BaseUrl);
        if (!string.IsNullOrEmpty(settings.ApiKey))
            _http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", settings.ApiKey);
    }

    private static Uri NormaliseBaseUrl(string url)
        => new(url.EndsWith('/') ? url : url + "/");

    public string ModelRevision => $"{Path.GetFileName(_settings.BaseUrl.TrimEnd('/'))}/{_settings.Model}";

    public async Task<bool> IsAvailableAsync(CancellationToken ct = default)
    {
        if (_available is { } cached) return cached;
        if (string.IsNullOrWhiteSpace(_settings.BaseUrl) || string.IsNullOrWhiteSpace(_settings.Model))
            return (bool)(_available = false);
        try
        {
            // A cheap probe. /models is part of the OpenAI-compatible contract and needs no tokens.
            using var response = await _http.GetAsync("models", ct).ConfigureAwait(false);
            return (bool)(_available = response.IsSuccessStatusCode);
        }
        catch (OperationCanceledException) { throw; }
        catch { return (bool)(_available = false); }
    }

    public async Task<MathVerbalisationResult> VerbaliseAsync(MathVerbalisationRequest request, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var mode = request.Mode switch
        {
            MathMode.DetailedSymbolic => MathModeRequest.DetailedSymbolic,
            MathMode.Explain => MathModeRequest.Explain,
            _ => MathModeRequest.NaturalFaithful,
        };

        string userMessage = MathPrompt.BuildUserMessage(
            request.Representation, mode, request.ContextBefore, request.ContextAfter,
            request.AnchorIds, _settings.ContextCharLimit);

        var messages = new List<ChatMessage>
        {
            new() { Role = "system", Content = MathPrompt.SystemInstruction },
            new() { Role = "user", Content = userMessage },
        };

        var allowed = request.AnchorIds.ToHashSet(StringComparer.Ordinal);
        var allProblems = new List<string>();
        int attempts = 0;

        for (int attempt = 0; attempt <= _settings.MaxRetries; attempt++)
        {
            attempts++;
            ct.ThrowIfCancellationRequested();

            string? content;
            try
            {
                content = await ChatAsync(messages, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                allProblems.Add($"request failed: {ex.Message}");
                // A transport failure may be transient, so it is retried within the same bound.
                continue;
            }

            var result = MathResponseValidator.Validate(content, allowed, mode);
            if (result.IsValid)
            {
                return new MathVerbalisationResult(
                    result.Payload!.Faithful, result.Payload.Explanation, result.Payload.SourceIds,
                    [.. result.Payload.Warnings, .. result.Warnings], Rejected: false, [],
                    MathPrompt.Version, ModelRevision, attempts);
            }

            allProblems.AddRange(result.Problems);
            if (attempt == _settings.MaxRetries) break;

            // Feed the complaint back once per retry. Telling the model what was wrong is more likely
            // to fix it than resending the same request and hoping.
            messages.Add(new ChatMessage { Role = "assistant", Content = content ?? "" });
            messages.Add(new ChatMessage
            {
                Role = "user",
                Content = "That response was rejected. Fix exactly these problems and return only JSON " +
                          $"matching the \"{MathPrompt.ResponseSchemaName}\" schema:\n- " +
                          string.Join("\n- ", result.Problems),
            });
        }

        return MathVerbalisationResult.Reject(allProblems, MathPrompt.Version, ModelRevision, attempts);
    }

    private async Task<string?> ChatAsync(List<ChatMessage> messages, CancellationToken ct)
    {
        var body = new ChatRequest
        {
            Model = _settings.Model,
            Messages = messages,
            Temperature = _settings.Temperature,
            MaxTokens = _settings.MaxTokens,
            ResponseFormat = _settings.UseStructuredOutput
                ? new ResponseFormat { Type = "json_schema", JsonSchema = new JsonSchemaSpec
                    { Name = MathPrompt.ResponseSchemaName, Strict = true, Schema = JsonDocument.Parse(MathPrompt.ResponseSchemaJson) } }
                : new ResponseFormat { Type = "json_object" },
        };

        using var response = await _http.PostAsJsonAsync("chat/completions", body, JsonOptions, ct).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            string detail = await SafeReadAsync(response, ct).ConfigureAwait(false);
            throw new InvalidOperationException($"HTTP {(int)response.StatusCode}: {Truncate(detail)}");
        }

        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false));
        var choices = document.RootElement.GetProperty("choices");
        if (choices.GetArrayLength() == 0) throw new InvalidOperationException("The endpoint returned no choices.");
        var message = choices[0].GetProperty("message");

        // Some endpoints answer a refusal in a sibling field with no content; surface it rather than
        // treating an empty string as a malformed response.
        if (message.TryGetProperty("refusal", out var refusal) && refusal.ValueKind == JsonValueKind.String
            && !string.IsNullOrWhiteSpace(refusal.GetString()))
            throw new InvalidOperationException($"The endpoint refused the request: {refusal.GetString()}");

        return message.TryGetProperty("content", out var content) && content.ValueKind == JsonValueKind.String
            ? content.GetString()
            : null;
    }

    private static async Task<string> SafeReadAsync(HttpResponseMessage response, CancellationToken ct)
    {
        try { return await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false); }
        catch { return "(no body)"; }
    }

    private static string Truncate(string s, int max = 300) => s.Length <= max ? s : s[..max] + "…";

    public void Dispose()
    {
        if (_ownsClient) _http.Dispose();
    }

    private sealed class ChatMessage
    {
        [JsonPropertyName("role")] public string Role { get; init; } = "";
        [JsonPropertyName("content")] public string Content { get; init; } = "";
    }

    private sealed class ResponseFormat
    {
        [JsonPropertyName("type")] public string Type { get; init; } = "json_object";
        [JsonPropertyName("json_schema")] public JsonSchemaSpec? JsonSchema { get; init; }
    }

    private sealed class JsonSchemaSpec
    {
        [JsonPropertyName("name")] public string Name { get; init; } = "";
        [JsonPropertyName("strict")] public bool Strict { get; init; }
        [JsonPropertyName("schema")] public JsonDocument? Schema { get; init; }
    }

    private sealed class ChatRequest
    {
        [JsonPropertyName("model")] public string Model { get; init; } = "";
        [JsonPropertyName("messages")] public List<ChatMessage> Messages { get; init; } = [];
        [JsonPropertyName("temperature")] public double Temperature { get; init; }
        [JsonPropertyName("max_tokens")] public int MaxTokens { get; init; }
        [JsonPropertyName("response_format")] public ResponseFormat? ResponseFormat { get; init; }
        [JsonPropertyName("stream")] public bool Stream { get; init; }
    }
}

/// <summary>
/// Transcribes an equation crop to LaTeX by reusing RailReaderCore's VLM transport.
/// </summary>
/// <remarks>
/// Core's <c>IVlmService.DescribeBlockAsync</c> with <c>BlockAction.LaTeX</c> already does exactly
/// crop-to-LaTeX, so it is reused as-is. What Core does not do is source-linked narration planning,
/// and that deliberately lives in <see cref="OpenAiCompatibleMathVerbaliser"/> instead of changing the
/// meaning of an existing Core API. A text-only endpoint must not be treated as vision-capable, so
/// availability is probed rather than assumed.
/// </remarks>
public sealed class VlmMathTranscriber : IMathTranscriber
{
    private readonly IVlmService _vlm;
    private readonly VlmEndpointConfig _endpoint;
    private readonly IExtractionSource _extraction;
    private readonly int _cropPixelWidth;

    public VlmMathTranscriber(IVlmService vlm, VlmEndpointConfig endpoint, IExtractionSource extraction, int cropPixelWidth = 1024)
    {
        _vlm = vlm ?? throw new ArgumentNullException(nameof(vlm));
        _endpoint = endpoint;
        _extraction = extraction ?? throw new ArgumentNullException(nameof(extraction));
        _cropPixelWidth = cropPixelWidth;
    }

    public async Task<bool> IsAvailableAsync(CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(_endpoint.Endpoint) || string.IsNullOrWhiteSpace(_endpoint.Model))
            return false;
        try
        {
            // TestConnectionAsync returns null on success and a message otherwise; it does not throw
            // for the expected failures.
            string? error = await _vlm.TestConnectionAsync(_endpoint, ct).ConfigureAwait(false);
            return error is null;
        }
        catch (OperationCanceledException) { throw; }
        catch { return false; }
    }

    public async Task<MathTranscriptionResult> TranscribeAsync(MathTranscriptionRequest request, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (request.PngBytes.IsEmpty)
            return new MathTranscriptionResult(null, null, [], "No crop was supplied.", null);

        var result = await _vlm.DescribeBlockAsync(
            request.PngBytes.ToArray(), VlmService.BlockAction.LaTeX, _endpoint,
            VlmService.PromptStyle.Instruction, structuredOutput: false, ct).ConfigureAwait(false);

        if (result.Error is not null)
            return new MathTranscriptionResult(null, null, [], result.Error, null);
        if (string.IsNullOrWhiteSpace(result.Text))
            return new MathTranscriptionResult(null, null,
                ["The vision model returned no transcription for this crop."], "Empty transcription.", null);

        var warnings = new List<string>();
        // A confidence figure and a fluent LaTeX string are not evidence the expression was read
        // correctly. The crop reference is kept so the review panel can show the original beside the
        // transcription and let a human decide.
        warnings.Add("Transcription is unverified. Check it against the original crop before trusting the narration.");
        if (request.SourceTextHint is { Length: > 0 } hint && !OverlapsHint(result.Text!, hint))
            warnings.Add("The transcription differs substantially from the text layer under the crop, which may indicate a recognition problem.");

        return new MathTranscriptionResult(StripDelimiters(result.Text!), null, warnings, null, CropReference(request));
    }

    private static string StripDelimiters(string latex)
    {
        string s = latex.Trim();
        foreach (var (open, close) in new[] { ("$$", "$$"), ("\\[", "\\]"), ("$", "$"), ("\\(", "\\)") })
            if (s.StartsWith(open, StringComparison.Ordinal) && s.EndsWith(close, StringComparison.Ordinal) && s.Length > open.Length + close.Length)
                return s[open.Length..^close.Length].Trim();
        return s;
    }

    private static bool OverlapsHint(string transcription, string hint)
    {
        // A crude shared-token check: enough to flag a total mismatch without claiming agreement.
        var a = Tokens(transcription);
        var b = Tokens(hint);
        if (a.Count == 0 || b.Count == 0) return false;
        return a.Intersect(b).Count() / (double)Math.Min(a.Count, b.Count) > 0.3;
    }

    private static HashSet<string> Tokens(string s)
        => s.ToLowerInvariant().Split([' ', '{', '}', '\\', '^', '_', '(', ')'], StringSplitOptions.RemoveEmptyEntries).ToHashSet();

    private static string? CropReference(MathTranscriptionRequest request)
        => request.AnchorIds.Count > 0 ? string.Join(",", request.AnchorIds) : null;
}

/// <summary>
/// Resolves a mathematical unit's representation, transcribing the crop when extraction did not carry
/// LaTeX, and caching the result.
/// </summary>
public sealed class MathScriptResolver : IMathScriptResolver
{
    private readonly IMathTranscriber? _transcriber;
    private readonly IExtractionSource _extraction;
    private readonly Dictionary<string, string?> _cache = new(StringComparer.Ordinal);
    private readonly int _cropPixelWidth;

    public MathScriptResolver(
        IExtractionSource extraction,
        IMathTranscriber? transcriber = null,
        int cropPixelWidth = 1024)
    {
        _extraction = extraction ?? throw new ArgumentNullException(nameof(extraction));
        _transcriber = transcriber;
        _cropPixelWidth = cropPixelWidth;
    }

    public async Task<string?> ResolveRepresentationAsync(ReadingUnit unit, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(unit);
        if (unit.Latex is { Length: > 0 } existing) return existing;
        lock (_cache) if (_cache.TryGetValue(unit.Id, out var cached)) return cached;

        string? representation = null;
        if (_transcriber is not null && await _transcriber.IsAvailableAsync(ct).ConfigureAwait(false))
        {
            var anchor = unit.Anchors.FirstOrDefault(a => a.FramingBounds is not null);
            if (anchor?.FramingBounds is { } bounds)
            {
                var png = await _extraction.RenderCropPngAsync(anchor.Extraction, anchor.PageIndex, bounds, _cropPixelWidth, ct)
                    .ConfigureAwait(false);
                if (png is { } bytes && !bytes.IsEmpty)
                {
                    var result = await _transcriber.TranscribeAsync(new MathTranscriptionRequest(
                        bytes, unit.OriginalText, unit.ContextBefore, unit.ContextAfter, unit.AnchorIds), ct)
                        .ConfigureAwait(false);
                    if (result.Succeeded) representation = result.Representation;
                }
            }
        }

        lock (_cache) _cache[unit.Id] = representation;
        return representation;
    }

    /// <summary>Forgets cached transcriptions, e.g. after the user edits one or the extraction changes.</summary>
    public void Invalidate(string? unitId = null)
    {
        lock (_cache)
        {
            if (unitId is null) _cache.Clear();
            else _cache.Remove(unitId);
        }
    }
}

/// <summary>
/// Wraps <see cref="LocalLatexSpeaker"/> in the delegate shape <see cref="NarrationPlanner"/> expects.
/// </summary>
public static class LocalMathReader
{
    /// <summary>The reader function, for passing to <see cref="NarrationPlanner"/>.</summary>
    public static Func<ReadingUnit, string, string> Reader { get; } = (_, representation) => LocalLatexSpeaker.Speak(representation);
}
