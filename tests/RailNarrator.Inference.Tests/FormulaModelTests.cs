using System.Security.Cryptography;
using RailNarrator.Domain;
using RailNarrator.Inference;
using SkiaSharp;
using Xunit;

namespace RailNarrator.Inference.Tests;

/// <summary>
/// The formula model's surroundings, which need no model file: the image it is given (Pillow's
/// arithmetic, byte for byte), its vocabulary as PaddleX writes it, the checks on its output, and
/// what is made of a transcription.
/// </summary>
public sealed class FormulaModelTests
{
    // ---- the image -----------------------------------------------------------------------

    /// <summary>A grey test image with a white border, the same in the script that made the digests.</summary>
    private static byte[] Synthetic(int w, int h)
    {
        var image = new byte[w * h];
        Array.Fill(image, (byte)255);
        for (int y = 4; y < h - 3; y++)
            for (int x = 5; x < w - 6; x++)
                if ((x / 7 + y / 5) % 3 == 0) image[y * w + x] = (byte)((x * 37 + y * 91 + x * y % 53 * 5) % 180);
        return image;
    }

    /// <summary>
    /// The digests are of the reference pipeline's 384 x 384 image (PaddleX's UniMERNetImgDecode
    /// under Pillow 11.3 and 12.0, which agree): wide, tall, tiny, a real crop's size, one that needs
    /// no margin of its own, and one a pixel off square.
    /// </summary>
    [Theory]
    [InlineData(300, 41, "2c1c5df0016dad97dd3c43b0bb84af0880cf49e9d3dfd393953403f728e7fc05")]
    [InlineData(97, 640, "91a6aeffd99b8240573ffec5fb483a647752f7b32adf8784140e27f3aa1da208")]
    [InlineData(40, 12, "b258accaf0cbff13a281baaf95eb9b007dc26f20a946091737594afd175d8ae8")]
    [InlineData(1025, 251, "4cb22946421c7262947933356b34bc7c19ea86d061f1891c99d732834aa21013")]
    [InlineData(64, 64, "b1d6de6332c427a31ae12dab5e7e593d1e7442d0e3c1ba882c5d3b511a3621d9")]
    [InlineData(700, 699, "d6db9ffc7fd66cbe57e86e0d2dcf02a336a3459b679cdf315537914111907f21")]
    public void TheModelsImageIsTheReferencePipelines_ByteForByte(int width, int height, string sha256)
    {
        var grey = Synthetic(width, height);
        var canvas = FormulaImage.Canvas([grey, grey, grey], width, height);

        Assert.NotNull(canvas);
        Assert.Equal(FormulaImage.Size * FormulaImage.Size, canvas[0].Length);
        Assert.Equal(sha256, Convert.ToHexString(SHA256.HashData(canvas[0])).ToLowerInvariant());
    }

    [Fact]
    public void AWideEquationIsFittedToTheWidth_CentredOnBlack()
    {
        var grey = Synthetic(300, 41);
        var canvas = FormulaImage.Canvas([grey, grey, grey], 300, 41)![0];

        // The top and bottom bands are padding; the equation's rows are in the middle.
        Assert.All(canvas.Take(FormulaImage.Size * 100), b => Assert.Equal(0, b));
        Assert.All(canvas.Skip(FormulaImage.Size * 284), b => Assert.Equal(0, b));
        Assert.Contains(canvas.Skip(FormulaImage.Size * 192).Take(FormulaImage.Size), b => b > 200);
    }

    [Fact]
    public void ABlankImageOrARule_IsNotAnEquation()
    {
        var white = new byte[100 * 40];
        Array.Fill(white, (byte)255);
        Assert.Null(FormulaImage.Canvas([white, white, white], 100, 40));

        // One dark row across a wide crop: the trimmed ink is hundreds of times wider than tall.
        var wide = new byte[1000 * 20];
        Array.Fill(wide, (byte)255);
        for (int x = 0; x < 1000; x++) wide[10 * 1000 + x] = 0;
        Assert.Null(FormulaImage.Canvas([wide, wide, wide], 1000, 20));
    }

    [Fact]
    public void TheTensorIsNormalisedAsTheModelWasTrained()
    {
        var grey = Synthetic(64, 64);
        float[]? tensor = FormulaImage.Prepare([grey, grey, grey], 64, 64);

        Assert.NotNull(tensor);
        Assert.Equal(FormulaImage.Size * FormulaImage.Size, tensor.Length);
        // White is (1 - 0.7931) / 0.1738 and the black padding (0 - 0.7931) / 0.1738.
        Assert.Equal(1.1904, tensor.Max(), 3);
        Assert.Equal(-4.5633, tensor.Min(), 3);
    }

