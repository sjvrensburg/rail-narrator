namespace RailNarrator.Domain;

/// <summary>
/// Splits prose into sentences at real boundaries.
/// </summary>
/// <remarks>
/// Naive splitting on <c>". "</c> breaks a sentence after every abbreviation and every decimal
/// point, which produces units too short to narrate with natural prosody and, worse, puts a
/// highlight boundary in the middle of a phrase. The guards here are explicit and inspectable
/// rather than statistical: an academic paper is dense with "Fig. 3", "et al.", "e.g." and "0.5",
/// and a reader can see why a split did or did not happen.
/// <para>
/// Offsets are UTF-16 and refer to the input string, so a caller can map each sentence back to
/// source characters without re-searching.
/// </para>
/// </remarks>
public static class SentenceSplitter
{
    /// <summary>Abbreviations that do not end a sentence. Matched case-sensitively as written, plus a lowercase form.</summary>
    public static readonly IReadOnlySet<string> NonTerminalAbbreviations = new HashSet<string>(StringComparer.Ordinal)
    {
        "Mr.", "Mrs.", "Ms.", "Dr.", "Prof.", "Rev.", "Hon.", "St.", "Jr.", "Sr.",
        "Fig.", "Figs.", "Eq.", "Eqs.", "Tab.", "Sec.", "Sect.", "Ch.", "App.", "Vol.", "Vols.",
        "No.", "Nos.", "p.", "pp.", "al.", "ed.", "eds.", "trans.", "cf.", "viz.", "ibid.",
        "e.g.", "i.e.", "etc.", "vs.", "approx.", "dept.", "est.", "mt.",
        "Jan.", "Feb.", "Mar.", "Apr.", "Jun.", "Jul.", "Aug.", "Sep.", "Sept.", "Oct.", "Nov.", "Dec.",
        "Inc.", "Ltd.", "Co.", "Corp.", "Univ.", "Inst.",
    };

    /// <summary>Single letters that are usually initials when followed by a period.</summary>
    private const string InitialLetters = "ABCDEFGHIJKLMNOPQRSTUVWXYZ";

    /// <summary>
    /// Splits <paramref name="text"/> into sentence ranges. Never returns an empty list for a
    /// non-blank input: the whole string comes back as one sentence rather than being dropped.
    /// </summary>
    /// <param name="text">Prose to split.</param>
    /// <param name="minChars">
    /// Fragments shorter than this are merged forward into the next sentence. Stops a stray initial
    /// or a mis-detected abbreviation from becoming its own narration unit.
    /// </param>
    public static IReadOnlyList<TextRange> Split(string text, int minChars = 12)
    {
        ArgumentNullException.ThrowIfNull(text);
        if (string.IsNullOrWhiteSpace(text)) return [];

        var raw = new List<TextRange>();
        int sentenceStart = FirstNonWhitespace(text, 0);
        if (sentenceStart < 0) return [];

        int i = sentenceStart;
        while (i < text.Length)
        {
            char c = text[i];
            if (c is '.' or '!' or '?' && IsSentenceEnd(text, i))
            {
                // Include any closing quotes/brackets and trailing whitespace with the sentence so
                // the next one starts on a real character.
                int end = i + 1;
                while (end < text.Length && IsCloser(text[end])) end++;
                raw.Add(TextRange.FromBounds(sentenceStart, end));
                int next = FirstNonWhitespace(text, end);
                if (next < 0) { sentenceStart = -1; break; }
                sentenceStart = next;
                i = next;
                continue;
            }
            // A blank line is a paragraph break and always ends a sentence, even without punctuation.
            if (c == '\n' && i + 1 < text.Length && text[i + 1] == '\n')
            {
                int trimmedEnd = TrimTrailingWhitespace(text, sentenceStart, i);
                if (trimmedEnd > sentenceStart) raw.Add(TextRange.FromBounds(sentenceStart, trimmedEnd));
                int next = FirstNonWhitespace(text, i + 1);
                if (next < 0) { sentenceStart = -1; break; }
                sentenceStart = next;
                i = next;
                continue;
            }
            i++;
        }
        if (sentenceStart >= 0 && sentenceStart < text.Length)
        {
            int trimmedEnd = TrimTrailingWhitespace(text, sentenceStart, text.Length);
            if (trimmedEnd > sentenceStart) raw.Add(TextRange.FromBounds(sentenceStart, trimmedEnd));
        }

        return MergeShort(raw, text, minChars);
    }

