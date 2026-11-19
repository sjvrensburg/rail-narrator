using RailNarrator.Domain;
using RailNarrator.Inference;
using RailReader.Core.Models;
using RailReader.Core.Services;
using RailReader.Core.Vlm.Anthropic;
using SkiaSharp;
using Xunit;
using Xunit.Abstractions;

namespace RailNarrator.Inference.Tests;

/// <summary>
/// Claude through Anthropic's real API. Each test is a few small requests and costs money, so they
/// run only when asked for: <c>RN_CLAUDE_INTEGRATION=1</c>, with credentials the Anthropic SDK finds
/// itself (<c>ANTHROPIC_API_KEY</c>, or a profile from <c>ant auth login</c>).
/// <c>RN_CLAUDE_MODEL</c> names a model other than the default.
/// </summary>
[Collection("Claude")]
public sealed class ClaudeIntegrationTests(ITestOutputHelper output)
{
    private static ClaudeEndpointSettings Settings => new(Environment.GetEnvironmentVariable("RN_CLAUDE_MODEL"));

    /// <summary>An equation as a page crop would show it: black text on white.</summary>
    private static ReadOnlyMemory<byte> EquationPng()
    {
        using var surface = SKSurface.Create(new SKImageInfo(640, 120));
        surface.Canvas.Clear(SKColors.White);
        using var font = new SKFont(SKTypeface.Default, 44);
        using var paint = new SKPaint { Color = SKColors.Black, IsAntialias = true };
        surface.Canvas.DrawText("y = a + b x + e", 40, 76, font, paint);
        using var image = surface.Snapshot();
        using var data = image.Encode(SKEncodedImageFormat.Png, 100);
        return data.ToArray();
    }

    private static MathInterpretationRequest Request() => new(
        MathPassageKind.DisplayEquation,
        "y = a + b x + e (1)",
        [EquationPng()],
        "We model the response y as a linear function of the regressor x, with intercept a, slope b and an error e.",
        "The error has mean zero.",
        "en-GB");

    [ClaudeIntegrationFact]
    public async Task OnTheRealApi_ClaudeInterpretsAnEquationFromItsImage_AndTheAnswerCanBeSpoken()
    {
        var interpreter = new ClaudeMathInterpreter(Settings);
        Assert.True(await interpreter.IsAvailableAsync(), "Claude was not available: check the credentials and the model name.");

        var result = await interpreter.InterpretAsync(Request());

        Assert.True(result.Succeeded, string.Join(" ", result.Problems));
        output.WriteLine("Interpreted: " + result.Spoken);
        Assert.Empty(SpokenMathsValidator.Problems(result.Spoken, Request()));
        // The request went as it was built: effort and the refusal fallback were both accepted.
        Assert.False(interpreter.AnOptionalFieldWasRefused);
    }

    [ClaudeIntegrationFact]
    public async Task OnTheRealApi_ClaudeTranscribesAnEquationThatIsThenReadByRule()
    {
        string? spoken = await new ClaudeMathInterpreter(Settings).ReadAsync(Request());

        Assert.NotNull(spoken);
        output.WriteLine("Read by rule: " + spoken);
        Assert.Contains("equals", spoken, StringComparison.OrdinalIgnoreCase);
    }

    [ClaudeIntegrationFact]
    public async Task OnTheRealApi_ClaudeVerbalisesAnExpression_AndTheSchemaIsAcceptedAsSent()
    {
        var verbaliser = new ClaudeMathVerbaliser(Settings);

        var result = await verbaliser.VerbaliseAsync(new MathVerbalisationRequest(
            @"\hat{\beta} = (X^{\top} X)^{-1} X^{\top} y", MathMode.NaturalFaithful,
            "The ordinary least squares estimator is", "where X is the matrix of regressors.", ["a0"], "en-GB"));

        Assert.False(result.Rejected, string.Join(" ", result.RejectionReasons));
        output.WriteLine("Verbalised: " + result.Faithful);
        // Structured output took the schema: it was not dropped and asked for again without.
        Assert.False(verbaliser.AnOptionalFieldWasRefused);
    }

    [ClaudeIntegrationFact]
    public async Task OnTheRealApi_TheSettingsTestFindsTheModel()
    {
        var probe = await ClaudeEndpointProbe.ProbeAsync(null, Settings.Model, null);
        Assert.True(probe.IsReady, probe.Message);
    }

    [ClaudeIntegrationFact]
    public async Task OnTheRealApi_CoresClaudeClientTranscribesACropForTheOlderReadingModes()
    {
        var transcriber = new VlmMathTranscriber(new AnthropicVlmClient(),
            new VlmEndpointConfig(null, Settings.EffectiveModel, null) { Provider = VlmProvider.Anthropic }, new NoCrops());
        Assert.True(await transcriber.IsAvailableAsync());

        var result = await transcriber.TranscribeAsync(new MathTranscriptionRequest(EquationPng(), "y = a + b x + e", "", "", ["a0"]));

        Assert.True(result.Succeeded, result.Error);
        output.WriteLine("Transcribed: " + result.Representation);
        Assert.Contains("=", result.Representation, StringComparison.Ordinal);
    }

    private sealed class NoCrops : IExtractionSource
    {
        public Task<ExtractionSnapshot> ExtractAsync(ExtractionRequest request, IProgress<ExtractionProgress>? progress = null, CancellationToken ct = default)
            => throw new NotSupportedException();
        public Task<ReadOnlyMemory<byte>?> RenderCropPngAsync(ExtractionIdentity extraction, int pageIndex, PageRect region, int targetPixelWidth, CancellationToken ct = default)
            => throw new NotSupportedException();
    }
}

public sealed class ClaudeIntegrationFactAttribute : FactAttribute
{
    public ClaudeIntegrationFactAttribute()
    {
        if (Environment.GetEnvironmentVariable("RN_CLAUDE_INTEGRATION") != "1")
            Skip = "Set RN_CLAUDE_INTEGRATION=1, with ANTHROPIC_API_KEY or an 'ant auth login' profile, to ask Claude through Anthropic's real API (costs a few small requests).";
    }
}
