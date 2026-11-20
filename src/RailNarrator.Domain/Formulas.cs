using System.Text;
using System.Text.RegularExpressions;

namespace RailNarrator.Domain;

/// <summary>What a language model is given for a passage that holds a display equation.</summary>
public enum MathEvidence
{
    /// <summary>The page image of the passage (and its text layer and context, which are always sent).</summary>
    Image,

    /// <summary>
    /// The formula model's LaTeX for the equation and no image, so a model that cannot see will do.
    /// A sentence with maths in its line has no formula to recognise and is then interpreted from its
    /// text layer and context alone.
    /// </summary>
    Formula,

    /// <summary>The page image and the formula model's LaTeX.</summary>
    Both,
}

/// <summary>A formula model's reading of one crop, or why there is none.</summary>
/// <param name="Latex">The transcription. Null when <paramref name="Problem"/> is set.</param>
/// <param name="Problem">Why the output is not a reading (it did not finish, it repeats itself, the crop is blank).</param>
public sealed record FormulaRecognition(string? Latex, string? Problem)
{
    public bool Succeeded => Latex is { Length: > 0 } && Problem is null;

    public static FormulaRecognition Failed(string problem) => new(null, problem);
}

/// <summary>
/// Transcribes the image of one display equation to LaTeX, locally: a dedicated formula-recognition
/// model in place of a vision language model.
/// </summary>
/// <remarks>
/// A transcription is not proof the equation was read correctly: such a model misreads a symbol now
/// and then with nothing in its output to show it. Callers check it against the page's text layer
/// (<see cref="FormulaTextLayerCheck"/>) and label what they make of it.
/// </remarks>
public interface IFormulaRecogniser
{
    /// <summary>Model file and pipeline version; part of every cache key.</summary>
    string Revision { get; }

    /// <summary>Never throws for a crop the model cannot read; returns the problem.</summary>
    Task<FormulaRecognition> RecogniseAsync(ReadOnlyMemory<byte> png, CancellationToken ct = default);
}

/// <summary>A display equation as the formula model transcribed it, reconciled with the page's text.</summary>
/// <param name="Latex">The transcription, with a one-for-one letter disagreement settled in the text layer's favour.</param>
/// <param name="TextLayer">The equation as the PDF's text layer has it.</param>
/// <param name="Notes">What the comparison with the text layer found, in words a reviewer and a language model can both use.</param>
/// <param name="Disagrees">The transcription and the text layer still name different Greek letters: one of them is wrong.</param>
public sealed record RecognisedFormula(string Latex, string TextLayer, IReadOnlyList<string> Notes, bool Disagrees)
{
    /// <summary>
    /// Version of how a formula is put to a language model (the paragraph in the interpretation
    /// prompt and what this record carries). Part of the cache key of every request that has a
    /// formula; requests without one are keyed exactly as before.
    /// </summary>
    public const string EvidenceVersion = "formula-evidence/1";
}

/// <summary>
/// Compares the Greek letters and the decimal numbers in a formula model's LaTeX with those in the
/// PDF's text layer for the same equation.
/// </summary>
/// <remarks>
/// <para>
/// The text layer garbles the structure of an equation (a fraction becomes a run of characters) but
/// usually has its letters right, and a formula model has the structure right but can take one
/// letter for another: on the GARCH paper PP-FormulaNet_plus-M read zeta as xi in several
/// equations. The two errors are independent, so the letters each names are worth comparing. Sets
/// are compared, not counts: the text layer repeats and drops characters.
/// </para>
/// <para>
/// Only Greek letters are compared. Latin letters are too many for a set to tell anything, and
/// operators are where the text layer is least reliable (a summation sign is often an unmapped
/// glyph). A text layer with no Greek letter at all says nothing about them, and one with unmapped
/// glyphs cannot show that a letter is absent.
/// </para>
/// <para>
/// Decimal numbers are compared one way: a number the page's text has and the transcription lacks.
/// On the GARCH paper the model left out the standard error printed under a coefficient
/// ("3.4905" with "(0.0161)" below it) in two estimated equations; nothing in its output shows
/// that, and the text layer has the digits.
/// </para>
/// </remarks>
public static partial class FormulaTextLayerCheck
{
    private static readonly (string Command, char Letter)[] Commands =
    [
        ("alpha", 'α'), ("beta", 'β'), ("gamma", 'γ'), ("delta", 'δ'), ("epsilon", 'ε'), ("varepsilon", 'ε'),
        ("zeta", 'ζ'), ("eta", 'η'), ("theta", 'θ'), ("vartheta", 'θ'), ("iota", 'ι'), ("kappa", 'κ'), ("varkappa", 'κ'),
        ("lambda", 'λ'), ("mu", 'μ'), ("nu", 'ν'), ("xi", 'ξ'), ("pi", 'π'), ("varpi", 'π'), ("rho", 'ρ'), ("varrho", 'ρ'),
        ("sigma", 'σ'), ("varsigma", 'σ'), ("tau", 'τ'), ("upsilon", 'υ'), ("phi", 'φ'), ("varphi", 'φ'), ("chi", 'χ'),
        ("psi", 'ψ'), ("omega", 'ω'),
        ("Gamma", 'Γ'), ("Delta", 'Δ'), ("Theta", 'Θ'), ("Lambda", 'Λ'), ("Xi", 'Ξ'), ("Upsilon", 'Υ'), ("Phi", 'Φ'),
        ("Psi", 'Ψ'), ("Omega", 'Ω'),
    ];