    private static bool IsSentenceEnd(string s, int punctIndex)
    {
        char c = s[punctIndex];
        if (c is '!' or '?') return true;

        // A period followed by a digit is a decimal point or a numbered list marker, not an end.
        if (punctIndex + 1 < s.Length && char.IsDigit(s[punctIndex + 1])) return false;
        // A period preceded by a digit and followed by whitespace then a digit is a number ("3.14 15").
        if (punctIndex > 0 && char.IsDigit(s[punctIndex - 1]))
        {
            if (punctIndex + 1 < s.Length && char.IsDigit(s[punctIndex + 1])) return false;
            // "5. Next" — a decimal at the end of a fragment is not a sentence end.
            return false;
        }

        // Abbreviation check: walk back over the token containing this period.
        int start = punctIndex;
        while (start > 0 && (char.IsLetter(s[start - 1]) || s[start - 1] == '.')) start--;
        string token = s[start..(punctIndex + 1)];
        if (NonTerminalAbbreviations.Contains(token)) return false;
        if (NonTerminalAbbreviations.Contains(token.ToLowerInvariant())) return false;

        // A single letter before the period is an initial: "J. R. R. Tolkien".
        if (punctIndex - start == 1 && InitialLetters.Contains(char.ToUpperInvariant(s[start]))) return false;

        // Must be followed by whitespace and then something, otherwise it is the string's own end.
        if (punctIndex + 1 >= s.Length) return true;
        if (!char.IsWhiteSpace(s[punctIndex + 1]) && !IsCloser(s[punctIndex + 1])) return false;

        // A lowercase word after the period usually means the sentence continues ("0.5 ml, i.e. ...").
        int next = FirstNonWhitespace(s, punctIndex + 1);
        if (next >= 0 && next < s.Length && char.IsLower(s[next]))
        {
            // Unless the following token is itself an abbreviation opener, treat it as a continuation.
            return false;
        }
        return true;
    }

    private static bool IsCloser(char c) => c is ')' or ']' or '}' or '"' or '\'' or '\u2019' or '\u201D' or '\u2039' or '\u00BB';

    private static int FirstNonWhitespace(string s, int from)
    {
        for (int i = Math.Max(0, from); i < s.Length; i++)
            if (!char.IsWhiteSpace(s[i])) return i;
        return -1;
    }

    private static int TrimTrailingWhitespace(string s, int start, int end)
    {
        while (end > start && char.IsWhiteSpace(s[end - 1])) end--;
        return end;
    }

    private static IReadOnlyList<TextRange> MergeShort(List<TextRange> raw, string text, int minChars)
    {
        if (raw.Count == 0) return [];
        var merged = new List<TextRange>(raw.Count);
        TextRange? pending = null;
        foreach (var r in raw)
        {
            if (pending is { } p)
            {
                merged.Add(TextRange.FromBounds(p.Start, r.End));
                pending = null;
                continue;
            }
            if (NonWhitespaceLength(text, r) < minChars) { pending = r; continue; }
            merged.Add(r);
        }
        // A trailing short fragment has nothing to merge forward into; attach it to the last sentence
        // rather than dropping it, so no source text is silently lost.
        if (pending is { } tail)
        {
            if (merged.Count > 0) merged[^1] = TextRange.FromBounds(merged[^1].Start, tail.End);
            else merged.Add(tail);
        }
        return merged;
    }

    private static int NonWhitespaceLength(string s, TextRange r)
    {
        int n = 0;
        for (int i = r.Start; i < r.End && i < s.Length; i++) if (!char.IsWhiteSpace(s[i])) n++;
        return n;
    }
}

/// <summary>Tuning for inline maths detection.</summary>
/// <param name="RequireStrongSymbol">A run must contain at least one unambiguous mathematical operator to be treated as maths.</param>
/// <param name="MinRunChars">Shortest run accepted. Below this a single stray glyph would split a sentence.</param>
/// <param name="MaxRunChars">Longest run accepted. A "run" longer than this is prose that happens to contain symbols, not an expression.</param>
/// <param name="UseScriptSizeCues">Consider sub- and superscript geometry as evidence.</param>
/// <param name="ScriptSizeFraction">A glyph this much shorter than its line's median height counts as a sub- or superscript.</param>
public sealed record MathsDetectionOptions(
    bool RequireStrongSymbol = true,
    int MinRunChars = 3,
    int MaxRunChars = 240,
    bool UseScriptSizeCues = true,
    double ScriptSizeFraction = 0.78);