    [Fact]
    public void APngIsReadOnWhite()
    {
        using var bitmap = new SKBitmap(new SKImageInfo(120, 40, SKColorType.Rgba8888, SKAlphaType.Unpremul));
        bitmap.Erase(SKColors.Transparent);
        // Two bars with a transparent gap between them.
        for (int x = 20; x < 100; x++) for (int y = 15; y < 25; y++) if (x is < 50 or >= 70) bitmap.SetPixel(x, y, SKColors.Black);
        using var data = bitmap.Encode(SKEncodedImageFormat.Png, 100);

        float[]? tensor = FormulaImage.Prepare(data.ToArray());

        // Transparent is paper: the gap is white between two bars of ink, not more ink.
        Assert.NotNull(tensor);
        Assert.Contains(tensor, v => v > 1.0f);
    }

    [Fact]
    public void ABoxReductionIsTheMeanOfEachBlock_PartialBlocksIncluded()
    {
        // 5 x 3, reduced by 2 x 2: two whole blocks and a half one across, a whole and a half down.
        byte[] source =
        [
            0, 2, 10, 20, 100,
            4, 6, 30, 40, 104,
            7, 9, 50, 51, 200,
        ];
        var reduced = PillowResample.Reduce(source, 5, 3, 2, 2, out int w, out int h);

        Assert.Equal((3, 2), (w, h));
        Assert.Equal([3, 25, 102, 8, 51, 200], reduced);   // (0+2+4+6+2)/4, …; halves round up
    }

    // ---- the vocabulary ------------------------------------------------------------------

    private const string Config = """
        Global:
          model_name: PP-FormulaNet_plus-M
        PostProcess:
          name: UniMERNetDecode
          character_dict:
            fast_tokenizer_file:
              added_tokens:
              - content: <s>
                id: 0
                lstrip: false
                special: true
              - content: <pad>
                id: 1
                special: true
              - content: </s>
                id: 2
                special: true
              - content: '[START_REF]'
                id: 3
                special: true
              decoder:
                type: ByteLevel
              model:
                merges:
                - Ġ t
                - i n
                vocab:
                  <s>: 0
                  <pad>: 1
                  </s>: 2
                  '[START_REF]': 3
                  \alpha: 4
                  Ġ: 5
                  '{': 6
                  '}': 7
                  _: 8
                  t: 9
                  'no': 10
                  ''': 11
                  "\x7F": 12
                  ': ': 13
                  Ã©: 14
                  "\"": 15
              normalizer: null
            tokenizer_config_file:
              added_tokens_decoder:
                '0':
                  content: <s>
                  special: true
        """;

    private static FormulaVocabulary Vocabulary()
    {
        // The real file has thousands of tokens; the reader refuses a vocabulary too small to be one.
        var text = new System.Text.StringBuilder();
        foreach (string line in Config.Split('\n'))
        {
            text.Append(line).Append('\n');
            if (line.Contains("\"\\\"\": 15", StringComparison.Ordinal))
                for (int i = 16; i < 1200; i++) text.Append("                  w").Append(i).Append(": ").Append(i).Append('\n');
        }
        return FormulaVocabulary.Read(new StringReader(text.ToString()));
    }

    [Fact]
    public void TheVocabularyIsReadFromTheModelsConfig_PlainAndQuotedTokens()
    {
        var vocabulary = Vocabulary();

        Assert.Equal(1200, vocabulary.Count);
        Assert.Equal(@"\alpha", vocabulary.Token(4));
        Assert.Equal("no", vocabulary.Token(10));      // quoted, or YAML would read a boolean
        Assert.Equal("'", vocabulary.Token(11));
        Assert.Equal("\u007F", vocabulary.Token(12));
        Assert.Equal(": ", vocabulary.Token(13));     // the id is what follows the last colon
        Assert.Equal("\"", vocabulary.Token(15));
    }

