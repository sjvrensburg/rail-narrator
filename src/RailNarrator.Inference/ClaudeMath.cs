using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Anthropic;
using Anthropic.Exceptions;
using Anthropic.Models.Messages;
using RailNarrator.Domain;
using Beta = Anthropic.Models.Beta.Messages;

namespace RailNarrator.Inference;

/// <summary>Configuration for maths through Claude (Anthropic's Messages API).</summary>
/// <param name="Model">A Claude model id; <see cref="DefaultModel"/> when blank.</param>
/// <param name="ApiKey">
/// Optional. Held in memory only and never written to a log or a message. When blank the SDK looks
/// for credentials itself (<c>ANTHROPIC_API_KEY</c>, <c>ANTHROPIC_AUTH_TOKEN</c>, an
/// <c>ant auth login</c> profile).
/// </param>
/// <param name="BaseUrl">Optional: a gateway that speaks the Messages API. Blank means Anthropic's own API.</param>
/// <param name="MaxTokens">
/// Cap on one response. Generous, because on current models thinking counts against it and a small
/// cap cuts the answer itself; only tokens generated are billed.
/// </param>
/// <param name="MaxRetries">Bounded retries for an answer that cannot be spoken, as for the other endpoint.</param>
/// <param name="RequestTimeout">Per-request timeout; a passage being read cannot wait the SDK's ten minutes.</param>
/// <param name="ContextCharLimit">Bound on the surrounding document text sent with an expression to verbalise.</param>
/// <param name="TransportRetries">The SDK's own retries of a rate limit, a server error or a dropped connection.</param>
public sealed record ClaudeEndpointSettings(
    string? Model = null,
    string? ApiKey = null,
    string? BaseUrl = null,
    int MaxTokens = 16000,
    int MaxRetries = 1,
    TimeSpan? RequestTimeout = null,
    int ContextCharLimit = 400,
    int TransportRetries = 2)
{
    /// <summary>The model used when none is named.</summary>
    public const string DefaultModel = "claude-opus-5-5";

    /// <summary>
    /// The address as the SDK wants it: the API's root, to which it adds "/v1/messages" itself.
    /// A reader pastes what the documentation's curl example shows, the whole
    /// "https://api.anthropic.com/v1/messages"; left as typed, every request went to
    /// ".../v1/messages/v1/..." and the model was reported as unknown.
    /// </summary>
    public string? BaseUrl { get; init; } = NormaliseAddress(BaseUrl);

    /// <summary>
    /// Strips a trailing "/v1/messages", "/v1" or "/messages", and gives null for Anthropic's own
    /// address, which is the same as naming none (so its model check and refusal fallback apply).
    /// </summary>
    public static string? NormaliseAddress(string? address)
    {
        if (string.IsNullOrWhiteSpace(address)) return null;
        string a = address.Trim().TrimEnd('/');
        foreach (string suffix in (string[])["/v1/messages", "/messages", "/v1"])
            if (a.EndsWith(suffix, StringComparison.OrdinalIgnoreCase)) { a = a[..^suffix.Length].TrimEnd('/'); break; }
        return a.Equals("https://api.anthropic.com", StringComparison.OrdinalIgnoreCase) || a.Length == 0 ? null : a;
    }

    public string EffectiveModel => string.IsNullOrWhiteSpace(Model) ? DefaultModel : Model.Trim();

    public TimeSpan Timeout => RequestTimeout ?? TimeSpan.FromMinutes(3);

    /// <summary>True when requests go to Anthropic's own API rather than a gateway.</summary>
    public bool IsAnthropicsOwnApi => string.IsNullOrWhiteSpace(BaseUrl);

    /// <summary>Where requests go, for cache keys and messages. Never holds a key.</summary>
    public string Address => IsAnthropicsOwnApi ? "api.anthropic.com" : BaseUrl!.Trim().TrimEnd('/');
}

/// <summary>One request to Claude: a system instruction, the passage's images, then the text.</summary>
/// <param name="MoreEffort">Medium effort instead of low: where the other endpoint would let its model think.</param>
/// <param name="JsonSchema">A JSON schema the answer must match, or null for plain text.</param>
internal sealed record ClaudeRequest(
    string System,
    IReadOnlyList<ReadOnlyMemory<byte>> Images,
    string Text,
    bool MoreEffort,
    string? JsonSchema = null);