/// <summary>
/// Geometry and identity of one glyph, as far as maths detection needs it.
/// </summary>
/// <param name="Character">The glyph.</param>
/// <param name="Rect">Its box in displayed page points.</param>
/// <param name="LineMedianHeight">Median glyph height on the same line — the scale that makes "smaller" meaningful.</param>
/// <param name="LineBaselineY">The line's baseline, so a small glyph can be told apart from a subscript or a superscript.</param>
public readonly record struct GlyphCue(char Character, PageRect Rect, float LineMedianHeight, float LineBaselineY);

/// <summary>
/// Finds inline mathematical regions in a prose run.
/// </summary>
/// <remarks>
/// <para>
/// Conservative by design. A false positive splits a sentence in the middle and reads half a clause
/// as an equation; a false negative leaves an expression to be spoken as raw text, which is bad but
/// recoverable — the user has a "treat as maths" command. So the detector requires positive evidence
/// and errs towards prose.
/// </para>
/// <para>
/// It combines three signals, because no single one is sufficient: the layout role when a model
/// supplied one, unambiguous operator glyphs, and sub/superscript geometry. A model-free analyser
/// reports every block as plain text, and Core's word index treats an expression as a run of glyphs
/// rather than parsing it, so neither can be relied on alone.
/// </para>
/// </remarks>
public static class MathsRegionDetector
{
    /// <summary>Glyphs that are mathematical in essentially any context.</summary>
    public static readonly IReadOnlySet<char> StrongSymbols = new HashSet<char>(
        "∑∏∫∬∭∮√∛∜≤≥≠≈≡∝∞∂∇±∓×÷∈∉∋⊂⊃⊆⊇∪∩∀∃∄∅∧∨¬⊕⊗⊥∥∠⇒⇐⇔→←↔↦≅≇∼≃∝ℏℓ℮" +
        "ℝℕℤℚℂℙ𝔼" +
        "αβγδεζηθικλμνξοπρστυφχψωΑΒΓΔΕΖΗΘΙΚΛΜΝΞΟΠΡΣΤΥΦΧΨΩ");

    /// <summary>Characters that only count as mathematical next to other evidence.</summary>
    public static readonly IReadOnlySet<char> WeakSymbols = new HashSet<char>("=+-*/^_<>|{}[]()\\.,:;");

    /// <summary>
    /// Detects inline maths runs in <paramref name="text"/>.
    /// </summary>
    /// <param name="text">The prose run.</param>
    /// <param name="glyphs">One cue per character of <paramref name="text"/>; must be the same length, or null to use symbol evidence only.</param>
    /// <param name="options">Detection tuning.</param>
    /// <returns>Disjoint, ordered ranges of <paramref name="text"/> that read as expressions.</returns>
    public static IReadOnlyList<TextRange> DetectInline(string text, IReadOnlyList<GlyphCue>? glyphs, MathsDetectionOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(text);
        options ??= new MathsDetectionOptions();
        if (text.Length == 0) return [];
        if (glyphs is not null && glyphs.Count != text.Length)
            throw new ArgumentException($"Expected {text.Length} glyph cues, got {glyphs.Count}.", nameof(glyphs));

        // Score each character: does it look like part of an expression?
        var scores = new byte[text.Length];
        for (int i = 0; i < text.Length; i++)
        {
            char c = text[i];
            if (char.IsWhiteSpace(c)) { scores[i] = 0; continue; }
            if (StrongSymbols.Contains(c)) { scores[i] = 3; continue; }
            if (glyphs is not null && options.UseScriptSizeCues && IsScript(glyphs[i], options)) { scores[i] = 2; continue; }
            if (WeakSymbols.Contains(c)) { scores[i] = 1; continue; }
            // A lone letter between operators is part of the expression; a letter in a word is not.
            scores[i] = IsIsolatedLetter(text, i) ? (byte)2 : (byte)0;
        }

        var runs = CollectRuns(scores, text, options);
        return runs;
    }

