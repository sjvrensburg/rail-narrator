using System.Net;
using System.Text;
using System.Text.Json;
using RailNarrator.Domain;
using RailNarrator.Inference;
using RailReader.Core.Models;
using RailReader.Core.Services;
using Xunit;

namespace RailNarrator.Inference.Tests;

/// <summary>
/// Maths through Claude keeps the contract of the OpenAI-compatible clients: the same prompt and
/// validators, nothing passed through, and a request shaped as Anthropic's Messages API wants it.
/// The transport is stubbed below the SDK, so what is checked is the request the SDK really sends.
/// </summary>
[Collection("Claude")]
public sealed class ClaudeTests
{
    private const string Key = "test-key-0123456789";

    private const string Good = "The short-run component equals a constant plus weighted past squared shocks, and past values of itself.";

    private static MathInterpretationRequest Request(int images = 1) => new(
        MathPassageKind.DisplayEquation,
        "ht = α0 + Xqi=1 αiε∗2t−i + Xpj=1 βjht−j, (3)",
        Enumerable.Range(0, images).Select(_ => (ReadOnlyMemory<byte>)new byte[] { 0x89, 0x50, 0x4E, 0x47 }).ToList(),
        "To allow an asymmetric response of volatility, ht is modelled as the GJR-GARCH process.",
        "where the indicator is one when the shock is negative.",
        "en-GB");

    private sealed record Sent(string Path, JsonDocument Body, string? Beta, string Credential);

    private sealed class Stub : HttpMessageHandler
    {
        private readonly Queue<Func<HttpResponseMessage>> _responses;
        public List<Sent> Messages { get; } = [];
        public List<string> Gets { get; } = [];
        public Func<string, HttpResponseMessage> OnGet { get; init; } = path => path.EndsWith("/models", StringComparison.Ordinal)
            ? Json("""{"data":[{"id":"claude-opus-5-5","type":"model","display_name":"Claude Opus 5.5","created_at":"2026-01-01T00:00:00Z"}],"has_more":false,"first_id":"claude-opus-5-5","last_id":"claude-opus-5-5"}""")
            : Json("""{"id":"claude-opus-5-5","type":"model","display_name":"Claude Opus 5.5","created_at":"2026-01-01T00:00:00Z"}""");