    [Fact]
    public void TokensDecodeToLatex_SpecialTokensDropped_BytesBackToText()
    {
        var vocabulary = Vocabulary();

        // <s> \alpha Ġ _ Ġ { Ġ t Ġ } </s>, as the model writes "\alpha _ { t }".
        Assert.Equal(@"\alpha _ { t }", vocabulary.Decode([0, 4, 5, 8, 5, 6, 5, 9, 5, 7, 2]));
        // "Ã©" is the two bytes of "é" in the tokeniser's byte alphabet.
        Assert.Equal("té", vocabulary.Decode([9, 14]));
        Assert.Equal("t", vocabulary.Decode([3, 9, 1, 5000]));
    }

    [Theory]
    [InlineData("                  \\alpha 4\n")]                  // not "token: id"
    [InlineData("                  \"\\q\": 4\n")]                  // an escape YAML does not have
    public void ALineTheReaderDoesNotUnderstandIsAnError_NotAGuess(string line)
    {
        string config = "        vocab:\n" + line;
        Assert.Throws<FormatException>(() => FormulaVocabulary.Read(new StringReader(config)));
    }

    [Fact]
    public void AFileWithoutAVocabulary_OrWithAGapInIt_IsRefused()
    {
        Assert.Throws<FormatException>(() => FormulaVocabulary.Read(new StringReader("Global:\n  model_name: x\n")));

        var gap = new System.Text.StringBuilder("        vocab:\n");
        for (int i = 0; i < 1500; i++) if (i != 700) gap.Append("          w").Append(i).Append(": ").Append(i).Append('\n');
        var error = Assert.Throws<FormatException>(() => FormulaVocabulary.Read(new StringReader(gap.ToString())));
        Assert.Contains("700", error.Message, StringComparison.Ordinal);
    }

    // ---- the output ----------------------------------------------------------------------

    [Fact]
    public void TheOutputIsTidied_UprightGreekAsGreek_LayoutCommandsDropped()
    {
        Assert.Equal(@"\partial \delta ^ { * }", FormulaOutput.Clean(@"\partial   \updelta ^ { * }"));
        Assert.Equal(@"x \uparrow y \upsilon", FormulaOutput.Clean(@"x \uparrow y \upsilon"));
        Assert.Equal(@"a = b", FormulaOutput.Clean(@"\boldmath a \ensuremath = b"));
        Assert.Equal(@"\emphasis", FormulaOutput.Clean(@"\emphasis"));
    }

    [Theory]
    [InlineData(@"\frac { a } { b }", true)]
    [InlineData(@"\{ a \}", true)]
    [InlineData(@"\frac { a } { b", false)]
    [InlineData(@"a } {", false)]
    public void BracesBalanceOrTheOutputIsNotAnEquation(string latex, bool balanced)
        => Assert.Equal(balanced, FormulaOutput.BracesBalance(latex));

    [Fact]
    public void AnOutputThatEndsInTheSameTokensOverAndOver_HasLostItsPlace()
    {
        // A real start, then "\quad 其他" for ever: what the model did with a neighbour's ink in its crop.
        var runaway = Enumerable.Range(10, 40).Select(i => (long)i).Concat(Enumerable.Repeat(new long[] { 7, 8 }, 60).SelectMany(p => p)).ToArray();
        Assert.True(FormulaOutput.RunsIntoRepetition(runaway));

        // A long equation repeats shapes ("\theta _ { 1 } , \theta _ { 2 } , …"), not tokens for this long.
        var equation = Enumerable.Range(0, 60).SelectMany(i => new long[] { 20, 21, 22, 100 + i, 23, 24 }).ToArray();
        Assert.False(FormulaOutput.RunsIntoRepetition(equation));
        Assert.False(FormulaOutput.RunsIntoRepetition([1, 1, 1, 1, 1]));
    }

    // ---- what is made of a transcription -------------------------------------------------

    private static MathInterpretationRequest Request(params RecognisedFormula[] formulas)
        => new(MathPassageKind.DisplayEquation, string.Join(" ", formulas.Select(f => f.TextLayer)), [], "", "", "en-GB", true, formulas);

    [Fact]
    public void ATranscriptionIsReadByRule()
    {
        var formula = new RecognisedFormula(@"\sigma _ { t } ^ { 2 } = h _ { t } g _ { t } ,", "σ2t = htgt,", [], false);
        Assert.Equal("sigma sub t squared equals h sub t g sub t", FormulaMathReader.Read(Request(formula)));
    }

