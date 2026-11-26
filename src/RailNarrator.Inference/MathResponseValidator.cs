using System.Text.Json;
using System.Text.RegularExpressions;

namespace RailNarrator.Inference;

/// <summary>One parsed and validated verbalisation response.</summary>
public sealed record VerbalisationPayload(
    IReadOnlyList<string> SourceIds,
    string Expression,
    string Faithful,
    string? Explanation,
    IReadOnlyList<string> Warnings);

/// <summary>
/// Validates a model response against the closed schema before any of it is allowed to become speech.
/// </summary>
/// <remarks>
/// <para>
/// This is the boundary between untrusted model output and the narration plan, and it is deliberately
/// strict. An LLM's fluency is not evidence of correctness: it can reverse a quantifier, drop a bound,
/// flip an inequality, invent a variable's meaning, or cite a source id that does not exist — and every
/// one of those reads perfectly well aloud. What can be checked mechanically is checked here; what
/// cannot (whether "for every" and "there exists" are in the right order for <i>this</i> expression) is
/// surfaced as a warning for the review panel rather than silently accepted.
/// </para>
/// <para>
/// LaTeX leaking into <see cref="VerbalisationPayload.Faithful"/> is rejected outright. A speech
/// engine given <c>\frac{1}{n}</c> says "backslash frac one n", which is neither the expression nor a
/// reading of it, and passing it off as natural narration is the specific failure this whole layer
/// exists to prevent.
/// </para>
/// </remarks>
public static partial class MathResponseValidator
{
    public const int MaxFaithfulChars = 1200;
    public const int MaxExplanationChars = 600;
    public const int MaxExpressionChars = 2000;
    public const int MaxWarnings = 12;

    /// <summary>
    /// Parses and validates one response.
    /// </summary>
    /// <param name="json">Raw model output. May be wrapped in a Markdown code fence, which some endpoints add despite being told not to.</param>
    /// <param name="allowedSourceIds">The only ids a response may cite.</param>
    /// <param name="mode">Requested mode; an explanation outside Explain mode is dropped with a warning.</param>
    /// <param name="sourceText">
    /// What the model was shown (the expression and its context). A count or a pair of numbers
    /// that the source itself gives ("over 100 samples", "the first 5 seconds") is the reading,
    /// not an invented coordinate or timing; without it, every such phrase is refused.
    /// </param>
    public static ValidationResult Validate(string? json, IReadOnlySet<string> allowedSourceIds, MathModeRequest mode, string? sourceText = null)
    {
        if (string.IsNullOrWhiteSpace(json))
            return ValidationResult.Fail("The model returned an empty response.");

        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(StripCodeFence(json));
        }
        catch (JsonException ex)
        {
            return ValidationResult.Fail($"Response is not valid JSON: {ex.Message}");
        }