    /// <summary>
    /// True when a whole block should be treated as display maths. A layout model's role is
    /// authoritative when present; geometry is the fallback for a model-free extraction.
    /// </summary>
    public static bool IsDisplayMathsBlock(BlockKind kind, PageRect bbox, double pageHeight, IReadOnlyList<GlyphCue> glyphs, MathsDetectionOptions? options = null)
    {
        if (kind == BlockKind.DisplayMath) return true;
        if (kind is not (BlockKind.Unknown or BlockKind.Text)) return false;
        options ??= new MathsDetectionOptions();

        // An unnumbered, centred, short block with operator glyphs is the classic display equation.
        bool shortBlock = bbox.Height < pageHeight * 0.14;
        bool hasStrong = glyphs.Any(g => StrongSymbols.Contains(g.Character));
        int scriptCount = glyphs.Count(g => IsScript(g, options));
        bool hasScripts = scriptCount >= 2;
        return shortBlock && hasStrong && hasScripts;
    }

    private static bool IsScript(GlyphCue g, MathsDetectionOptions options)
    {
        if (g.LineMedianHeight <= 0f || g.Rect.Height <= 0f) return false;
        double ratio = g.Rect.Height / g.LineMedianHeight;
        if (ratio >= options.ScriptSizeFraction) return false;
        // Must sit clearly above or below the baseline; a small glyph on the baseline is just a
        // punctuation mark or a lowercase letter in a small font.
        float centre = g.Rect.MidY;
        float offset = centre - g.LineBaselineY;
        return Math.Abs(offset) > g.LineMedianHeight * 0.15;
    }

    private static bool IsIsolatedLetter(string text, int i)
    {
        if (!char.IsLetter(text[i])) return false;
        bool leftFree = i == 0 || char.IsWhiteSpace(text[i - 1]) || StrongSymbols.Contains(text[i - 1]) || WeakSymbols.Contains(text[i - 1]);
        bool rightFree = i + 1 >= text.Length || char.IsWhiteSpace(text[i + 1]) || StrongSymbols.Contains(text[i + 1]) || WeakSymbols.Contains(text[i + 1]);
        // Single letters standing alone ("x", "n") are variables; letters inside a word are prose.
        return leftFree && rightFree;
    }

    private static List<TextRange> CollectRuns(byte[] scores, string text, MathsDetectionOptions options)
    {
        var runs = new List<TextRange>();
        int i = 0;
        while (i < scores.Length)
        {
            if (scores[i] == 0) { i++; continue; }

            // Extend across gaps of at most a couple of spaces or weak characters, so "x + y" is one
            // run rather than three, but stop at a real word boundary.
            int start = i;
            int lastScored = i;
            int gap = 0;
            while (i < scores.Length)
            {
                if (scores[i] > 0) { lastScored = i; gap = 0; i++; continue; }
                gap++;
                // Whitespace inside an expression is fine; a long untokenised word is prose.
                if (gap > 2) break;
                if (!char.IsWhiteSpace(text[i]) && scores[i] == 0 && char.IsLetterOrDigit(text[i])) break;
                i++;
            }
            int end = lastScored + 1;

            int length = end - start;
            if (length < options.MinRunChars || length > options.MaxRunChars) { i = end; continue; }

            bool hasStrong = false;
            int strongOrScript = 0;
            for (int k = start; k < end; k++)
            {
                if (scores[k] == 3) { hasStrong = true; strongOrScript++; }
                else if (scores[k] == 2) strongOrScript++;
            }
            if (options.RequireStrongSymbol && !hasStrong) { i = end; continue; }
            if (strongOrScript < 2) { i = end; continue; }

            // Trim trailing punctuation that belongs to the surrounding sentence.
            while (end > start && TrailingPunctuation(text[end - 1])) end--;
            while (start < end && char.IsWhiteSpace(text[start])) start++;
            if (end - start >= options.MinRunChars) runs.Add(TextRange.FromBounds(start, end));
            i = Math.Max(i, end);
        }
        return runs;
    }

    private static bool TrailingPunctuation(char c) => c is ',' or ';' or ':' or '.' or '!' or '?';
}
