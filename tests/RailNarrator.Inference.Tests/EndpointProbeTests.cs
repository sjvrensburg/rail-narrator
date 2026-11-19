using System.Net;
using RailNarrator.Inference;
using Xunit;

namespace RailNarrator.Inference.Tests;

/// <summary>The Settings dialogue's endpoint test says which part is wrong, and never shows the key.</summary>
public sealed class EndpointProbeTests
{
    private sealed class Answer(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        public HttpRequestMessage? Last;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Last = request;
            return Task.FromResult(respond(request));
        }
    }

    private static HttpResponseMessage Json(string body, HttpStatusCode code = HttpStatusCode.OK)
        => new(code) { Content = new StringContent(body) };

    [Fact]
    public async Task AModelTheEndpointLists_IsReady_AndTheKeyIsSentAsABearerToken()
    {
        var handler = new Answer(_ => Json("""{"object":"list","data":[{"id":"qwen4-max"},{"id":"other"}]}"""));
        var result = await EndpointProbe.ProbeAsync("https://example.test/compatible-mode/v1", "qwen4-max", "sk-secret", handler);

        Assert.Equal(EndpointProbeStatus.Ready, result.Status);
        Assert.Equal(["other", "qwen4-max"], result.Models);
        Assert.Equal("https://example.test/compatible-mode/v1/models", handler.Last!.RequestUri!.ToString());
        Assert.Equal("Bearer sk-secret", handler.Last.Headers.Authorization!.ToString());
        Assert.DoesNotContain("sk-secret", result.Message);
    }

    [Fact]
    public async Task AModelTheEndpointDoesNotList_IsReported_WithWhatItOffers()
    {
        var handler = new Answer(_ => Json("""{"data":[{"id":"a"},{"id":"b"}]}"""));
        var result = await EndpointProbe.ProbeAsync("http://127.0.0.1:8088/v1/", "c", null, handler);
        Assert.Equal(EndpointProbeStatus.ModelNotListed, result.Status);
        Assert.Contains("a, b", result.Message);
        Assert.Null(handler.Last!.Headers.Authorization);
    }

    [Theory]
    [InlineData(HttpStatusCode.Unauthorized, "sk-wrong", "refused the API key")]
    [InlineData(HttpStatusCode.Forbidden, null, "wants an API key")]
    public async Task ARefusedKey_IsReportedAsSuch(HttpStatusCode code, string? key, string expected)
    {
        var result = await EndpointProbe.ProbeAsync("https://example.test/v1", "m", key, new Answer(_ => Json("{}", code)));
        Assert.Equal(EndpointProbeStatus.KeyRefused, result.Status);
        Assert.Contains(expected, result.Message);
        Assert.DoesNotContain("sk-wrong", result.Message);
    }

    [Fact]
    public async Task AnAddressThatIsNotTheApiBase_IsReportedAsAnUnexpectedAnswer()
    {
        var html = await EndpointProbe.ProbeAsync("https://example.test", "m", null, new Answer(_ => Json("<html></html>")));
        Assert.Equal(EndpointProbeStatus.UnexpectedAnswer, html.Status);
        var missing = await EndpointProbe.ProbeAsync("https://example.test", "m", null, new Answer(_ => Json("", HttpStatusCode.NotFound)));
        Assert.Equal(EndpointProbeStatus.UnexpectedAnswer, missing.Status);
        Assert.Contains("/v1", missing.Message);
    }

    [Fact]
    public async Task NothingAnswering_IsUnreachable()
    {
        var result = await EndpointProbe.ProbeAsync("http://127.0.0.1:9/v1", "m", null,
            new Answer(_ => throw new HttpRequestException("Connection refused")));
        Assert.Equal(EndpointProbeStatus.Unreachable, result.Status);
        Assert.Contains("Connection refused", result.Message);
    }

    [Theory]
    [InlineData(null, "m")]
    [InlineData("https://example.test/v1", " ")]
    [InlineData("ftp://example.test", "m")]
    [InlineData("not an address", "m")]
    public async Task MissingOrMalformedValues_AreNotConfigured_AndNothingIsSent(string? url, string model)
    {
        var handler = new Answer(_ => throw new InvalidOperationException("nothing should be sent"));
        var result = await EndpointProbe.ProbeAsync(url, model, null, handler);
        Assert.Equal(EndpointProbeStatus.NotConfigured, result.Status);
        Assert.Null(handler.Last);
    }
}