        using (document)
        {
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
                return ValidationResult.Fail($"Expected a JSON object, got {root.ValueKind}.");

            var warnings = new List<string>();
            var problems = new List<string>();

            string? faithful = GetString(root, "faithful", problems, MaxFaithfulChars, required: true);
            string? expression = GetString(root, "expression", problems, MaxExpressionChars, required: false) ?? "";
            string? explanation = GetString(root, "explanation", problems, MaxExplanationChars, required: false);

            var sourceIds = new List<string>();
            if (root.TryGetProperty("source_ids", out var idsElement) && idsElement.ValueKind == JsonValueKind.Array)
            {
                foreach (var id in idsElement.EnumerateArray())
                {
                    if (id.ValueKind != JsonValueKind.String) { problems.Add("source_ids contains a non-string entry."); continue; }
                    string value = id.GetString()!;
                    if (!allowedSourceIds.Contains(value))
                    {
                        // An invented source reference would move a highlight onto text the model was
                        // never shown. Rejecting is the only safe response.
                        problems.Add($"source_ids cites '{value}', which is not a supplied source id.");
                        continue;
                    }
                    sourceIds.Add(value);
                }
            }
            else problems.Add("source_ids is missing or is not an array.");

            var responseWarnings = new List<string>();
            if (root.TryGetProperty("warnings", out var wElement) && wElement.ValueKind == JsonValueKind.Array)
            {
                foreach (var w in wElement.EnumerateArray().Take(MaxWarnings))
                    if (w.ValueKind == JsonValueKind.String) responseWarnings.Add(w.GetString()!);
            }

            if (faithful is not null)
            {
                if (LaTeXLeak().IsMatch(faithful))
                    problems.Add("faithful narration still contains LaTeX or math markup, so it is not spoken language.");
                if (InventsCoordinatesOrTimes(faithful, sourceText))
                    problems.Add("faithful narration contains coordinates or audio times, which a model must not generate.");
                if (faithful.Trim().Length == 0)
                    problems.Add("faithful narration is empty.");
            }

            // An unsolicited interpretation is extra speech the reader did not ask for, and may be an
            // unsupported identification ("this is the OLS estimator") stated as fact.
            if (explanation is { Length: > 0 } && mode != MathModeRequest.Explain)
            {
                warnings.Add("The model supplied an explanation outside Explain mode; it was dropped.");
                explanation = null;
            }

            foreach (var extra in root.EnumerateObject().Select(p => p.Name))
            {
                if (extra is not ("source_ids" or "expression" or "faithful" or "explanation" or "warnings"))
                    warnings.Add($"Ignored an unexpected response field '{extra}'.");
            }

            if (problems.Count > 0) return ValidationResult.Fail(problems);

            return ValidationResult.Ok(new VerbalisationPayload(
                sourceIds, expression, faithful!.Trim(), explanation?.Trim(), responseWarnings), warnings);
        }
    }

    /// <summary>Removes a Markdown code fence, which some endpoints add despite being told not to.</summary>
    public static string StripCodeFence(string json)
    {
        string s = json.Trim();
        if (!s.StartsWith("```", StringComparison.Ordinal)) return s;
        int firstNewline = s.IndexOf('\n');
        if (firstNewline < 0) return s;
        s = s[(firstNewline + 1)..];
        int fence = s.LastIndexOf("```", StringComparison.Ordinal);
        if (fence >= 0) s = s[..fence];
        return s.Trim();
    }

    private static string? GetString(JsonElement root, string name, List<string> problems, int maxLength, bool required)
    {
        if (!root.TryGetProperty(name, out var element))
        {
            if (required) problems.Add($"Required field '{name}' is missing.");
            return null;
        }
        if (element.ValueKind == JsonValueKind.Null)
        {
            if (required) problems.Add($"Required field '{name}' is null.");
            return null;
        }
        if (element.ValueKind != JsonValueKind.String)
        {
            problems.Add($"Field '{name}' must be a string, got {element.ValueKind}.");
            return null;
        }
        string value = element.GetString() ?? "";
        if (value.Length > maxLength)
        {
            problems.Add($"Field '{name}' is {value.Length} characters, over the {maxLength} limit.");
            return null;
        }
        return value;
    }

    /// <summary>LaTeX or math markup that must not survive into spoken text.</summary>
    [GeneratedRegex(@"\\[a-zA-Z]+|[\^_{}$]|\\\[|\\\]|\$\$")]
    private static partial Regex LaTeXLeak();

    /// <summary>A coordinate or timing in <paramref name="faithful"/> with a number the source does not give.</summary>
    private static bool InventsCoordinatesOrTimes(string faithful, string? sourceText)
    {
        foreach (Match m in CoordinateOrTime().Matches(faithful))
        {
            if (sourceText is null) return true;
            foreach (Match number in Number().Matches(m.Value))
                if (!Regex.IsMatch(sourceText, @"(?<![\d.])" + Regex.Escape(number.Value) + @"(?![\d]|\.\d)"))
                    return true;
        }
        return false;
    }

    [GeneratedRegex(@"\d+(\.\d+)?")]
    private static partial Regex Number();

    /// <summary>Coordinates or timings, which a verbaliser has no business producing.</summary>
    [GeneratedRegex(@"\(\s*-?\d+(\.\d+)?\s*,\s*-?\d+|\b\d+(\.\d+)?\s*(ms|milliseconds?|seconds?|samples?|frames?)\b", RegexOptions.IgnoreCase)]
    private static partial Regex CoordinateOrTime();
}

/// <summary>Outcome of validating one response.</summary>
public sealed record ValidationResult(bool IsValid, VerbalisationPayload? Payload, IReadOnlyList<string> Problems, IReadOnlyList<string> Warnings)
{
    public static ValidationResult Ok(VerbalisationPayload payload, IReadOnlyList<string> warnings)
        => new(true, payload, [], warnings);

    public static ValidationResult Fail(string problem) => new(false, null, [problem], []);

    public static ValidationResult Fail(IReadOnlyList<string> problems) => new(false, null, problems, []);
}