/// <param name="Text">The answer's text blocks, joined. Null when there is an <paramref name="Error"/>.</param>
/// <param name="Refused">Claude declined the request (<c>stop_reason: refusal</c>).</param>
/// <param name="CutOff">The answer stopped at the token limit.</param>
/// <param name="Error">What went wrong, for the reader. Never contains the key.</param>
/// <param name="Final">Asking again cannot help (the key, the model name or the request itself is wrong).</param>
internal sealed record ClaudeReply(string? Text, bool Refused, bool CutOff, string? Error, bool Final)
{
    /// <summary>The failure was the endpoint's (key, quota, server, connection), not this request's.</summary>
    public bool Endpoint { get; init; }

    public static ClaudeReply Failed(string error, bool final) => new(null, false, false, error, final);
}

/// <summary>
/// The Messages API for the maths clients, through Anthropic's .NET SDK: one place that knows the
/// request's shape, which optional fields a model or gateway refused, and how an SDK exception reads
/// to the reader.
/// </summary>
/// <remarks>
/// <para>
/// Thinking is left to the model (adaptive, and on current models not switchable); depth is asked for
/// with <c>output_config.effort</c>, low or medium. A model that does not know the field is asked
/// again without it, and remembered. No sampling parameters are sent: current models reject them.
/// </para>
/// <para>
/// Against Anthropic's own API, a model whose safety classifiers can decline a request is sent
/// <c>fallbacks: "default"</c>, so a declined passage is answered by the model Anthropic names for
/// that refusal within the same call. A gateway may not know the beta, so it gets the plain request.
/// </para>
/// </remarks>
internal sealed class ClaudeMessages
{
    private const string ServerFallbackBeta = "server-side-fallback-2026-07-01";

    /// <summary>Models that accept <c>fallbacks: "default"</c>; the others have nothing to fall back to or reject it.</summary>
    private static readonly HashSet<string> ServerFallbackModels = new(StringComparer.Ordinal)
    {
        "claude-opus-5-5", "claude-opus-5", "claude-sonnet-5-5",
        "claude-fable-5-2", "claude-fable-5-1", "claude-fable-5",
        "claude-mythos-5-2", "claude-mythos-5-1",
    };

    // One connection pool for every client. Infinite, because the SDK enforces its own timeout and
    // HttpClient's 100 s default would cut a long answer short.
    private static readonly HttpClient SharedHttp = new() { Timeout = System.Threading.Timeout.InfiniteTimeSpan };

    private readonly ClaudeEndpointSettings _settings;
    private readonly AnthropicClient _client;
    private readonly string _model;
    private bool _effortRejected, _schemaRejected, _fallbackRejected;
    private bool? _available;

    public ClaudeMessages(ClaudeEndpointSettings settings, HttpClient? httpClient = null)
    {
        _settings = settings ?? throw new ArgumentNullException(nameof(settings));
        _model = settings.EffectiveModel;
        _client = CreateClient(settings.ApiKey, settings.BaseUrl, httpClient, settings.Timeout, settings.TransportRetries);
    }

    public ClaudeEndpointSettings Settings => _settings;

    public string Model => _model;

    /// <summary>An optional field was refused and dropped (effort, the schema or the refusal fallback), for tests.</summary>
    internal bool AnOptionalFieldWasRefused => _effortRejected || _schemaRejected || _fallbackRejected;