    [Fact]
    public void ALoneBarIsGiven_AsInAConditionalExpectation()
    {
        var formula = new RecognisedFormula(@"y _ { t } = \mathsf { E } ( y _ { t } | \mathcal { F } _ { t - 1 } ) + \varepsilon _ { t } ,", "yt = E(yt |Ft−1) + εt ,", [], false);
        string? spoken = FormulaMathReader.Read(Request(formula));
        Assert.NotNull(spoken);
        Assert.Contains("y sub t given F sub t minus one", spoken, StringComparison.Ordinal);
    }

    [Fact]
    public void ATranscriptionThatDisagreesWithThePage_IsNotReadAloud()
    {
        var formula = new RecognisedFormula(@"\zeta_t + \xi_t", "ζt + ζt", ["The formula model's transcription has xi, which the page's text does not."], Disagrees: true);
        Assert.Null(FormulaMathReader.Read(Request(formula)));
        Assert.Null(FormulaMathReader.Read(Request()));
    }

    [Fact]
    public void ARunawayTranscriptionIsNotAReadingOfTheEquation()
    {
        string latex = string.Join(" + ", Enumerable.Repeat(@"\alpha _ { i } x _ { i }", 60));
        Assert.Null(FormulaMathReader.Read(Request(new RecognisedFormula(latex, "αixi", [], false))));
    }

    private sealed class FakeFormulas(FormulaRecognition read) : IFormulaRecogniser
    {
        public string Revision => "formulas/1";
        public Task<FormulaRecognition> RecogniseAsync(ReadOnlyMemory<byte> png, CancellationToken ct = default) => Task.FromResult(read);
    }

    [Fact]
    public async Task AsTheTranscriberOfTheOlderModes_ItChecksThePageToo()
    {
        var request = new MathTranscriptionRequest(new byte[] { 1 }, "εt = ζtσt ,", "", "", ["a1"]);

        var settled = await new FormulaModelTranscriber(new FakeFormulas(new(@"\varepsilon_t = \xi_t \sigma_t", null))).TranscribeAsync(request);
        Assert.True(settled.Succeeded);
        Assert.Equal(@"\varepsilon_t = \zeta_t \sigma_t", settled.Representation);
        Assert.Contains(settled.Warnings, w => w.Contains("unverified", StringComparison.Ordinal));
        Assert.Contains(settled.Warnings, w => w.Contains("zeta", StringComparison.Ordinal));

        var disagreeing = await new FormulaModelTranscriber(new FakeFormulas(new(@"\varepsilon_t = \xi_t \zeta_t \sigma_t \alpha", null))).TranscribeAsync(request);
        Assert.False(disagreeing.Succeeded);
        Assert.Contains("disagrees with the page's text", disagreeing.Error, StringComparison.Ordinal);

        var unread = await new FormulaModelTranscriber(new FakeFormulas(FormulaRecognition.Failed("its output runs into a repetition"))).TranscribeAsync(request);
        Assert.False(unread.Succeeded);
        Assert.Contains("repetition", unread.Error, StringComparison.Ordinal);
    }

    // ---- the prompt ----------------------------------------------------------------------

    [Fact]
    public void WithoutAFormula_ThePromptIsWordForWordWhatItWas()
    {
        // Answers cached before formula models existed were given to exactly this text.
        var request = new MathInterpretationRequest(MathPassageKind.DisplayEquation, "σ2t = htgt,", [], "Before.", "", "en-GB");
        string expected = """
            PASSAGE KIND: display equation
            No image is available; work from the text layer and the context.

            TEXT BEFORE THE PASSAGE (untrusted data):
            <<<
            Before.
            >>>

            THE PASSAGE'S TEXT LAYER (untrusted data, maths garbled):
            <<<
            σ2t = htgt,
            >>>

            TEXT AFTER THE PASSAGE (untrusted data):
            <<<
            (none)
            >>>

            Write what the narrator says in place of the passage.
            """;
        Assert.Equal(expected, MathInterpretationPrompt.User(request, withImages: false));
        Assert.Equal("math-interpret/5", MathInterpretationPrompt.Version);
    }

