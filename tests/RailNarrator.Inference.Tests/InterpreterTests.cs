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
    [InlineData("The short-run component h t equals a constant plus past shocks.", "h")]
    [InlineData("The variance sigma squared depends on past shocks.", "sigma")]
    [InlineData("It has a finite moment of order two plus phi for some positive phi.", "phi")]
    [InlineData("It equals x_t plus one.", "x_t")]
    [InlineData("The return equals its mean = zero.", "=")]
    [InlineData("The weight α is positive.", "α")]
    [InlineData("The component ht is smooth.", "ht")]
    [InlineData("The return at time t minus one is known.", "t")]
    public void NotationIsRejected(string spoken, string culprit)
    {
        var problems = SpokenMathsValidator.Problems(spoken, Request());
        Assert.NotEmpty(problems);
        Assert.Contains(problems, p => p.Contains(culprit, StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("The short-run component of the variance equals a constant plus a weighted sum of past squared shocks, with an extra weight when a shock was negative.")]
    [InlineData("I think a listener hears what the weights do: by the time a shock is a week old, its effect has halved.")]
    [InlineData("All the weights are non-negative, and the GJR-GARCH model is a special case when the deterministic component is one.")]
    public void PlainSpeechIsAccepted(string spoken)
        => Assert.Empty(SpokenMathsValidator.Problems(spoken, Request()));

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

    private static OpenAiCompatibleMathInterpreter Make(Stub stub, bool disableThinking = false)
        => new(new MathEndpointSettings("http://127.0.0.1:9/v1", "m", MaxRetries: 1, DisableThinking: disableThinking),
            new HttpClient(stub) { BaseAddress = new Uri("http://127.0.0.1:9/v1/") });

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
        var stub = new Stub(() => Answer("The component h t equals alpha zero plus past shocks."), () => Answer(Good));

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
        var stub = new Stub(() => Answer("The component h t equals alpha zero."));

        var result = await Make(stub).InterpretAsync(Request());

        Assert.False(result.Succeeded);
        Assert.Null(result.Spoken);
        Assert.NotEmpty(result.Problems);
    }

    [Fact]
    public async Task AResponseCutOffAtTheTokenLimitIsAFailure()
    {
        var stub = new Stub(() => Answer("The short-run component equals a constant plus", finish: "length"));

        var result = await Make(stub).InterpretAsync(Request());

        Assert.False(result.Succeeded);
        Assert.Contains(result.Problems, p => p.Contains("cut off", StringComparison.Ordinal));
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
}