        public Stub(params Func<HttpResponseMessage>[] responses) => _responses = new(responses);

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            string path = request.RequestUri!.AbsolutePath;
            if (request.Method == HttpMethod.Get) { Gets.Add(path); return OnGet(path); }
            Messages.Add(new Sent(path, JsonDocument.Parse(await request.Content!.ReadAsStringAsync(ct)),
                request.Headers.TryGetValues("anthropic-beta", out var beta) ? string.Join(",", beta) : null,
                // The SDK chooses the header for the credential (x-api-key or a bearer token).
                string.Join(",", request.Headers.Where(h => h.Key is "x-api-key" or "Authorization").SelectMany(h => h.Value))));
            return (_responses.Count > 1 ? _responses.Dequeue() : _responses.Peek())();
        }
    }

    private static HttpResponseMessage Json(string body, HttpStatusCode status = HttpStatusCode.OK)
        => new(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") };

    private static HttpResponseMessage Answer(string text, string stop = "end_turn")
        => Json(JsonSerializer.Serialize(new
        {
            id = "msg_1", type = "message", role = "assistant", model = "claude-opus-5-5",
            content = new object[] { new { type = "thinking", thinking = "", signature = "s" }, new { type = "text", text } },
            stop_reason = stop, stop_sequence = (string?)null,
            usage = new { input_tokens = 10, output_tokens = 10 },
        }));

    private static HttpResponseMessage Error(HttpStatusCode status, string type, string message)
        => Json(JsonSerializer.Serialize(new { type = "error", error = new { type, message } }), status);

    private static ClaudeEndpointSettings Settings(string? model = null, string? baseUrl = null)
        => new(model, Key, baseUrl, TransportRetries: 0);

    private static ClaudeMathInterpreter Make(Stub stub, MathThinking thinking = MathThinking.EquationsOnly, string? model = null, string? baseUrl = null)
        => new(Settings(model, baseUrl), new HttpClient(stub), thinking);

    private static string? Effort(Sent sent)
        => sent.Body.RootElement.TryGetProperty("output_config", out var o) && o.TryGetProperty("effort", out var e) ? e.GetString() : null;

    private static string UserText(Sent sent)
        => sent.Body.RootElement.GetProperty("messages")[0].GetProperty("content").EnumerateArray()
            .Last(b => b.GetProperty("type").GetString() == "text").GetProperty("text").GetString()!;

    // ---- interpretation --------------------------------------------------------------------

    [Fact]
    public async Task ThePassageIsSentAsAMessagesRequest_ImageFirst_WithThePromptAsTheSystemText()
    {
        var stub = new Stub(() => Answer(Good));
        var result = await Make(stub).InterpretAsync(Request());

        Assert.True(result.Succeeded);
        Assert.Equal(Good, result.Spoken);
        var sent = Assert.Single(stub.Messages);
        Assert.EndsWith("/v1/messages", sent.Path, StringComparison.Ordinal);
        Assert.Contains(Key, sent.Credential, StringComparison.Ordinal);
        var body = sent.Body.RootElement;
        // No model named: Claude Opus.
        Assert.Equal(ClaudeEndpointSettings.DefaultModel, body.GetProperty("model").GetString());
        Assert.Equal(16000, body.GetProperty("max_tokens").GetInt32());
        var system = body.GetProperty("system");
        Assert.Equal(MathInterpretationPrompt.System,
            system.ValueKind == JsonValueKind.String ? system.GetString() : system[0].GetProperty("text").GetString());
        var content = body.GetProperty("messages")[0].GetProperty("content");
        Assert.Equal("image", content[0].GetProperty("type").GetString());
        Assert.Equal("base64", content[0].GetProperty("source").GetProperty("type").GetString());
        Assert.Equal("image/png", content[0].GetProperty("source").GetProperty("media_type").GetString());
        Assert.Equal("text", content[1].GetProperty("type").GetString());
        Assert.Contains("THE PASSAGE'S TEXT LAYER", content[1].GetProperty("text").GetString(), StringComparison.Ordinal);
        // Current models reject sampling parameters and a thinking switch; neither is sent.
        Assert.False(body.TryGetProperty("temperature", out _));
        Assert.False(body.TryGetProperty("thinking", out _));
    }

    [Fact]
    public async Task TheThinkingPolicySetsTheEffort_MediumWhereTheOtherEndpointWouldThink()
    {
        var stub = new Stub(() => Answer(Good));
        var interpreter = Make(stub, MathThinking.EquationsOnly);
        var sentence = Request() with { Kind = MathPassageKind.SentenceWithMaths };

        await interpreter.InterpretAsync(Request());
        await interpreter.InterpretAsync(sentence);
        await interpreter.InterpretAsync(sentence with { HasDisplayEquation = true });
        await Make(stub, MathThinking.Off).InterpretAsync(Request());
        await Make(stub, MathThinking.Always).InterpretAsync(sentence);

        Assert.Equal(["medium", "low", "medium", "low", "medium"], stub.Messages.Select(Effort));
    }

    [Fact]
    public async Task AgainstAnthropicsOwnApi_ADeclinedPassageMayBeAnsweredByTheFallbackModel()
    {
        var stub = new Stub(() => Answer(Good));
        await Make(stub).InterpretAsync(Request());                                        // Claude Opus, Anthropic's API
        await Make(stub, baseUrl: "http://127.0.0.1:9").InterpretAsync(Request());        // a gateway
        await Make(stub, model: "claude-haiku-5-5").InterpretAsync(Request());            // nothing to fall back to

        // The SDK reads a gateway address from the environment too, and a gateway may not know the beta.
        bool gatewayInEnvironment = !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("ANTHROPIC_BASE_URL"));
        var own = stub.Messages[0];
        Assert.Equal(!gatewayInEnvironment, own.Body.RootElement.TryGetProperty("fallbacks", out var fallbacks));
        if (!gatewayInEnvironment)
        {
            Assert.Equal("default", fallbacks.GetString());
            Assert.Contains("server-side-fallback-2026-07-01", own.Beta, StringComparison.Ordinal);
        }
        foreach (var plain in stub.Messages.Skip(1))
        {
            Assert.False(plain.Body.RootElement.TryGetProperty("fallbacks", out _));
            Assert.Null(plain.Beta);
        }
    }

    [Fact]
    public async Task NotationGetsOneRepairTurn_AsANewRequestCarryingTheRejectedAnswer()
    {
        var stub = new Stub(() => Answer("The variance is σ squared and x_t follows."), () => Answer(Good));
        var result = await Make(stub).InterpretAsync(Request());

        Assert.True(result.Succeeded);
        Assert.Equal(Good, result.Spoken);
        Assert.Equal(2, stub.Messages.Count);
        // A new conversation, not a continued one: no assistant turn, so no thinking block to replay.
        var messages = stub.Messages[1].Body.RootElement.GetProperty("messages");
        Assert.Equal(1, messages.GetArrayLength());
        string repair = UserText(stub.Messages[1]);
        Assert.Contains("THE PASSAGE'S TEXT LAYER", repair, StringComparison.Ordinal);
        Assert.Contains("The variance is σ squared and x_t follows.", repair, StringComparison.Ordinal);
        Assert.Contains("That cannot be spoken as it is", repair, StringComparison.Ordinal);
    }

    [Fact]
    public async Task NotationAfterTheRepairIsRefused_NeverPassedThrough()
    {
        var stub = new Stub(() => Answer("The variance is σ squared."));
        var result = await Make(stub).InterpretAsync(Request());

        Assert.False(result.Succeeded);
        Assert.Null(result.Spoken);
        Assert.Equal(2, stub.Messages.Count);
        Assert.Contains(result.Problems, p => p.Contains("σ", StringComparison.Ordinal));
    }

    [Fact]
    public async Task APassageClaudeDeclinesIsRefusedAtOnce_NotAskedAgain()
    {
        var stub = new Stub(() => Answer("", stop: "refusal"));
        var result = await Make(stub).InterpretAsync(Request());

        Assert.False(result.Succeeded);
        Assert.Single(stub.Messages);
        Assert.Contains(result.Problems, p => p.Contains("declined", StringComparison.Ordinal));
    }

    [Fact]
    public async Task AResponseCutOffAtTheTokenLimitIsAFailure()
    {
        var stub = new Stub(() => Answer("The short-run component equals a constant plus", stop: "max_tokens"));
        var result = await Make(stub).InterpretAsync(Request());

        Assert.False(result.Succeeded);
        Assert.Contains(result.Problems, p => p.Contains("cut off", StringComparison.Ordinal));
    }

    [Fact]
    public async Task AModelThatDoesNotKnowEffortIsAskedAgainWithoutIt_AndRemembered()
    {
        var stub = new Stub(
            () => Error(HttpStatusCode.BadRequest, "invalid_request_error", "output_config.effort: this model does not support the effort parameter"),
            () => Answer(Good));
        var interpreter = Make(stub, model: "claude-haiku-4-5");

        var first = await interpreter.InterpretAsync(Request());
        var second = await interpreter.InterpretAsync(Request());

        Assert.True(first.Succeeded);
        Assert.True(second.Succeeded);
        Assert.Equal(3, stub.Messages.Count);
        Assert.Equal("medium", Effort(stub.Messages[0]));
        Assert.Null(Effort(stub.Messages[1]));
        Assert.Null(Effort(stub.Messages[2]));
    }

    [Fact]
    public async Task ARefusedKeyEndsThePassageAtOnce_AndTheKeyIsInNoMessage()
    {
        var stub = new Stub(() => Error(HttpStatusCode.Unauthorized, "authentication_error", "invalid x-api-key"));
        var result = await Make(stub).InterpretAsync(Request());

        Assert.False(result.Succeeded);
        Assert.Single(stub.Messages);
        Assert.Contains(result.Problems, p => p.Contains("API key", StringComparison.Ordinal));
        Assert.DoesNotContain(result.Problems, p => p.Contains(Key, StringComparison.Ordinal));
    }

    [Fact]
    public async Task WithNoKeyAnywhere_TheClientsAreStillBuilt_AndThePassageSaysWhy()
    {
        // Claude can be chosen in Settings before a key exists; that must not stop start-up.
        string? saved = Environment.GetEnvironmentVariable("ANTHROPIC_API_KEY"), savedToken = Environment.GetEnvironmentVariable("ANTHROPIC_AUTH_TOKEN");
        Environment.SetEnvironmentVariable("ANTHROPIC_API_KEY", null);
        Environment.SetEnvironmentVariable("ANTHROPIC_AUTH_TOKEN", null);
        try
        {
            var stub = new Stub(() => Error(HttpStatusCode.Unauthorized, "authentication_error", "x-api-key header is required"));
            var interpreter = new ClaudeMathInterpreter(new ClaudeEndpointSettings(TransportRetries: 0), new HttpClient(stub));
            _ = new ClaudeMathVerbaliser(new ClaudeEndpointSettings(TransportRetries: 0), new HttpClient(stub));

            var result = await interpreter.InterpretAsync(Request());

            Assert.False(result.Succeeded);
            Assert.Contains(result.Problems, p => p.Contains("API key", StringComparison.Ordinal) || p.Contains("credential", StringComparison.OrdinalIgnoreCase));
        }
        finally
        {
            Environment.SetEnvironmentVariable("ANTHROPIC_API_KEY", saved);
            Environment.SetEnvironmentVariable("ANTHROPIC_AUTH_TOKEN", savedToken);
        }
    }

    [Fact]
    public void TheRevisionNamesClaudeTheAddressAndTheModel_SoItsAnswersAreCachedApart()
    {
        var claude = Make(new Stub(() => Answer(Good)));
        Assert.Equal($"{MathInterpretationPrompt.Version}|claude|api.anthropic.com|claude-opus-5-5", claude.Revision);
        Assert.Contains("|claude|api.anthropic.com|claude-opus-5-5", ((IMathReader)claude).Revision, StringComparison.Ordinal);
        Assert.DoesNotContain(Key, claude.Revision, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Availability_AsksAnthropicForTheModel_AndTakesAGatewayOnTrust()
    {
        var own = new Stub(() => Answer(Good));
        Assert.True(await Make(own).IsAvailableAsync());
        Assert.Contains(own.Gets, p => p.EndsWith("/v1/models/claude-opus-5-5", StringComparison.Ordinal));

        var unknown = new Stub(() => Answer(Good)) { OnGet = _ => Error(HttpStatusCode.NotFound, "not_found_error", "model: claude-nope") };
        Assert.False(await Make(unknown, model: "claude-nope").IsAvailableAsync());

        var gateway = new Stub(() => Answer(Good));
        Assert.True(await Make(gateway, baseUrl: "http://127.0.0.1:9").IsAvailableAsync());
        Assert.Empty(gateway.Gets);
    }

    // ---- reading by rule -------------------------------------------------------------------

    [Fact]
    public async Task ARefusedEquationIsTranscribedByClaudeAndReadByRule()
    {
        var stub = new Stub(() => Answer(@"h_t = \alpha_0 + \beta h_{t-1}"));
        string? spoken = await Make(stub).ReadAsync(Request());

        Assert.NotNull(spoken);
        Assert.Contains("h sub t", spoken, StringComparison.Ordinal);
        Assert.DoesNotContain("\\", spoken, StringComparison.Ordinal);
        var sent = Assert.Single(stub.Messages);
        Assert.Equal("low", Effort(sent));
        Assert.Contains("Transcribe the equation in the image as LaTeX", UserText(sent), StringComparison.Ordinal);
    }

    [Fact]
    public async Task WithoutAnImageNothingIsTranscribed()
    {
        var stub = new Stub(() => Answer("x"));
        Assert.Null(await Make(stub).ReadAsync(Request(images: 0)));
        Assert.Empty(stub.Messages);
    }

    // ---- verbalisation (the older reading modes) --------------------------------------------

    private static readonly MathVerbalisationRequest Expression =
        new(@"x^{2}", MathMode.NaturalFaithful, "Let x be the return.", "It is positive.", ["a0"], "en-GB");

    private const string ValidJson = """{"source_ids":["a0"],"expression":"x","faithful":"X squared.","explanation":null,"warnings":[]}""";

    [Fact]
    public async Task VerbalisationAsksForTheSchemaAsStructuredOutput_AndStillValidatesTheAnswer()
    {
        var stub = new Stub(() => Answer(ValidJson));
        var result = await new ClaudeMathVerbaliser(Settings(), new HttpClient(stub)).VerbaliseAsync(Expression);

        Assert.False(result.Rejected);
        Assert.Equal("X squared.", result.Faithful);
        var format = Assert.Single(stub.Messages).Body.RootElement.GetProperty("output_config").GetProperty("format");
        Assert.Equal("json_schema", format.GetProperty("type").GetString());
        // Structured output does not take string-length keywords; they are checked here instead.
        string schema = format.GetProperty("schema").GetRawText();
        Assert.Contains("\"faithful\"", schema, StringComparison.Ordinal);
        Assert.DoesNotContain("maxLength", schema, StringComparison.Ordinal);
        Assert.DoesNotContain("minLength", schema, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ARejectedVerbalisationIsAskedForAgainWithTheComplaint_ThenRefused()
    {
        // Cites a source that was not supplied: the schema allows it, the validator does not.
        const string wrongSource = """{"source_ids":["zz"],"expression":"x","faithful":"X squared.","explanation":null,"warnings":[]}""";
        var stub = new Stub(() => Answer(wrongSource));
        var result = await new ClaudeMathVerbaliser(Settings() with { MaxRetries = 1 }, new HttpClient(stub)).VerbaliseAsync(Expression);

        Assert.True(result.Rejected);
        Assert.Equal(2, stub.Messages.Count);
        string second = UserText(stub.Messages[1]);
        Assert.Contains("WHICH WAS REJECTED", second, StringComparison.Ordinal);
        Assert.Contains("zz", second, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AGatewayThatRefusesTheSchemaIsAskedAgainWithoutIt()
    {
        var stub = new Stub(
            () => Error(HttpStatusCode.BadRequest, "invalid_request_error", "output_config.format: json_schema is not supported"),
            () => Answer(ValidJson));
        var result = await new ClaudeMathVerbaliser(Settings(baseUrl: "http://127.0.0.1:9"), new HttpClient(stub)).VerbaliseAsync(Expression);

        Assert.False(result.Rejected);
        Assert.Equal(2, stub.Messages.Count);
        Assert.False(stub.Messages[1].Body.RootElement.TryGetProperty("output_config", out var o) && o.TryGetProperty("format", out _));
    }

    // ---- the vision model, through Core's client ---------------------------------------------

    private sealed class CountingVlm : IVlmService
    {
        public int Tests, Describes;

        public Task<VlmService.VlmResult> DescribeBlockAsync(byte[] pngBytes, VlmService.BlockAction action, VlmEndpointConfig endpoint,
            VlmService.PromptStyle style = VlmService.PromptStyle.Instruction, bool structuredOutput = false, CancellationToken ct = default)
        {
            Describes++;
            return Task.FromResult(new VlmService.VlmResult(@"$$x^{2}$$", null));
        }

        public Task<string?> TestConnectionAsync(VlmEndpointConfig endpoint, CancellationToken ct = default)
        {
            Tests++;
            return Task.FromResult<string?>(null);
        }
    }

    private sealed class NoCrops : IExtractionSource
    {
        public Task<ExtractionSnapshot> ExtractAsync(ExtractionRequest request, IProgress<ExtractionProgress>? progress = null, CancellationToken ct = default)
            => throw new NotSupportedException();
        public Task<ReadOnlyMemory<byte>?> RenderCropPngAsync(ExtractionIdentity extraction, int pageIndex, PageRect region, int targetPixelWidth, CancellationToken ct = default)
            => throw new NotSupportedException();
    }

    [Fact]
    public async Task TheTranscriberNeedsNoAddressForClaude_AndTestsTheConnectionOnce()
    {
        var vlm = new CountingVlm();
        var claude = new VlmMathTranscriber(vlm,
            new VlmEndpointConfig(null, ClaudeEndpointSettings.DefaultModel, null) { Provider = VlmProvider.Anthropic }, new NoCrops());

        Assert.True(await claude.IsAvailableAsync());
        Assert.True(await claude.IsAvailableAsync());
        // Claude's connection test is a small real request; one is enough for a whole document.
        Assert.Equal(1, vlm.Tests);
        var transcription = await claude.TranscribeAsync(new MathTranscriptionRequest(new byte[] { 0x89, 0x50, 0x4E, 0x47 }, "x2", "", "", ["a0"]));
        Assert.Equal("x^{2}", transcription.Representation);

        // An OpenAI-compatible endpoint has no default address: without one it is not configured.
        var noAddress = new VlmMathTranscriber(vlm, new VlmEndpointConfig(null, "m", null), new NoCrops());
        Assert.False(await noAddress.IsAvailableAsync());
        Assert.Equal(1, vlm.Tests);
    }

    // ---- the Settings dialogue's test --------------------------------------------------------

    [Fact]
    public async Task TheSettingsTestTellsAKnownModelAnUnknownOneARefusedKeyAndNothingAnsweringApart()
    {
        var ready = await ClaudeEndpointProbe.ProbeAsync(null, null, Key, new Stub());
        Assert.Equal(EndpointProbeStatus.Ready, ready.Status);
        Assert.Contains("claude-opus-5-5", ready.Models);
        Assert.Contains("api.anthropic.com", ready.Message, StringComparison.Ordinal);

        var unknown = await ClaudeEndpointProbe.ProbeAsync(null, "claude-nope", Key, new Stub
        {
            OnGet = path => path.EndsWith("/models", StringComparison.Ordinal)
                ? Json("""{"data":[{"id":"claude-opus-5-5","type":"model","display_name":"Claude Opus 5.5","created_at":"2026-01-01T00:00:00Z"}],"has_more":false,"first_id":"claude-opus-5-5","last_id":"claude-opus-5-5"}""")
                : Error(HttpStatusCode.NotFound, "not_found_error", "model: claude-nope"),
        });
        Assert.Equal(EndpointProbeStatus.ModelNotListed, unknown.Status);
        Assert.Contains("claude-opus-5-5", unknown.Message, StringComparison.Ordinal);

        var refused = await ClaudeEndpointProbe.ProbeAsync(null, null, Key, new Stub
        {
            OnGet = _ => Error(HttpStatusCode.Unauthorized, "authentication_error", "invalid x-api-key"),
        });
        Assert.Equal(EndpointProbeStatus.KeyRefused, refused.Status);
        Assert.DoesNotContain(Key, refused.Message, StringComparison.Ordinal);

        var silent = await ClaudeEndpointProbe.ProbeAsync(null, null, Key, new Throwing());
        Assert.Equal(EndpointProbeStatus.Unreachable, silent.Status);
        Assert.DoesNotContain(Key, silent.Message, StringComparison.Ordinal);

        var notAnAddress = await ClaudeEndpointProbe.ProbeAsync("not a url", null, Key, new Stub());
        Assert.Equal(EndpointProbeStatus.NotConfigured, notAnAddress.Status);
    }

    private sealed class Throwing : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
            => throw new HttpRequestException("Connection refused");
    }
}
