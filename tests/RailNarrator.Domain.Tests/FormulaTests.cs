using RailNarrator.Domain;
using RailNarrator.TestFixtures;
using Xunit;

namespace RailNarrator.Domain.Tests;

/// <summary>
/// A formula model's LaTeX against the page's text layer: the two make different mistakes, so the
/// Greek letters each names are compared, a one-for-one slip is settled by the page, and anything
/// else is reported and left standing.
/// </summary>
public sealed class FormulaTextLayerCheckTests
{
    [Fact]
    public void LettersThatMatchLeaveTheTranscriptionAlone()
    {
        var formula = FormulaTextLayerCheck.Reconcile(@"\sigma _ { t } ^ { 2 } = h _ { t } g _ { t } ,", "σ2t = htgt,");
        Assert.False(formula.Disagrees);
        Assert.Empty(formula.Notes);
        Assert.Equal(@"\sigma _ { t } ^ { 2 } = h _ { t } g _ { t } ,", formula.Latex);
    }

    [Fact]
    public void OneLetterTakenForAnother_IsSettledByThePage_AndSaidSo()
    {
        // PP-FormulaNet_plus-M on the GARCH paper's equation (2): zeta read as xi.
        var formula = FormulaTextLayerCheck.Reconcile(@"\varepsilon _ { t } = \xi _ { t } \sigma _ { t } ,", "εt = ζtσt ,");
        Assert.False(formula.Disagrees);
        Assert.Equal(@"\varepsilon _ { t } = \zeta _ { t } \sigma _ { t } ,", formula.Latex);
        string note = Assert.Single(formula.Notes);
        Assert.Contains("xi", note, StringComparison.Ordinal);
        Assert.Contains("zeta", note, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(@"\varepsilon_t + \varphi_1", "ϵt + ϕ1")]      // the variant forms of a font
    [InlineData(@"\epsilon_t + \phi_1", "εt + φ1")]
    [InlineData(@"\theta + \mu", "𝜃 + µ")]                      // mathematical italic, and the micro sign
    [InlineData(@"\Delta y_t", "∆yt")]                          // the increment sign a font uses for capital delta
    public void VariantFormsAreTheSameLetter(string latex, string text)
    {
        var formula = FormulaTextLayerCheck.Reconcile(latex, text);
        Assert.False(formula.Disagrees);
        Assert.Empty(formula.Notes);
    }

    [Fact]
    public void ComputerModernsEpsilon_IsEpsilon()
    {
        // pdfTeX's text layer gives \epsilon as "ǫ" (o with ogonek); eight equations of one paper
        // were held back for "having epsilon, which the page's text does not".
        var formula = FormulaTextLayerCheck.Reconcile(@"\epsilon _ { n } = C _ { 2 } \kappa ^ { - 1 }", "ǫn = C2κ−1");

        Assert.False(formula.Disagrees);
        Assert.Empty(formula.Notes);
    }

    [Fact]
    public void ATextLayerWithNoGreekLetterSaysNothingAboutThem()
    {
        var formula = FormulaTextLayerCheck.Reconcile(@"\alpha + \beta", "a + b");
        Assert.False(formula.Disagrees);
        Assert.Empty(formula.Notes);
    }

    [Fact]
    public void ASumOrProductSignInTheTextIsNotALetter()
    {
        var formula = FormulaTextLayerCheck.Reconcile(@"\sum _ { i } \alpha _ { i } \prod _ { k } x _ { k }", "Σi αi Πk xk");
        Assert.False(formula.Disagrees);
        Assert.Empty(formula.Notes);
    }

    [Fact]
    public void AnyOtherDisagreementIsReported_NotRepaired()
    {
        // Some zetas read rightly and one as xi: the sets no longer differ one for one.
        var formula = FormulaTextLayerCheck.Reconcile(@"\varepsilon_t = \zeta_t h_t , \{ \xi_t \} \sim N", "εt = ζtht , {ζt} ∼ N");
        Assert.True(formula.Disagrees);
        Assert.Equal(@"\varepsilon_t = \zeta_t h_t , \{ \xi_t \} \sim N", formula.Latex);
        Assert.Contains(formula.Notes, n => n.Contains("xi", StringComparison.Ordinal) && n.Contains("page's text does not", StringComparison.Ordinal));
    }

    [Fact]
    public void ALetterThePageHasAndTheTranscriptionLacks_IsADisagreement()
    {
        var formula = FormulaTextLayerCheck.Reconcile(@"\frac { \partial f } { \partial x }", "∂f ∂δ");
        Assert.True(formula.Disagrees);
        Assert.Contains(formula.Notes, n => n.Contains("delta", StringComparison.Ordinal));
    }

    [Fact]
    public void ATextLayerWithUnmappedGlyphs_CannotShowALetterIsAbsent()
    {
        // The control character is a glyph the font gave no mapping for: it may be the xi.
        var formula = FormulaTextLayerCheck.Reconcile(@"\zeta_t + \xi_t", "ζt + \u0003t");
        Assert.False(formula.Disagrees);
        Assert.Empty(formula.Notes);
    }

    [Fact]
    public void ANumberThePageHasAndTheTranscriptionLeftOut_IsADisagreement()
    {
        // The GARCH paper's estimated equation: the standard error under the coefficient was dropped.
        var formula = FormulaTextLayerCheck.Reconcile(
            @"\hat { g } _ { t } = \left\{ 3 . 9 5 8 - 3 . 4 9 0 5 G _ { 1 } ( t ^ { * } ; \hat { \gamma } _ { 1 } , \hat { \mathbf { c } } _ { 1 } ) \right\} ,",
            "gˆt = 3.958 −3.4905 (0.0161) G1(t ∗ ; γˆ1, cˆ1) ,");
        Assert.True(formula.Disagrees);
        string note = Assert.Single(formula.Notes);
        Assert.Contains("0.0161", note, StringComparison.Ordinal);
        Assert.DoesNotContain("3.958", note, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(@"h _ { t } = 0 . 1 0 + 0 . 8 5 h _ { t - 1 }", "ht = 0.10 + 0.85ht−1")]
    [InlineData(@"0.85 ^ { 2 } x", "0.852x")]            // a superscript's digit run together with the number
    [InlineData(@"x _ { 1 } + 2 y", "x1 + 2y")]            // whole numbers are indices as often as values: not compared
    [InlineData(@"0.5 + 1.25", "0.5")]                     // the text layer may have lost one: not held against the transcription
    public void NumbersThatAreAllThereAgree(string latex, string text)
    {
        Assert.Empty(FormulaTextLayerCheck.NumbersMissingFrom(latex, text));
        Assert.False(FormulaTextLayerCheck.Reconcile(latex, text).Disagrees);
    }

    [Fact]
    public void ASettledLetterAndAMissingNumberAreBothReported()
    {
        var formula = FormulaTextLayerCheck.Reconcile(@"\xi_t = 0.5", "ζt = 0.5 (0.01)");
        Assert.True(formula.Disagrees);
        Assert.Contains(@"\zeta", formula.Latex, StringComparison.Ordinal);
        Assert.Equal(2, formula.Notes.Count);
    }

    [Fact]
    public void CommandsThatOnlyStartLikeALetterAreNotLetters()
    {
        Assert.Empty(FormulaTextLayerCheck.LettersInLatex(@"\times \partial \mathrm{pi} \eta_t".Replace(@"\eta_t", "", StringComparison.Ordinal)));
        Assert.Equal(['η'], FormulaTextLayerCheck.LettersInLatex(@"\eta_t \beth"));
    }
}

/// <summary>
/// The resolver with a formula model: what the interpreter is given, the crop the formula model
/// reads, and the reading by rule when there is no interpretation.
/// </summary>
public sealed class FormulaResolverTests
{
    private sealed class FakeInterpreter(Func<MathInterpretationRequest, MathInterpretationResult> answer) : IMathInterpreter
    {
        public List<MathInterpretationRequest> Requests { get; } = [];
        public string Revision => "fake/1";
        public Task<bool> IsAvailableAsync(CancellationToken ct = default) => Task.FromResult(true);

        public Task<MathInterpretationResult> InterpretAsync(MathInterpretationRequest request, CancellationToken ct = default)
        {
            Requests.Add(request);
            return Task.FromResult(answer(request));
        }

        public static FakeInterpreter Saying(string words) => new(_ => new MathInterpretationResult(words, [], []));
        public static FakeInterpreter Refusing() => new(_ => MathInterpretationResult.Reject("It still contains notation."));
    }

    private sealed class FakeReader(Func<MathInterpretationRequest, string?> words) : IMathReader
    {
        public List<MathInterpretationRequest> Requests { get; } = [];
        public string Revision => "reader/1";
        public Task<string?> ReadAsync(MathInterpretationRequest request, CancellationToken ct = default)
        {
            Requests.Add(request);
            return Task.FromResult(words(request));
        }
    }

    private sealed class FakeFormulas(Func<FormulaRecognition> read) : IFormulaRecogniser
    {
        public int Calls;
        public string Revision => "formulas/1";
        public Task<FormulaRecognition> RecogniseAsync(ReadOnlyMemory<byte> png, CancellationToken ct = default)
        {
            Calls++;
            return Task.FromResult(read());
        }

        public static FakeFormulas Reading(string latex) => new(() => new FormulaRecognition(latex, null));
    }

    private sealed class Crops : IExtractionSource
    {
        public List<(PageRect Region, int Width)> Rendered { get; } = [];
        public Task<ExtractionSnapshot> ExtractAsync(ExtractionRequest request, IProgress<ExtractionProgress>? progress = null, CancellationToken ct = default)
            => throw new NotSupportedException();
        public Task<ReadOnlyMemory<byte>?> RenderCropPngAsync(ExtractionIdentity extraction, int pageIndex, PageRect region, int targetPixelWidth, CancellationToken ct = default)
        {
            Rendered.Add((region, targetPixelWidth));
            // Distinct bytes per region, so a cache keyed by the crop tells them apart.
            return Task.FromResult<ReadOnlyMemory<byte>?>(new byte[] { 1, 2, (byte)region.Top, (byte)region.Left, (byte)(targetPixelWidth % 251) });
        }
    }

    private sealed class MemoryCache : ITextCache
    {
        public Dictionary<string, string> Values { get; } = [];
        public Task<string?> TryGetAsync(string key, CancellationToken ct = default)
            => Task.FromResult(Values.TryGetValue(key, out var v) ? v : null);
        public Task PutAsync(string key, string value, CancellationToken ct = default) { Values[key] = value; return Task.CompletedTask; }
    }

    private static readonly NarrationPlannerOptions Interpret = new(MathMode: MathMode.Interpret, InterpretMaths: true);
    private const string Latex = @"\sigma _ { t } ^ { 2 } = h _ { t } g _ { t } ,";
    private const string Words = "sigma sub t squared equals h sub t g sub t";

    private static readonly PageRect EquationRect = new(200, 140, 400, 160);

    /// <summary>A sentence with maths (segment 0), a display equation (1) and a sentence (2).</summary>
    private static Task<NarrationPlan> PlanAsync()
    {
        var a1 = SyntheticNarration.Anchor("a1", 0, new PageRect(72, 116, 500, 128));
        var a2 = SyntheticNarration.Anchor("a2", 0, EquationRect);
        var a3 = SyntheticNarration.Anchor("a3", 0, new PageRect(72, 166, 500, 178));
        IReadOnlyList<ReadingUnit> units =
        [
            SyntheticNarration.ProseUnit("u1", "The variance σ 2t is assumed to have a multiplicative decomposition.", a1) with { ContainsMaths = true },
            new ReadingUnit("u2", ReadingUnitKind.DisplayMath, [a2], "σ2t = htgt,", null, "", "", [], 0),
            SyntheticNarration.ProseUnit("u3", "where g is a positive-valued deterministic component.", a3),
        ];
        return PlanOf(units);
    }

    /// <summary>A sentence leading into its equation: one passage (segment 0).</summary>
    private static Task<NarrationPlan> LeadInPlanAsync()
    {
        var b0 = SyntheticNarration.Anchor("b0", 0, new PageRect(72, 100, 500, 112));
        var b1 = SyntheticNarration.Anchor("b1", 0, EquationRect);
        IReadOnlyList<ReadingUnit> units =
        [
            SyntheticNarration.ProseUnit("v0", "Let the variance be given as", b0),
            new ReadingUnit("v1", ReadingUnitKind.DisplayMath, [b1], "σ2t = htgt,", null, "", "", [], 0),
        ];
        return PlanOf(units);
    }

    private static Task<NarrationPlan> PlanOf(IReadOnlyList<ReadingUnit> units)
        => new NarrationPlanner(options: Interpret).BuildPlanAsync(
            new ExtractionSnapshot(new ExtractionIdentity("deadbeefcafebabe", 1), CoordinateFrameId.DisplayedPagePoints(), ExtractionOptions.ModelFree, []),
            units, NarrationFilterSettings.Default);

    [Fact]
    public async Task WithBoth_TheInterpreterIsGivenTheImageAndTheTranscription()
    {
        var plan = await PlanAsync();
        var interpreter = FakeInterpreter.Saying("The variance is the product of two components.");
        var crops = new Crops();
        var resolver = new MathInterpretationResolver(interpreter, crops, options: Interpret,
            formulas: FakeFormulas.Reading(Latex), formulaReader: new FakeReader(_ => Words), evidence: MathEvidence.Both);

        var resolved = await resolver.ResolveAsync(plan, plan.Segments[1]);

        var request = Assert.Single(interpreter.Requests);
        Assert.Single(request.Images);
        var formula = Assert.Single(request.Formulas);
        Assert.Equal(Latex, formula.Latex);
        Assert.Equal("σ2t = htgt,", formula.TextLayer);
        Assert.Equal(ScriptProvenance.ModelGenerated, resolved.Provenance);
        Assert.Contains(resolved.Warnings, w => w.Contains("page image, a formula model's transcription", StringComparison.Ordinal));
    }

    [Fact]
    public async Task TheFormulaModelReadsATightCropOfTheEquation_NotTheInterpretersPaddedOne()
    {
        var plan = await PlanAsync();
        var crops = new Crops();
        var resolver = new MathInterpretationResolver(FakeInterpreter.Saying("The variance is a product."), crops, options: Interpret,
            formulas: FakeFormulas.Reading(Latex), formulaReader: new FakeReader(_ => Words), evidence: MathEvidence.Both);

        await resolver.ResolveAsync(plan, plan.Segments[1]);

        // No padding above or below: a formula model transcribes the edge of a neighbouring line too.
        var forFormula = Assert.Single(crops.Rendered, c => c.Width == MathInterpretationResolver.FormulaCropPixelWidth);
        Assert.Equal(EquationRect.Top, forFormula.Region.Top);
        Assert.Equal(EquationRect.Bottom, forFormula.Region.Bottom);
        Assert.True(forFormula.Region.Left < EquationRect.Left && forFormula.Region.Right > EquationRect.Right);
        var forInterpreter = Assert.Single(crops.Rendered, c => c.Width != MathInterpretationResolver.FormulaCropPixelWidth);
        Assert.True(forInterpreter.Region.Top < EquationRect.Top && forInterpreter.Region.Bottom > EquationRect.Bottom);
    }

    [Fact]
    public async Task WithTheTranscriptionAlone_NoImageIsSent_EvenForASentence()
    {
        var plan = await PlanAsync();
        var interpreter = FakeInterpreter.Saying("The variance is a product.");
        var crops = new Crops();
        var resolver = new MathInterpretationResolver(interpreter, crops, options: Interpret,
            formulas: FakeFormulas.Reading(Latex), formulaReader: new FakeReader(_ => Words), evidence: MathEvidence.Formula);

        var equation = await resolver.ResolveAsync(plan, plan.Segments[1]);
        await resolver.ResolveAsync(plan, plan.Segments[0]);

        Assert.Empty(interpreter.Requests[0].Images);
        Assert.Equal(Latex, Assert.Single(interpreter.Requests[0].Formulas).Latex);
        Assert.Contains(equation.Warnings, w => w.Contains("from a formula model's transcription", StringComparison.Ordinal));
        // A sentence with maths in its line has no formula to transcribe: its text and context only.
        Assert.Empty(interpreter.Requests[1].Images);
        Assert.Empty(interpreter.Requests[1].Formulas);
        Assert.All(crops.Rendered, c => Assert.Equal(MathInterpretationResolver.FormulaCropPixelWidth, c.Width));
    }

    [Fact]
    public async Task WithTheImageAlone_TheFormulaModelIsNotAsked()
    {
        var plan = await PlanAsync();
        var interpreter = FakeInterpreter.Saying("The variance is a product.");
        var formulas = FakeFormulas.Reading(Latex);
        var resolver = new MathInterpretationResolver(interpreter, new Crops(), options: Interpret,
            formulas: formulas, formulaReader: new FakeReader(_ => Words), evidence: MathEvidence.Image);

        var resolved = await resolver.ResolveAsync(plan, plan.Segments[1]);

        Assert.Equal(0, formulas.Calls);
        Assert.Empty(interpreter.Requests[0].Formulas);
        Assert.Single(interpreter.Requests[0].Images);
        Assert.Contains(resolved.Warnings, w => w.StartsWith("Interpreted by a language model from the page image and its context", StringComparison.Ordinal));
    }

    [Fact]
    public async Task ARequestWithoutAFormulaIsKeyedAsItWasBeforeFormulaModels()
    {
        var plan = await PlanAsync();
        var cache = new MemoryCache();
        var resolver = new MathInterpretationResolver(FakeInterpreter.Saying("The variance is a product."), cache: cache, options: Interpret,
            formulas: FakeFormulas.Reading(Latex), formulaReader: new FakeReader(_ => Words), evidence: MathEvidence.Both);

        // A sentence has no formula, so its answer must stay where an earlier version cached it.
        await resolver.ResolveAsync(plan, plan.Segments[0]);

        string expected = CacheKey.Sha256Hex(string.Join("␟", "interpret/1", "fake/1", nameof(MathPassageKind.SentenceWithMaths),
            "The variance σ 2t is assumed to have a multiplicative decomposition.", "",
            "σ2t = htgt, where g is a positive-valued deterministic component.", "en-GB", ""));
        Assert.Equal([expected], cache.Values.Keys);
    }

    [Fact]
    public async Task ATranscriptionIsMadeOnce_ThenServedFromTheCache_AndRegenerateAsksAgain()
    {
        var plan = await PlanAsync();
        var cache = new MemoryCache();
        var formulas = FakeFormulas.Reading(Latex);
        MathInterpretationResolver Resolver() => new(FakeInterpreter.Refusing(), new Crops(), cache, Interpret,
            formulas: formulas, formulaReader: new FakeReader(_ => Words), evidence: MathEvidence.Both);

        var first = await Resolver().ResolveAsync(plan, plan.Segments[1]);
        await Resolver().ResolveAsync(plan, plan.Segments[1]);
        Assert.Equal(1, formulas.Calls);

        await Resolver().RefreshAsync(plan, first);
        Assert.Equal(2, formulas.Calls);
    }

    [Fact]
    public async Task AnEquationTheFormulaModelCannotRead_IsLeftOut_WithAWarning()
    {
        var plan = await PlanAsync();
        var interpreter = FakeInterpreter.Saying("The variance is a product.");
        var resolver = new MathInterpretationResolver(interpreter, new Crops(), options: Interpret,
            formulas: new FakeFormulas(() => FormulaRecognition.Failed("it did not finish within its limit of 1536 tokens")),
            formulaReader: new FakeReader(_ => Words), evidence: MathEvidence.Both);

        var resolved = await resolver.ResolveAsync(plan, plan.Segments[1]);

        Assert.Empty(interpreter.Requests[0].Formulas);
        Assert.Single(interpreter.Requests[0].Images);
        Assert.Contains(resolved.Warnings, w => w.Contains("formula model could not read the equation", StringComparison.Ordinal)
            && w.Contains("did not finish", StringComparison.Ordinal));
    }

    [Fact]
    public async Task ALetterSettledByThePage_ReachesTheInterpreterAndTheReviewer()
    {
        var a = SyntheticNarration.Anchor("a2", 0, EquationRect);
        var plan = await PlanOf([new ReadingUnit("u2", ReadingUnitKind.DisplayMath, [a], "εt = ζtσt ,", null, "", "", [], 0)]);
        var interpreter = FakeInterpreter.Saying("The shock is a standardised shock scaled by the volatility.");
        var resolver = new MathInterpretationResolver(interpreter, new Crops(), options: Interpret,
            formulas: FakeFormulas.Reading(@"\varepsilon _ { t } = \xi _ { t } \sigma _ { t } ,"),
            formulaReader: new FakeReader(_ => Words), evidence: MathEvidence.Both);

        var resolved = await resolver.ResolveAsync(plan, plan.Segments[0]);

        var formula = Assert.Single(interpreter.Requests[0].Formulas);
        Assert.Contains(@"\zeta", formula.Latex, StringComparison.Ordinal);
        Assert.DoesNotContain(@"\xi", formula.Latex, StringComparison.Ordinal);
        Assert.Contains(resolved.Warnings, w => w.Contains("read xi where the page's text has zeta", StringComparison.Ordinal));
    }

    [Fact]
    public async Task ARefusedEquationIsReadFromTheFormulaModel_BeforeTheInterpretersOwnTranscription()
    {
        var plan = await PlanAsync();
        var transcribing = new FakeReader(_ => "from the interpreter's transcription");
        var byRule = new FakeReader(_ => Words);
        var resolver = new MathInterpretationResolver(FakeInterpreter.Refusing(), new Crops(), options: Interpret, reader: transcribing,
            formulas: FakeFormulas.Reading(Latex), formulaReader: byRule, evidence: MathEvidence.Both);

        var resolved = await resolver.ResolveAsync(plan, plan.Segments[1]);

        Assert.Equal(ScriptProvenance.LocalRule, resolved.Provenance);
        Assert.Equal(Words, resolved.SpokenText);
        Assert.Empty(transcribing.Requests);
        // The formula reader is asked for the equation alone, without the image.
        var asked = Assert.Single(byRule.Requests);
        Assert.Equal(MathPassageKind.DisplayEquation, asked.Kind);
        Assert.Empty(asked.Images);
        Assert.Contains(resolved.Warnings, w => w.Contains("formula model's transcription of the equation", StringComparison.Ordinal));
    }

    [Fact]
    public async Task ARefusedLeadIn_IsReadWholeFromTheInterpretersTranscriptionFirst()
    {
        var plan = await LeadInPlanAsync();
        var transcribing = new FakeReader(_ => "Let the variance be given as sigma sub t squared equals h sub t g sub t");
        var byRule = new FakeReader(_ => Words);
        var resolver = new MathInterpretationResolver(FakeInterpreter.Refusing(), new Crops(), options: Interpret, reader: transcribing,
            formulas: FakeFormulas.Reading(Latex), formulaReader: byRule, evidence: MathEvidence.Both);

        var resolved = await resolver.ResolveAsync(plan, plan.Segments[0]);

        Assert.Equal("Let the variance be given as sigma sub t squared equals h sub t g sub t", resolved.SpokenText);
        Assert.Empty(byRule.Requests);
    }

    [Fact]
    public async Task WithoutALanguageModel_AnEquationIsReadByRule_AndASentenceAsPrinted()
    {
        var plan = await PlanAsync();
        var formulas = FakeFormulas.Reading(Latex);
        var resolver = new MathInterpretationResolver(null, new Crops(), options: Interpret,
            formulas: formulas, formulaReader: new FakeReader(_ => Words));

        var equation = await resolver.ResolveAsync(plan, plan.Segments[1]);
        var sentence = await resolver.ResolveAsync(plan, plan.Segments[0]);

        Assert.Equal(ScriptProvenance.LocalRule, equation.Provenance);
        Assert.Equal(Words, equation.SpokenText);
        Assert.Equal(Words.Length, Assert.Single(equation.SourceLinks).SpokenRange.Length);
        Assert.Contains(equation.Warnings, w => w.Contains("No language model is configured", StringComparison.Ordinal));
        Assert.Contains(equation.Warnings, w => w.Contains("not its meaning", StringComparison.Ordinal));

        Assert.Equal(ScriptProvenance.SourceVerbatim, sentence.Provenance);
        Assert.Equal(plan.Segments[0].SpokenText, sentence.SpokenText);
        Assert.Equal(1, formulas.Calls);   // the sentence has no equation to transcribe
    }

    [Fact]
    public async Task WithoutALanguageModel_ALeadInKeepsItsSentence_AndItsEquationIsRead()
    {
        var plan = await LeadInPlanAsync();
        var resolver = new MathInterpretationResolver(null, new Crops(), options: Interpret,
            formulas: FakeFormulas.Reading(Latex), formulaReader: new FakeReader(_ => Words));

        var resolved = await resolver.ResolveAsync(plan, plan.Segments[0]);

        Assert.Equal(ScriptProvenance.LocalRule, resolved.Provenance);
        Assert.Equal("Let the variance be given as " + Words, resolved.SpokenText);
        // The sentence keeps its own links; the reading is linked to the equation's region.
        var last = resolved.SourceLinks[^1];
        Assert.Equal(["b1"], last.AnchorIds);
        Assert.Equal(Words, resolved.SpokenText.Substring(last.SpokenRange.Start, last.SpokenRange.Length));
        Assert.Contains(resolved.SourceLinks, l => l.AnchorIds.Contains("b0"));
        Assert.Contains(resolved.Warnings, w => w.Contains("read as printed", StringComparison.Ordinal));
    }

    [Fact]
    public async Task WithoutALanguageModel_AnEquationThatCannotBeReadIsAnnounced()
    {
        var plan = await PlanAsync();
        var resolver = new MathInterpretationResolver(null, new Crops(), options: Interpret,
            formulas: FakeFormulas.Reading(Latex), formulaReader: new FakeReader(_ => null));

        var resolved = await resolver.ResolveAsync(plan, plan.Segments[1]);

        Assert.Equal(ScriptProvenance.FailureAnnouncement, resolved.Provenance);
        Assert.Equal(Interpret.AnnouncementTemplate, resolved.SpokenText);
    }

    [Fact]
    public void WithNeitherALanguageModelNorAFormulaModel_ThereIsNothingToResolveWith()
        => Assert.Throws<ArgumentException>(() => new MathInterpretationResolver(null, options: Interpret));
}