    private static readonly Dictionary<string, char> LetterOf = Commands.ToDictionary(c => c.Command, c => c.Letter, StringComparer.Ordinal);

    // The first command listed for a letter: what a corrected letter is written as.
    private static readonly Dictionary<char, string> CommandOf = Commands.GroupBy(c => c.Letter).ToDictionary(g => g.Key, g => g.First().Command);

    private static readonly Dictionary<char, string> NameOf = Commands.GroupBy(c => c.Letter)
        .ToDictionary(g => g.Key, g => char.IsUpper(g.Key) ? "capital " + g.First().Command.ToLowerInvariant() : g.First().Command);

    [GeneratedRegex(@"\\([A-Za-z]+)")]
    private static partial Regex Command();

    /// <summary>The Greek letters a LaTeX string names.</summary>
    public static IReadOnlySet<char> LettersInLatex(string latex)
    {
        var letters = new HashSet<char>();
        foreach (Match m in Command().Matches(latex ?? ""))
            if (LetterOf.TryGetValue(m.Groups[1].Value, out char letter)) letters.Add(letter);
        return letters;
    }

    /// <summary>
    /// The Greek letters in a text layer: variant forms and mathematical-alphabet copies folded to
    /// the plain letter. Capital sigma and pi are left out, since they stand for the sum and product
    /// signs as often as for letters.
    /// </summary>
    public static IReadOnlySet<char> LettersInText(string text)
    {
        var letters = new HashSet<char>();
        foreach (char raw in (text ?? "").Normalize(NormalizationForm.FormKC))
        {
            char c = raw switch { 'ϵ' => 'ε', 'ϑ' => 'θ', 'ϕ' => 'φ', 'ϱ' => 'ρ', 'ς' => 'σ', 'ϖ' => 'π', 'ϰ' => 'κ', '∆' => 'Δ', _ => raw };
            if (CommandOf.ContainsKey(c)) letters.Add(c);
        }
        return letters;
    }

    /// <summary>An unmapped glyph leaves a control character or U+FFFD in the text layer.</summary>
    private static bool HasUnmappedGlyphs(string text) => text.Any(c => c == '\uFFFD' || (char.IsControl(c) && c is not ('\n' or '\r' or '\t')));

    /// <summary>
    /// Reconciles a transcription with the text layer. When each names exactly one Greek letter the
    /// other lacks, the model took one for the other and the text layer's letter is used; any other
    /// disagreement is reported and left standing.
    /// </summary>
    public static RecognisedFormula Reconcile(string latex, string textLayer)
    {
        ArgumentNullException.ThrowIfNull(latex);
        textLayer ??= "";
        var notes = new List<string>();
        bool disagrees = false;

        var inText = LettersInText(textLayer);
        var inLatex = LettersInLatex(latex);
        // A text layer with no Greek letter says nothing about them.
        var onlyText = inText.Count == 0 ? [] : inText.Except(inLatex).Order().ToList();
        // A text layer with unmapped glyphs cannot show that a letter is absent from the page.
        var onlyLatex = inText.Count == 0 || HasUnmappedGlyphs(textLayer) ? [] : inLatex.Except(inText).Order().ToList();
        if (onlyText.Count == 1 && onlyLatex.Count == 1)
        {
            char read = onlyLatex[0], printed = onlyText[0];
            latex = Command().Replace(latex, m =>
                LetterOf.TryGetValue(m.Groups[1].Value, out char letter) && letter == read ? "\\" + CommandOf[printed] : m.Value);
            notes.Add($"The formula model read {NameOf[read]} where the page's text has {NameOf[printed]}; the page's letter was used.");
        }
        else if (onlyText.Count > 0 || onlyLatex.Count > 0)
        {
            disagrees = true;
            if (onlyLatex.Count > 0)
                notes.Add($"The formula model's transcription has {Names(onlyLatex)}, which the page's text does not.");
            if (onlyText.Count > 0)
                notes.Add($"The page's text has {Names(onlyText)}, which the formula model's transcription does not.");
        }

        var missing = NumbersMissingFrom(latex, textLayer);
        if (missing.Count > 0)
        {
            disagrees = true;
            notes.Add($"The page's text has the number{(missing.Count == 1 ? "" : "s")} {string.Join(", ", missing)}, which the formula model's transcription does not.");
        }
        return new RecognisedFormula(latex, textLayer, notes, disagrees);
    }

    /// <summary>
    /// The decimal numbers in the text layer that the transcription does not have. The transcription
    /// writes a number token by token ("3 . 9 5 8"), so it is compared without its spaces; a text
    /// number that only continues one of the transcription's is a superscript's digit run together
    /// with it ("0.85" squared read as "0.852"), not a missing number.
    /// </summary>
    public static IReadOnlyList<string> NumbersMissingFrom(string latex, string textLayer)
    {
        var inLatex = Decimal().Matches(Spaces().Replace(latex ?? "", "")).Select(m => m.Value).ToHashSet(StringComparer.Ordinal);
        return Decimal().Matches(textLayer ?? "").Select(m => m.Value).Distinct(StringComparer.Ordinal)
            .Where(number => !inLatex.Any(l => number.StartsWith(l, StringComparison.Ordinal)))
            .Order(StringComparer.Ordinal).ToList();
    }

    [GeneratedRegex(@"\d+\.\d+")]
    private static partial Regex Decimal();

    [GeneratedRegex(@"\s+")]
    private static partial Regex Spaces();

    private static string Names(IReadOnlyList<char> letters) => string.Join(", ", letters.Select(l => NameOf[l]));
}