    /// <summary>
    /// Only what the settings supply is overridden: an unset key or address keeps the SDK's own
    /// resolution (environment, an <c>ant auth login</c> profile, the default address).
    /// </summary>
    internal static AnthropicClient CreateClient(string? apiKey, string? baseUrl, HttpClient? httpClient, TimeSpan timeout, int transportRetries)
    {
        var http = httpClient ?? SharedHttp;
        bool hasKey = !string.IsNullOrWhiteSpace(apiKey), hasUrl = !string.IsNullOrWhiteSpace(baseUrl);
        return (hasKey, hasUrl) switch
        {
            (true, true) => new AnthropicClient { HttpClient = http, Timeout = timeout, MaxRetries = transportRetries, ApiKey = apiKey!.Trim(), BaseUrl = baseUrl!.Trim() },
            (true, false) => new AnthropicClient { HttpClient = http, Timeout = timeout, MaxRetries = transportRetries, ApiKey = apiKey!.Trim() },
            (false, true) => new AnthropicClient { HttpClient = http, Timeout = timeout, MaxRetries = transportRetries, BaseUrl = baseUrl!.Trim() },
            _ => new AnthropicClient { HttpClient = http, Timeout = timeout, MaxRetries = transportRetries },
        };
    }

    private bool UsesServerFallback => !_fallbackRejected && _settings.IsAnthropicsOwnApi
        && string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("ANTHROPIC_BASE_URL"))
        && ServerFallbackModels.Contains(_model);

    /// <summary>
    /// Whether the model can be asked. Anthropic's own API is asked for the model (no tokens); a
    /// gateway may offer nothing but Messages, so it is taken to be there and its first request tells.
    /// </summary>
    public async Task<bool> IsAvailableAsync(CancellationToken ct)
    {
        if (_available is { } cached) return cached;
        if (!_settings.IsAnthropicsOwnApi) return (bool)(_available = true);
        try
        {
            await _client.Models.Retrieve(_model, cancellationToken: ct).ConfigureAwait(false);
            return (bool)(_available = true);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        // The key or the model name is wrong; that does not mend itself while the application runs.
        catch (Exception ex) when (ex is AnthropicUnauthorizedException or AnthropicForbiddenException or AnthropicNotFoundException)
        {
            UnavailableReason = Describe(ex, _settings);
            return (bool)(_available = false);
        }
        catch (Exception ex)
        {
            UnavailableReason = Describe(ex, _settings);
            return false;   // not cached: the network may come back
        }
    }

    /// <summary>Why the model could not be asked when <see cref="IsAvailableAsync"/> last said no.</summary>
    public string? UnavailableReason { get; private set; }

    public async Task<ClaudeReply> SendAsync(ClaudeRequest request, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(request);
        // A refused optional field is dropped and the request sent again; three fields, so at most four sends.
        for (int attempt = 0; attempt < 4; attempt++)
        {
            bool effort = !_effortRejected, schema = request.JsonSchema is not null && !_schemaRejected, fallback = UsesServerFallback;
            try
            {
                if (fallback)
                {
                    var response = await _client.Beta.Messages.Create(BuildFallbackParams(request, effort, schema), ct).ConfigureAwait(false);
                    return new ClaudeReply(TextOf(response), response.StopReason == "refusal", response.StopReason == "max_tokens", null, false);
                }
                else
                {
                    var response = await _client.Messages.Create(BuildParams(request, effort, schema), ct).ConfigureAwait(false);
                    return new ClaudeReply(TextOf(response), response.StopReason == "refusal", response.StopReason == "max_tokens", null, false);
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch (AnthropicBadRequestException ex)
            {
                string message = ex.Message;
                if (fallback && (Mentions(message, "fallback") || Mentions(message, "beta"))) { _fallbackRejected = true; continue; }
                if (effort && Mentions(message, "effort")) { _effortRejected = true; continue; }
                if (schema && (Mentions(message, "schema") || Mentions(message, "format"))) { _schemaRejected = true; continue; }
                if ((effort || schema) && Mentions(message, "output_config")) { _effortRejected = _schemaRejected = true; continue; }
                return ClaudeReply.Failed("Claude rejected the request: " + Short(message), final: true);
            }
            catch (Exception ex)
            {
                return ClaudeReply.Failed(Describe(ex, _settings),
                    final: ex is AnthropicUnauthorizedException or AnthropicForbiddenException or AnthropicNotFoundException) with { Endpoint = true };
            }
        }
        return ClaudeReply.Failed("Claude rejected the request however it was put.", final: true);
    }

    private MessageCreateParams BuildParams(ClaudeRequest request, bool effort, bool schema)
    {
        var content = new List<ContentBlockParam>();
        foreach (var image in request.Images)
            content.Add(new ImageBlockParam
            {
                Source = new Base64ImageSource { Data = Convert.ToBase64String(image.Span), MediaType = MediaType.ImagePng },
            });
        content.Add(new TextBlockParam { Text = request.Text });

        var parameters = new MessageCreateParams
        {
            Model = _model,
            MaxTokens = _settings.MaxTokens,
            System = request.System,
            Messages = [new UserMessageParam { Content = content }],
        };
        if (!effort && !schema) return parameters;
        var output = new OutputConfig();
        if (effort) output = output with { Effort = request.MoreEffort ? Effort.Medium : Effort.Low };
        if (schema) output = output with { Format = new JsonOutputFormat { Schema = ClaudeSchema(request.JsonSchema!) } };
        return parameters with { OutputConfig = output };
    }

    /// <summary><see cref="BuildParams"/> on the beta endpoint, with <c>fallbacks: "default"</c>.</summary>
    private Beta.MessageCreateParams BuildFallbackParams(ClaudeRequest request, bool effort, bool schema)
    {
        var content = new List<Beta.BetaContentBlockParam>();
        foreach (var image in request.Images)
            content.Add(new Beta.BetaImageBlockParam
            {
                Source = new Beta.BetaBase64ImageSource { Data = Convert.ToBase64String(image.Span), MediaType = Beta.MediaType.ImagePng },
            });
        content.Add(new Beta.BetaTextBlockParam { Text = request.Text });

        var parameters = new Beta.MessageCreateParams
        {
            Model = _model,
            MaxTokens = _settings.MaxTokens,
            System = request.System,
            Betas = [ServerFallbackBeta],
            Fallbacks = new Beta.Default(),
            Messages = [new Beta.BetaUserMessageParam { Content = content }],
        };
        if (!effort && !schema) return parameters;
        var output = new Beta.BetaOutputConfig();
        if (effort) output = output with { Effort = request.MoreEffort ? Beta.Effort.Medium : Beta.Effort.Low };
        if (schema) output = output with { Format = new Beta.BetaJsonOutputFormat { Schema = ClaudeSchema(request.JsonSchema!) } };
        return parameters with { OutputConfig = output };
    }

    /// <summary>
    /// The schema as the SDK's property map, without the string-length keywords: structured output
    /// does not accept them, and the response is validated against the full schema here anyway.
    /// </summary>
    internal static Dictionary<string, JsonElement> ClaudeSchema(string schema)
    {
        var node = JsonNode.Parse(schema)!;
        Strip(node);
        using var document = JsonDocument.Parse(node.ToJsonString());
        return document.RootElement.EnumerateObject().ToDictionary(p => p.Name, p => p.Value.Clone());

        static void Strip(JsonNode? n)
        {
            if (n is JsonObject o)
            {
                o.Remove("minLength");
                o.Remove("maxLength");
                foreach (var child in o.Select(p => p.Value).ToList()) Strip(child);
            }
            else if (n is JsonArray a)
            {
                foreach (var child in a) Strip(child);
            }
        }
    }

    /// <summary>The answer's text blocks, joined; thinking blocks are not part of the answer.</summary>
    private static string TextOf(Message response)
    {
        var text = new StringBuilder();
        foreach (var block in response.Content)
            if (block.TryPickText(out TextBlock? t)) text.Append(t.Text);
        return text.ToString().Trim();
    }

    private static string TextOf(Beta.BetaMessage response)
    {
        var text = new StringBuilder();
        foreach (var block in response.Content)
            if (block.TryPickText(out Beta.BetaTextBlock? t)) text.Append(t.Text);
        return text.ToString().Trim();
    }

    private static bool Mentions(string message, string word) => message.Contains(word, StringComparison.OrdinalIgnoreCase);

    private static string Short(string s) => s.Length <= 300 ? s : s[..300] + "…";

    /// <summary>An SDK failure in the reader's words. The key is in none of them.</summary>
    internal static string Describe(Exception ex, ClaudeEndpointSettings settings) => ex switch
    {
        // Also what a missing key looks like: with none configured the SDK still sends the request.
        AnthropicUnauthorizedException => "Claude refused the API key, or none is set.",
        AnthropicForbiddenException => "The API key may not use this model.",
        AnthropicNotFoundException => $"Claude does not know the model '{settings.EffectiveModel}'.",
        AnthropicRateLimitException => "Claude is rate-limiting requests; the passage was not answered.",
        AnthropicApiException api => "Claude answered with an error: " + Short(api.Message),
        // The SDK's own message is a bare "I/O exception"; the cause is on the inner exception.
        AnthropicIOException io => $"Could not reach {settings.Address}: {io.InnerException?.Message ?? io.Message}",
        HttpRequestException http => $"Could not reach {settings.Address}: {http.Message}",
        OperationCanceledException => $"No answer from {settings.Address} within {settings.Timeout.TotalSeconds:F0} s.",
        _ => "request failed: " + ex.Message,
    };
}

/// <summary>
/// Interprets maths through Claude, sending the passage's image with its text layer and context.
/// </summary>
/// <remarks>
/// <para>
/// The same contract as <see cref="OpenAiCompatibleMathInterpreter"/>, with the same prompt and the
/// same validator: every response is validated, an answer with notation gets one repair turn and is
/// then refused, a response cut off at the token limit is a failure, and the input is never passed
/// through. The repair is a new request that carries the rejected answer as text, not a continued
/// conversation, so no thinking block has to be replayed.
/// </para>
/// <para>
/// <see cref="MathThinking"/> sets the effort, since Claude decides its own thinking: medium where
/// the other endpoint's model would think, low where it would answer directly.
/// </para>
/// <para>
/// Also an <see cref="IMathReader"/>: a refused interpretation is transcribed to LaTeX from the image
/// by the same model and read by rule.
/// </para>
/// </remarks>
public sealed class ClaudeMathInterpreter : IMathInterpreter, IMathReader
{
    private readonly ClaudeMessages _messages;
    private readonly MathThinking _thinking;

    public ClaudeMathInterpreter(ClaudeEndpointSettings settings, HttpClient? httpClient = null, MathThinking thinking = MathThinking.EquationsOnly)
    {
        _messages = new ClaudeMessages(settings, httpClient);
        _thinking = thinking;
    }

    public string Revision => $"{MathInterpretationPrompt.Version}|claude|{_messages.Settings.Address}|{_messages.Model}";

    string IMathReader.Revision => $"{MathTranscriptionReading.PromptVersion}|{LocalLatexSpeaker.Version}|claude|{_messages.Settings.Address}|{_messages.Model}";

    internal bool AnOptionalFieldWasRefused => _messages.AnOptionalFieldWasRefused;

    public Task<bool> IsAvailableAsync(CancellationToken ct = default) => _messages.IsAvailableAsync(ct);

    public string? UnavailableReason => _messages.UnavailableReason;

    public async Task<MathInterpretationResult> InterpretAsync(MathInterpretationRequest request, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        bool withImages = request.Images.Count > 0;
        bool moreEffort = _thinking switch
        {
            MathThinking.Off => false,
            MathThinking.EquationsOnly => request.Kind == MathPassageKind.DisplayEquation || request.HasDisplayEquation,
            _ => true,
        };
        string passage = MathInterpretationPrompt.User(request, withImages);
        string text = passage;
        var problems = new List<string>();
        string? endpointProblem = null;

        for (int answers = 0; answers <= Math.Max(1, _messages.Settings.MaxRetries); answers++)
        {
            ct.ThrowIfCancellationRequested();
            var reply = await _messages.SendAsync(
                new ClaudeRequest(MathInterpretationPrompt.System, request.Images, text, moreEffort), ct).ConfigureAwait(false);
            if (reply.Error is not null)
            {
                problems.Add(reply.Error);
                endpointProblem = reply.Endpoint ? reply.Error : null;
                if (reply.Final) break;
                continue;
            }
            if (reply.Refused)
            {
                // The same passage asked again is declined again.
                problems.Add("Claude declined to interpret this passage.");
                break;
            }
            if (reply.CutOff)
            {
                problems.Add("The response was cut off at the token limit.");
                continue;
            }

            string spoken = MathAnswerText.Clean(reply.Text);
            var found = SpokenMathsValidator.Problems(spoken, request);
            if (found.Count == 0) return new MathInterpretationResult(spoken, [], []);
            problems.AddRange(found);

            // One repair turn with the complaint, then refuse.
            text = passage + "\n\nYOUR PREVIOUS ANSWER, WHICH WAS REJECTED:\n<<<\n" + spoken + "\n>>>\n\n" + MathAnswerText.RepairRequest(found);
        }
        return new MathInterpretationResult(null, [], problems.Count > 0 ? problems : ["No usable response."]) { EndpointProblem = endpointProblem };
    }

    /// <summary>
    /// The passage transcribed from its image and read by rule. Null without an image, or when the
    /// transcription cannot be read without leaving notation behind.
    /// </summary>
    public async Task<string?> ReadAsync(MathInterpretationRequest request, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (request.Images.Count == 0) return null;
        var reply = await _messages.SendAsync(new ClaudeRequest(
            MathTranscriptionReading.System, request.Images, MathTranscriptionReading.UserText(request), MoreEffort: false), ct).ConfigureAwait(false);
        if (reply.Error is not null || reply.Refused || reply.CutOff) return null;
        return MathTranscriptionReading.Spoken(reply.Text, request);
    }
}

/// <summary>
/// Verbalises recognised mathematics through Claude: the older reading modes' counterpart of
/// <see cref="OpenAiCompatibleMathVerbaliser"/>, with the same prompt, schema and validator.
/// </summary>
/// <remarks>
/// The JSON is asked for as structured output and still goes through
/// <see cref="MathResponseValidator"/>: the schema guarantees the shape, not the source ids or the
/// content. A rejected response is asked for again with the complaint, as a new request.
/// </remarks>
public sealed class ClaudeMathVerbaliser : IMathVerbaliser
{
    private readonly ClaudeMessages _messages;

    public ClaudeMathVerbaliser(ClaudeEndpointSettings settings, HttpClient? httpClient = null)
        => _messages = new ClaudeMessages(settings, httpClient);

    public string ModelRevision => $"claude/{_messages.Model}";

    internal bool AnOptionalFieldWasRefused => _messages.AnOptionalFieldWasRefused;

    public Task<bool> IsAvailableAsync(CancellationToken ct = default) => _messages.IsAvailableAsync(ct);

    public async Task<MathVerbalisationResult> VerbaliseAsync(MathVerbalisationRequest request, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var mode = request.Mode switch
        {
            MathMode.DetailedSymbolic => MathModeRequest.DetailedSymbolic,
            MathMode.Explain => MathModeRequest.Explain,
            _ => MathModeRequest.NaturalFaithful,
        };
        var settings = _messages.Settings;
        string expression = MathPrompt.BuildUserMessage(
            request.Representation, mode, request.ContextBefore, request.ContextAfter, request.AnchorIds, settings.ContextCharLimit);
        string text = expression;
        var allowed = request.AnchorIds.ToHashSet(StringComparer.Ordinal);
        var problems = new List<string>();
        int attempts = 0;

        for (int attempt = 0; attempt <= settings.MaxRetries; attempt++)
        {
            attempts++;
            ct.ThrowIfCancellationRequested();
            var reply = await _messages.SendAsync(new ClaudeRequest(
                MathPrompt.SystemInstruction, [], text, MoreEffort: false, MathPrompt.ResponseSchemaJson), ct).ConfigureAwait(false);
            if (reply.Error is not null)
            {
                problems.Add(reply.Error);
                if (reply.Final) break;
                continue;
            }
            if (reply.Refused) { problems.Add("Claude declined to read this expression."); break; }
            if (reply.CutOff) { problems.Add("The response was cut off at the token limit."); continue; }

            var result = MathResponseValidator.Validate(reply.Text, allowed, mode);
            if (result.IsValid)
            {
                return new MathVerbalisationResult(
                    result.Payload!.Faithful, result.Payload.Explanation, result.Payload.SourceIds,
                    [.. result.Payload.Warnings, .. result.Warnings], Rejected: false, [],
                    MathPrompt.Version, ModelRevision, attempts);
            }

            problems.AddRange(result.Problems);
            text = expression + "\n\nYOUR PREVIOUS RESPONSE, WHICH WAS REJECTED:\n<<<\n" + reply.Text + "\n>>>\n\n" +
                   "Fix exactly these problems and return only JSON matching the \"" + MathPrompt.ResponseSchemaName + "\" schema:\n- " +
                   string.Join("\n- ", result.Problems);
        }
        return MathVerbalisationResult.Reject(problems, MathPrompt.Version, ModelRevision, attempts);
    }
}

/// <summary>
/// Checks Claude for the Settings dialogue, with values that may not be saved yet: the counterpart
/// of <see cref="EndpointProbe"/>.
/// </summary>
/// <remarks>
/// The probe asks the Models API for the model named and for the list: no tokens. A gateway that
/// offers only Messages answers "not found" to both, which reads as an unknown model; the first
/// passage then tells.
/// </remarks>
public static class ClaudeEndpointProbe
{
    public static async Task<EndpointProbeResult> ProbeAsync(
        string? baseUrl, string? model, string? apiKey, HttpMessageHandler? handler = null,
        TimeSpan? timeout = null, CancellationToken ct = default)
    {
        if (!string.IsNullOrWhiteSpace(baseUrl)
            && (!Uri.TryCreate(baseUrl.Trim(), UriKind.Absolute, out var address) || address.Scheme is not ("http" or "https")))
            return new(EndpointProbeStatus.NotConfigured, $"'{baseUrl.Trim()}' is not an http or https address. Leave it empty for Anthropic's own API.", []);

        var settings = new ClaudeEndpointSettings(model, apiKey, baseUrl, RequestTimeout: timeout ?? TimeSpan.FromSeconds(15));
        using var http = handler is null ? null : new HttpClient(handler, disposeHandler: false) { Timeout = Timeout.InfiniteTimeSpan };
        var client = ClaudeMessages.CreateClient(apiKey, settings.BaseUrl, http, settings.Timeout, transportRetries: 0);
        string wanted = settings.EffectiveModel;

        var models = new List<string>();
        try
        {
            var page = await client.Models.List(cancellationToken: ct).ConfigureAwait(false);
            models.AddRange(page.Items.Select(m => m.ID).Order(StringComparer.Ordinal));
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception ex) when (ex is AnthropicUnauthorizedException or AnthropicForbiddenException)
        {
            return KeyRefused(apiKey, settings);
        }
        catch (Exception ex) when (ex is AnthropicIOException or HttpRequestException or OperationCanceledException)
        {
            return new(EndpointProbeStatus.Unreachable, ClaudeMessages.Describe(ex, settings), []);
        }
        catch { /* a gateway without a model list: the model itself is asked for below */ }

        try
        {
            await client.Models.Retrieve(wanted, cancellationToken: ct).ConfigureAwait(false);
            return new(EndpointProbeStatus.Ready, $"{settings.Address} answered and offers {wanted}.", models);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception ex) when (ex is AnthropicUnauthorizedException or AnthropicForbiddenException)
        {
            return KeyRefused(apiKey, settings);
        }
        catch (AnthropicNotFoundException)
        {
            return new(EndpointProbeStatus.ModelNotListed, models.Count == 0
                ? $"{settings.Address} answered but does not know {wanted}."
                : $"{settings.Address} answered but does not know {wanted}. It offers: {string.Join(", ", models.Take(8))}{(models.Count > 8 ? ", …" : "")}.",
                models);
        }
        catch (Exception ex) when (ex is AnthropicIOException or HttpRequestException or OperationCanceledException)
        {
            return new(EndpointProbeStatus.Unreachable, ClaudeMessages.Describe(ex, settings), []);
        }
        catch (Exception ex)
        {
            return new(EndpointProbeStatus.UnexpectedAnswer, ClaudeMessages.Describe(ex, settings), models);
        }
    }

    private static EndpointProbeResult KeyRefused(string? apiKey, ClaudeEndpointSettings settings) =>
        new(EndpointProbeStatus.KeyRefused, string.IsNullOrWhiteSpace(apiKey)
            ? $"{settings.Address} wants an API key, and none was found (none is set here, and the Anthropic SDK found none in the environment)."
            : $"{settings.Address} refused the API key.", []);
}