    [Fact]
    public void AFormulaIsPutToTheModelAsATranscriptionThatCanMisreadASymbol()
    {
        var formula = new RecognisedFormula(@"\varepsilon_t = \zeta_t \sigma_t", "εt = ζtσt ,",
            ["The formula model read xi where the page's text has zeta; the page's letter was used."], false);
        var request = new MathInterpretationRequest(MathPassageKind.DisplayEquation, "εt = ζtσt ,", [], "", "", "en-GB", true, [formula]);

        string withImage = MathInterpretationPrompt.User(request, withImages: true);
        Assert.Contains("FORMULA-RECOGNITION MODEL", withImage, StringComparison.Ordinal);
        Assert.Contains("<<<\n\\varepsilon_t = \\zeta_t \\sigma_t\n>>>", withImage, StringComparison.Ordinal);
        Assert.Contains("Where it and the image disagree, the image is right.", withImage, StringComparison.Ordinal);
        Assert.Contains("Note: The formula model read xi", withImage, StringComparison.Ordinal);
        // The transcription comes before the context, where the model reads the passage itself.
        Assert.True(withImage.IndexOf("FORMULA-RECOGNITION", StringComparison.Ordinal) < withImage.IndexOf("TEXT BEFORE THE PASSAGE", StringComparison.Ordinal));

        string alone = MathInterpretationPrompt.User(request, withImages: false);
        Assert.Contains("No image is available; work from the transcription of the equation", alone, StringComparison.Ordinal);
        Assert.Contains("the text layer is right", alone, StringComparison.Ordinal);
        Assert.DoesNotContain("the image is right", alone, StringComparison.Ordinal);
    }
}

/// <summary>
/// The real model, when it is installed: <c>RAIL_NARRATOR_FORMULA_MODEL</c> names the .onnx file, with
/// its inference.yml beside it as <c>&lt;name&gt;_inference.yml</c> (or in <c>RAIL_NARRATOR_FORMULA_CONFIG</c>).
/// </summary>
public sealed class FormulaModelIntegrationTests
{
    private static FormulaModelFiles Files()
    {
        string model = Environment.GetEnvironmentVariable("RAIL_NARRATOR_FORMULA_MODEL")!;
        string config = Environment.GetEnvironmentVariable("RAIL_NARRATOR_FORMULA_CONFIG")
            ?? Path.Combine(Path.GetDirectoryName(Path.GetFullPath(model))!, Path.GetFileNameWithoutExtension(model) + "_inference.yml");
        return new FormulaModelFiles(model, config);
    }

    private static byte[] Drawn(string text)
    {
        using var bitmap = new SKBitmap(new SKImageInfo(640, 160, SKColorType.Rgba8888, SKAlphaType.Opaque));
        using var canvas = new SKCanvas(bitmap);
        canvas.Clear(SKColors.White);
        using var font = new SKFont(SKTypeface.Default, 72);
        using var paint = new SKPaint { Color = SKColors.Black, IsAntialias = true };
        canvas.DrawText(text, 40, 105, SKTextAlign.Left, font, paint);
        using var data = bitmap.Encode(SKEncodedImageFormat.Png, 100);
        return data.ToArray();
    }

    [FormulaModelFact]
    public async Task TheRealModelReadsASimpleEquation_AndItsVocabularyIsWhole()
    {
        var files = Files();
        Assert.True(files.Exist, $"'{files.ModelPath}' and '{files.ConfigPath}' must both exist.");
        Assert.Equal(50_000, FormulaVocabulary.Load(files.ConfigPath).Count);

        using var recogniser = new PpFormulaNetRecogniser(files);
        var read = await recogniser.RecogniseAsync(Drawn("x = y + 2"));

        Assert.True(read.Succeeded, read.Problem);
        Assert.Equal("x=y+2", read.Latex!.Replace(" ", "", StringComparison.Ordinal));
        Assert.Equal("x equals y plus two", FormulaMathReader.Read(new MathInterpretationRequest(
            MathPassageKind.DisplayEquation, "x = y + 2", [], "", "", "en-GB", true, [new RecognisedFormula(read.Latex, "x = y + 2", [], false)])));
    }

    [FormulaModelFact]
    public async Task ABlankCropIsAProblem_NotAnEquation()
    {
        using var recogniser = new PpFormulaNetRecogniser(Files());
        var read = await recogniser.RecogniseAsync(Drawn(""));
        Assert.False(read.Succeeded);
        Assert.Contains("blank", read.Problem, StringComparison.Ordinal);
    }
}

public sealed class FormulaModelFactAttribute : FactAttribute
{
    public FormulaModelFactAttribute()
    {
        if (string.IsNullOrEmpty(Environment.GetEnvironmentVariable("RAIL_NARRATOR_FORMULA_MODEL")))
            Skip = "Set RAIL_NARRATOR_FORMULA_MODEL to PP-FormulaNet_plus-M.onnx (with PP-FormulaNet_plus-M_inference.yml beside it) to run the real formula model.";
    }
}
