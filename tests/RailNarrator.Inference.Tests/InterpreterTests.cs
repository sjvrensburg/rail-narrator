using System.Net;
using System.Text;
using System.Text.Json;
using RailNarrator.Domain;
using RailNarrator.Inference;
using Xunit;

namespace RailNarrator.Inference.Tests;

/// <summary>
/// Spoken maths must carry no notation, and the client must get an answer out of endpoints that
/// differ in what they accept — without ever passing the input through as a fallback.
/// </summary>
public sealed class InterpreterTests
{
    private static MathInterpretationRequest Request(int images = 1) => new(
        MathPassageKind.DisplayEquation,
        "ht = α0 + Xqi=1 αiε∗2t−i + Xpj=1 βjht−j, (3)",
        Enumerable.Range(0, images).Select(_ => (ReadOnlyMemory<byte>)new byte[] { 0x89, 0x50, 0x4E, 0x47 }).ToList(),
        "To allow an asymmetric response of volatility, ht is modelled as the GJR-GARCH process.",
        "where the indicator is one when the shock is negative.",
        "en-GB");

    [Theory]
    [InlineData("It equals x_t plus one.", "x_t")]
    [InlineData("The return equals its mean = zero.", "=")]
    [InlineData("The weight α is positive.", "α")]
    [InlineData("The variance is \\sigma^2.", "\\")]
    [InlineData("The component ht is smooth.", "ht")]
    public void WrittenNotationAndGluedLetters_AreRejected(string spoken, string culprit)
    {
        var problems = SpokenMathsValidator.Problems(spoken, Request());
        Assert.NotEmpty(problems);
        Assert.Contains(problems, p => p.Contains(culprit, StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("The short-run component of the variance equals a constant plus a weighted sum of past squared shocks, with an extra weight when a shock was negative.")]
    [InlineData("I think a listener hears what the weights do: by the time a shock is a week old, its effect has halved.")]
    [InlineData("All the weights are non-negative, and the GJR-GARCH model is a special case when the deterministic component is one.")]
    // Notation spoken as words, beside its meaning or where the meaning is uncertain (prompt v4).
    [InlineData("The conditional variance, sigma t squared, rises after a negative shock.")]
    [InlineData("The short-run component h t equals a constant plus a weighted sum of past squared shocks.")]
    [InlineData("It has a finite moment of order two plus phi, for some positive phi.")]
    public void PlainSpeech_AndSpokenNotationBesideItsMeaning_AreAccepted(string spoken)
        => Assert.Empty(SpokenMathsValidator.Problems(spoken, Request()));

    [Fact]
    public void AnAnswerThatMostlyReadsNotation_IsRejected()
    {
        var problems = SpokenMathsValidator.Problems(
            "h t equals omega plus alpha times epsilon t minus one squared plus beta h t minus one.", Request());
        Assert.Contains(problems, p => p.Contains("mostly reads notation", StringComparison.Ordinal));
    }

    [Fact]
    public void AnAnswerFarLongerThanThePassage_IsRejected()
    {
        string essay = string.Join(" ", Enumerable.Repeat("The variance responds to past shocks and decays slowly over time.", 12));
        Assert.Contains(SpokenMathsValidator.Problems(essay, Request()), p => p.Contains("too long", StringComparison.Ordinal));
    }

    private sealed class Stub : HttpMessageHandler
    {
        private readonly Queue<Func<HttpResponseMessage>> _responses;
        public List<JsonDocument> Bodies { get; } = [];
        public Stub(params Func<HttpResponseMessage>[] responses) => _responses = new(responses);

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            if (request.Method == HttpMethod.Get) return Json("""{"data":[{"id":"m"}]}""");
            Bodies.Add(JsonDocument.Parse(await request.Content!.ReadAsStringAsync(ct)));
            return (_responses.Count > 1 ? _responses.Dequeue() : _responses.Peek())();
        }
    }

    private static HttpResponseMessage Json(string body, HttpStatusCode status = HttpStatusCode.OK)
        => new(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") };

    private static HttpResponseMessage Answer(string content, string finish = "stop")
        => Json(JsonSerializer.Serialize(new { choices = new[] { new { finish_reason = finish, message = new { role = "assistant", content } } } }));

    private static OpenAiCompatibleMathInterpreter Make(Stub stub, bool disableThinking = false, MathThinking? thinking = null)
        => new(new MathEndpointSettings("http://127.0.0.1:9/v1", "m", MaxRetries: 1, DisableThinking: disableThinking),
            new HttpClient(stub) { BaseAddress = new Uri("http://127.0.0.1:9/v1/") }, thinking);

    private static bool AskedNotToThink(JsonDocument body)
        => body.RootElement.TryGetProperty("chat_template_kwargs", out var k) && !k.GetProperty("enable_thinking").GetBoolean();

    [Fact]
    public async Task EquationsOnly_ThinksForEquationsAndLeadIns_NotForSentences()
    {
        var stub = new Stub(() => Answer(Good));
        var interpreter = Make(stub, thinking: MathThinking.EquationsOnly);
        var sentence = Request() with { Kind = MathPassageKind.SentenceWithMaths };

        await interpreter.InterpretAsync(Request());
        await interpreter.InterpretAsync(sentence);
        await interpreter.InterpretAsync(sentence with { HasDisplayEquation = true });

        Assert.False(AskedNotToThink(stub.Bodies[0]));
        Assert.True(AskedNotToThink(stub.Bodies[1]));
        Assert.False(AskedNotToThink(stub.Bodies[2]));
    }

    // ---- an endpoint that cannot be asked ------------------------------------------------

    private sealed class Refusing(HttpStatusCode status, string body) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
            => Task.FromResult(Json(body, status));
    }

    [Fact]
    public async Task ARefusedKey_IsTheReasonTheInterpreterIsUnavailable_WithoutTheServersWords()
    {
        // The reader's case: the wrong key for the endpoint. Some servers echo part of the key.
        var handler = new Refusing(HttpStatusCode.Unauthorized, """{"code":"InvalidApiKey","message":"Invalid API-key provided: sk-abc***"}""");
        using var interpreter = new OpenAiCompatibleMathInterpreter(new MathEndpointSettings("https://maths.example/v1", "m", ApiKey: "sk-abcdef"),
            new HttpClient(handler) { BaseAddress = new Uri("https://maths.example/v1/") });

        Assert.False(await interpreter.IsAvailableAsync());
        Assert.Equal("API key refused (401) by maths.example.", interpreter.UnavailableReason);
    }

    [Fact]
    public async Task AnExhaustedQuota_IsReportedAsTheEndpointsProblem()
    {
        var stub = new Stub(() => Json("""{"error":"You exceeded your current quota for key sk-abc***"}""", HttpStatusCode.TooManyRequests));

        var result = await Make(stub).InterpretAsync(Request());

        Assert.False(result.Succeeded);
        Assert.Equal("Out of quota or rate-limited (429) at 127.0.0.1.", result.EndpointProblem);
    }

    [Fact]
    public async Task AnAnswerThatDoesNotPass_IsNotTheEndpointsProblem()
    {
        var stub = new Stub(() => Answer("$\\sigma_t^2 = h_t g_t$"));

        var result = await Make(stub).InterpretAsync(Request());

        Assert.False(result.Succeeded);
        Assert.Null(result.EndpointProblem);
    }

    [Fact]
    public async Task AFailureFollowedByAnAnswer_IsNotReported()
    {
        var stub = new Stub(() => Json("""{"error":"busy"}""", HttpStatusCode.ServiceUnavailable), () => Answer(Good));

        var result = await Make(stub).InterpretAsync(Request());

        Assert.True(result.Succeeded);
        Assert.Null(result.EndpointProblem);
    }

    private const string Good = "The short-run component equals a constant plus weighted past squared shocks, and past values of itself.";

    [Fact]
    public async Task ThePassageImageIsSent()
    {
        var stub = new Stub(() => Answer(Good));
        var result = await Make(stub).InterpretAsync(Request());

        Assert.True(result.Succeeded);
        var content = stub.Bodies[0].RootElement.GetProperty("messages")[1].GetProperty("content");
        Assert.Equal("image_url", content[0].GetProperty("type").GetString());
        Assert.StartsWith("data:image/png;base64,", content[0].GetProperty("image_url").GetProperty("url").GetString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task AnEndpointThatRejectsImagesIsAskedAgainWithTextOnly()
    {
        var stub = new Stub(() => Json("""{"error":"image input is not supported"}""", HttpStatusCode.BadRequest), () => Answer(Good));
        var interpreter = Make(stub);

        var result = await interpreter.InterpretAsync(Request());
        var second = await interpreter.InterpretAsync(Request());

        Assert.True(result.Succeeded);
        Assert.Contains(result.Warnings, w => w.Contains("did not accept images", StringComparison.Ordinal));
        Assert.Equal(JsonValueKind.Array, stub.Bodies[0].RootElement.GetProperty("messages")[1].GetProperty("content").ValueKind);
        Assert.DoesNotContain("image_url", stub.Bodies[1].RootElement.GetRawText(), StringComparison.Ordinal);
        // Remembered: the next passage does not pay for the failed attempt.
        Assert.Equal(3, stub.Bodies.Count);
        Assert.True(second.Succeeded);
    }

    [Fact]
    public async Task NotationGetsOneRepairTurn()
    {
        var stub = new Stub(() => Answer("The component h_t equals α0 plus past shocks."), () => Answer(Good));

        var result = await Make(stub).InterpretAsync(Request());

        Assert.True(result.Succeeded);
        Assert.Equal(Good, result.Spoken);
        var messages = stub.Bodies[1].RootElement.GetProperty("messages");
        Assert.Equal(4, messages.GetArrayLength());
        Assert.Contains("cannot be spoken", messages[3].GetProperty("content").GetString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task NotationAfterTheRepairIsRefused_NeverPassedThrough()
    {
        var stub = new Stub(() => Answer("The component h_t equals α0."));

        var result = await Make(stub).InterpretAsync(Request());

        Assert.False(result.Succeeded);
        Assert.Null(result.Spoken);
        Assert.NotEmpty(result.Problems);
    }

    [Fact]
    public async Task AResponseCutOffAtTheTokenLimitIsAFailure()
    {
        var stub = new Stub(() => Answer("The short-run component equals a constant plus", finish: "length"));

        var result = await Make(stub, disableThinking: true).InterpretAsync(Request());

        Assert.False(result.Succeeded);
        Assert.Contains(result.Problems, p => p.Contains("cut off", StringComparison.Ordinal));
    }

    [Fact]
    public async Task ThinkingThatRunsOutOfTokensIsAskedAgainWithThinkingOff()
    {
        var stub = new Stub(() => Answer("", finish: "length"), () => Answer(Good));

        var result = await Make(stub).InterpretAsync(Request());

        Assert.True(result.Succeeded);
        Assert.Equal(Good, result.Spoken);
        Assert.False(stub.Bodies[0].RootElement.TryGetProperty("chat_template_kwargs", out _));
        Assert.False(stub.Bodies[1].RootElement.GetProperty("chat_template_kwargs").GetProperty("enable_thinking").GetBoolean());
        Assert.Contains(result.Warnings, w => w.Contains("thinking off", StringComparison.Ordinal));
    }

    [Fact]
    public async Task TheThinkingRetryStillCountsAsTheOnlyRepairBudget()
    {
        // Out of tokens thinking, then notation twice: one thinking-off answer and one repair, then refused.
        var stub = new Stub(() => Answer("", finish: "length"),
            () => Answer("The component h_t equals α0."), () => Answer("The component h_t equals α0."));

        var result = await Make(stub).InterpretAsync(Request());

        Assert.False(result.Succeeded);
        Assert.Equal(3, stub.Bodies.Count);
    }

    [Fact]
    public async Task TheThinkingSwitchIsDroppedWhenTheEndpointDoesNotKnowIt()
    {
        var stub = new Stub(
            () => Json("""{"error":{"message":"Unrecognized request argument supplied: chat_template_kwargs"}}""", HttpStatusCode.BadRequest),
            () => Answer(Good));

        var result = await Make(stub, disableThinking: true).InterpretAsync(Request(images: 0));

        Assert.True(result.Succeeded);
        Assert.False(stub.Bodies[0].RootElement.GetProperty("chat_template_kwargs").GetProperty("enable_thinking").GetBoolean());
        Assert.False(stub.Bodies[1].RootElement.TryGetProperty("chat_template_kwargs", out _));
    }

    [Fact]
    public async Task LeakedThinkingAndQuotesAreStripped()
    {
        var stub = new Stub(() => Answer("<think>The user wants words.</think>\n\"" + Good + "\""));

        var result = await Make(stub).InterpretAsync(Request());

        Assert.Equal(Good, result.Spoken);
    }

    [Fact]
    public async Task ARefusedEquationIsTranscribedAndReadByRule()
    {
        var stub = new Stub(() => Answer(@"h_t = \alpha_0 + \sum_{i=1}^{q} \alpha_i \varepsilon_{t-i}^{*2}"));
        IMathReader reader = Make(stub);

        string? spoken = await reader.ReadAsync(Request());

        Assert.Equal("h sub t equals alpha sub zero plus the sum from i equals one to q of alpha sub i epsilon sub t minus i, star squared", spoken);
        var body = stub.Bodies[0].RootElement;
        Assert.False(body.GetProperty("chat_template_kwargs").GetProperty("enable_thinking").GetBoolean());   // an easy task
        Assert.Equal("image_url", body.GetProperty("messages")[1].GetProperty("content")[0].GetProperty("type").GetString());
    }

    [Fact]
    public async Task ASentenceIsTranscribedWithItsMathsDelimited()
    {
        var stub = new Stub(() => Answer(@"where $\phi_t = \varepsilon_t / g_t^{1/2}$ is the standardised shock."));
        IMathReader reader = Make(stub);

        string? spoken = await reader.ReadAsync(Request() with { Kind = MathPassageKind.SentenceWithMaths });

        Assert.Equal("where phi sub t equals epsilon sub t over g sub t to the one half power is the standardised shock.", spoken);
    }

    [Fact]
    public async Task NeighbouringSentencesOnTheSameLinesAreNotRead()
    {
        IMathReader reader = Make(new Stub(() => Answer(
            @"to zero is discussed in Section 3. Also note that the values of $h_t$, $t = 1, \dots, T$, depend on the choice of $\delta_0$. " +
            @"The conditional variance $h_t$ in (3) is therefore a relative entity.")));
        var request = Request() with
        {
            Kind = MathPassageKind.SentenceWithMaths,
            TextLayer = "Also note that the values of ht , t = 1, . . . , T, depend on the choice of δ0.",
        };

        Assert.Equal("Also note that the values of h sub t, t equals one, dot dot dot, T, depend on the choice of delta sub zero.",
            await reader.ReadAsync(request));
    }

    [Fact]
    public async Task TheTailOfThePreviousSentenceIsNotRead()
    {
        IMathReader reader = Make(new Stub(() => Answer(@". Let $\theta_j$ be the true value.")));
        Assert.Equal("Let theta sub j be the true value.", await reader.ReadAsync(Request() with { Kind = MathPassageKind.SentenceWithMaths }));
    }

    [Theory]
    [InlineData("where $\\phi_t$ is $x")]            // unclosed: not half read
    [InlineData("The answer = 3 < 4")]                  // notation outside the delimiters
    public async Task ATranscriptionThatLeavesNotationIsNotRead(string transcription)
    {
        IMathReader reader = Make(new Stub(() => Answer(transcription)));
        Assert.Null(await reader.ReadAsync(Request() with { Kind = MathPassageKind.SentenceWithMaths }));
    }

    [Fact]
    public async Task WithoutAnImageNothingIsTranscribed()
    {
        var stub = new Stub(() => Answer("x"));
        IMathReader reader = Make(stub);
        Assert.Null(await reader.ReadAsync(Request(images: 0)));
        Assert.Empty(stub.Bodies);
    }
}
