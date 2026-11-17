namespace RailNarrator.Domain;

/// <summary>
/// One correspondence between a range in a transformed string and the range it came from in the
/// input string. Both are UTF-16 offsets in their own string.
/// </summary>
/// <remarks>
/// A mapping is not necessarily one-to-one, and pretending otherwise is how highlights drift.
/// Dehyphenation removes a character (2 output chars from 3 input chars across a break); a spoken
/// expansion replaces one glyph with several words ("§" → "section"); a ligature expands one glyph
/// into two letters. Each is expressible as output-range → input-range, which is all the source map
/// needs.
/// </remarks>
public readonly record struct TextMapping(TextRange Output, TextRange Input);

/// <summary>
/// A transformed string together with the complete mapping back to its input.
/// </summary>
/// <remarks>
/// Composable: applying a second transform to a <see cref="TransformedText"/> composes the mappings,
/// so the final spoken text can still be traced all the way back to original character offsets and
/// therefore to source anchors. Losing that chain is what forces a reader to guess.
/// </remarks>
public sealed record TransformedText(string Text, IReadOnlyList<TextMapping> Mappings, string SourceText)
{
    /// <summary>
    /// The identity transform over <paramref name="text"/>.
    /// </summary>
    /// <remarks>
    /// One mapping <b>per character</b>, not one mapping covering the whole string. Composition
    /// resolves a downstream range by asking the upstream mapping what it came from, so a single
    /// whole-string mapping answers "the entire string" for every character and collapses every
    /// source link onto the whole unit. That is a silent correctness failure rather than a
    /// performance detail: the highlights would cover the page.
    /// </remarks>
    public static TransformedText Identity(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        var mappings = new List<TextMapping>(text.Length);
        for (int i = 0; i < text.Length; i++)
            mappings.Add(new TextMapping(new TextRange(i, 1), new TextRange(i, 1)));
        return new TransformedText(text, mappings, text);
    }

    /// <summary>
    /// Every input range contributing to an output range, in order. Several when the output range
    /// spans a transformation boundary — a dehyphenated word, for instance, comes from two lines.
    /// </summary>
    public IReadOnlyList<TextRange> ToSourceRanges(TextRange outputRange)
    {
        var result = new List<TextRange>();
        foreach (var m in Mappings)
        {
            if (!m.Output.Overlaps(outputRange)) continue;
            if (result.Count > 0 && result[^1].End == m.Input.Start) result[^1] = TextRange.FromBounds(result[^1].Start, m.Input.End);
            else result.Add(m.Input);
        }
        return result;
    }

    /// <summary>The tightest single input range covering an output range, or null when nothing maps.</summary>
    public TextRange? ToSourceRange(TextRange outputRange)
    {
        var ranges = ToSourceRanges(outputRange);
        if (ranges.Count == 0) return null;
        int start = ranges.Min(r => r.Start);
        int end = ranges.Max(r => r.End);
        return TextRange.FromBounds(start, end);
    }

    /// <summary>The output range covering an input range, or null when the input was removed entirely.</summary>
    public TextRange? ToOutputRange(TextRange sourceRange)
    {
        int start = int.MaxValue, end = int.MinValue;
        foreach (var m in Mappings)
        {
            if (!m.Input.Overlaps(sourceRange)) continue;
            start = Math.Min(start, m.Output.Start);
            end = Math.Max(end, m.Output.End);
        }
        return end > start ? TextRange.FromBounds(start, end) : null;
    }

    /// <summary>
    /// Composes a further transform applied to this text, so the result maps straight back to
    /// <see cref="SourceText"/>.
    /// </summary>
    public TransformedText Then(Func<TransformedText, TransformedText> next) => next(this);
}

/// <summary>
/// Text transformations that keep their mappings. Every one of these is a place a naive
/// implementation loses the link to the source and then highlights the wrong words.
/// </summary>
public static class TextNormaliser
{
    /// <summary>
    /// Ligatures and other compatibility characters that a PDF text layer commonly carries as a
    /// single glyph but that must be spoken, searched and matched as the letters they represent.
    /// </summary>
    public static readonly IReadOnlyDictionary<char, string> Ligatures = new Dictionary<char, string>
    {
        ['\uFB00'] = "ff", ['\uFB01'] = "fi", ['\uFB02'] = "fl", ['\uFB03'] = "ffi", ['\uFB04'] = "ffl",
        ['\uFB05'] = "st", ['\uFB06'] = "st",
        ['\u2018'] = "'", ['\u2019'] = "'", ['\u201C'] = "\"", ['\u201D'] = "\"",
        ['\u2013'] = "-", ['\u2014'] = "-", ['\u2026'] = "...",
        ['\u00A0'] = " ", ['\u2007'] = " ", ['\u202F'] = " ",
        ['\u2044'] = "/", ['\u2212'] = "-",
    };

    /// <summary>
    /// Expands ligatures and normalises compatibility characters, one output range per input
    /// character so the mapping stays exact.
    /// </summary>
    public static TransformedText ExpandLigatures(string input)
    {
        ArgumentNullException.ThrowIfNull(input);
        var sb = new System.Text.StringBuilder(input.Length);
        var mappings = new List<TextMapping>(input.Length);
        for (int i = 0; i < input.Length; i++)
        {
            char c = input[i];
            string replacement = Ligatures.TryGetValue(c, out var r) ? r : c.ToString();
            var output = new TextRange(sb.Length, replacement.Length);
            sb.Append(replacement);
            mappings.Add(new TextMapping(output, new TextRange(i, 1)));
        }
        return new TransformedText(sb.ToString(), mappings, input);
    }

    /// <summary>
    /// Collapses runs of whitespace to a single space and trims the ends, keeping a mapping for every
    /// surviving character. Whitespace that collapses away maps to an empty output range so its
    /// source characters remain reachable.
    /// </summary>
    public static TransformedText CollapseWhitespace(string input)
    {
        ArgumentNullException.ThrowIfNull(input);
        var sb = new System.Text.StringBuilder(input.Length);
        var mappings = new List<TextMapping>(input.Length);
        bool pendingSpace = false;
        for (int i = 0; i < input.Length; i++)
        {
            if (char.IsWhiteSpace(input[i]))
            {
                // Leading whitespace is dropped; interior whitespace becomes at most one space,
                // emitted when the next real character arrives so trailing whitespace needs no trim.
                if (sb.Length > 0) pendingSpace = true;
                mappings.Add(new TextMapping(new TextRange(sb.Length, 0), new TextRange(i, 1)));
                continue;
            }
            if (pendingSpace)
            {
                mappings.Add(new TextMapping(new TextRange(sb.Length, 1), new TextRange(i, 0)));
                sb.Append(' ');
                pendingSpace = false;
            }
            mappings.Add(new TextMapping(new TextRange(sb.Length, 1), new TextRange(i, 1)));
            sb.Append(input[i]);
        }
        return new TransformedText(sb.ToString(), mappings, input);
    }

    /// <summary>
    /// Joins a word split across a line break by end-of-line hyphenation ("infor-" + "mation" →
    /// "information"). A hyphen that is part of the word ("state-of-the-art", "re-read") is kept,
    /// which is why this is applied only at a line boundary and only when the break looks like
    /// hyphenation rather than a compound.
    /// </summary>
    /// <param name="input">Text containing explicit line breaks.</param>
    /// <param name="lineBreakIndices">UTF-16 offsets of the break characters. Only these are candidates.</param>
    /// <remarks>
    /// The heuristic is deliberately conservative. Removing a genuine hyphen changes the word and
    /// therefore the source mapping; keeping a soft hyphen costs a stumble in the audio. A hyphen is
    /// treated as soft only when a lowercase letter follows the break and the fragment before it is
    /// not a known prefix that would stand alone.
    /// </remarks>
    public static TransformedText Dehyphenate(string input, IReadOnlySet<int> lineBreakIndices)
    {
        ArgumentNullException.ThrowIfNull(input);
        var sb = new System.Text.StringBuilder(input.Length);
        var mappings = new List<TextMapping>(input.Length);
        var breakSet = lineBreakIndices ?? new HashSet<int>();

        for (int i = 0; i < input.Length; i++)
        {
            bool dropHyphen = false;
            if (input[i] == '-' && IsSoftHyphenBreak(input, i, breakSet, out int breakEnd))
            {
                // Emit the hyphen as an empty output range so its source offset stays reachable,
                // then skip the break whitespace that follows it.
                mappings.Add(new TextMapping(new TextRange(sb.Length, 0), new TextRange(i, breakEnd - i)));
                i = breakEnd - 1;
                dropHyphen = true;
            }

            if (!dropHyphen)
            {
                mappings.Add(new TextMapping(new TextRange(sb.Length, 1), new TextRange(i, 1)));
                sb.Append(input[i]);
            }
        }
        return new TransformedText(sb.ToString(), mappings, input);
    }

    private static bool IsSoftHyphenBreak(string s, int hyphenIndex, IReadOnlySet<int> breakSet, out int breakEnd)
    {
        breakEnd = hyphenIndex + 1;
        // The break must be immediately after the hyphen, allowing for surrounding whitespace.
        int j = hyphenIndex + 1;
        bool sawBreak = false;
        while (j < s.Length && (char.IsWhiteSpace(s[j]) || breakSet.Contains(j)))
        {
            if (breakSet.Contains(j) || s[j] == '\n') sawBreak = true;
            j++;
        }
        if (!sawBreak) return false;
        breakEnd = j;

        if (hyphenIndex == 0 || j >= s.Length) return false;
        // Both sides must be letters: "state-" at a line end followed by "of" is a compound, and
        // dropping that hyphen would change the word.
        if (!char.IsLetter(s[hyphenIndex - 1]) || !char.IsLetter(s[j])) return false;
        // A capital after the break starts a new sentence or a proper noun, not a joined word.
        if (char.IsUpper(s[j])) return false;
        // A very short fragment before the hyphen is more likely a prefix ("re-", "pre-", "e-").
        int fragmentStart = hyphenIndex - 1;
        while (fragmentStart > 0 && char.IsLetter(s[fragmentStart - 1])) fragmentStart--;
        if (hyphenIndex - fragmentStart < 2) return false;
        if (StandalonePrefixes.Contains(s[fragmentStart..hyphenIndex].ToLowerInvariant())) return false;

        // A hyphenated compound keeps its hyphens. "state-" at a line end followed by "of-the-art" is
        // one compound word, not a soft hyphen, and joining it would invent "stateof". Without a
        // dictionary the reliable signal is that either side already carries a hyphen of its own.
        if (TokenContainsHyphen(s, breakEnd, forward: true)) return false;
        if (TokenContainsHyphen(s, fragmentStart, forward: false)) return false;
        return true;
    }

    /// <summary>True when the token starting at <paramref name="from"/> (in the given direction) contains a hyphen.</summary>
    private static bool TokenContainsHyphen(string s, int from, bool forward)
    {
        if (forward)
        {
            for (int i = from; i < s.Length && !char.IsWhiteSpace(s[i]); i++)
                if (s[i] == '-') return true;
        }
        else
        {
            for (int i = from - 1; i >= 0 && !char.IsWhiteSpace(s[i]); i--)
                if (s[i] == '-') return true;
        }
        return false;
    }

    /// <summary>Prefixes that keep their hyphen even at a line break.</summary>
    private static readonly HashSet<string> StandalonePrefixes = new(StringComparer.Ordinal)
    {
        "re", "pre", "post", "non", "anti", "co", "de", "dis", "ex", "extra", "hyper", "hypo",
        "in", "im", "ir", "inter", "mid", "mis", "multi", "over", "out", "pan", "pro", "semi",
        "sub", "super", "trans", "ultra", "un", "under", "up", "e", "a",
    };

    /// <summary>
    /// Replaces a symbol or abbreviation with the words a narrator would say, keeping the mapping so
    /// the spoken range still points at the source glyph.
    /// </summary>
    /// <remarks>
    /// Applied to prose only. Mathematical content goes through the verbalisation path instead:
    /// expanding a symbol inside an expression produces a string that is neither the expression nor
    /// a faithful reading of it.
    /// </remarks>
    public static TransformedText ExpandForSpeech(string input, IReadOnlyDictionary<string, string>? dictionary = null)
    {
        ArgumentNullException.ThrowIfNull(input);
        var table = SpeechExpansions.BuildTable(dictionary);
        var sb = new System.Text.StringBuilder(input.Length);
        var mappings = new List<TextMapping>(input.Length);

        int i = 0;
        while (i < input.Length)
        {
            var match = SpeechExpansions.LongestMatchAt(input, i, table);
            if (match is { } m)
            {
                mappings.Add(new TextMapping(new TextRange(sb.Length, m.Replacement.Length), new TextRange(i, m.Length)));
                sb.Append(m.Replacement);
                i += m.Length;
                continue;
            }
            mappings.Add(new TextMapping(new TextRange(sb.Length, 1), new TextRange(i, 1)));
            sb.Append(input[i]);
            i++;
        }
        return new TransformedText(sb.ToString(), mappings, input);
    }

    /// <summary>
    /// The full prose pipeline: ligatures → dehyphenation → whitespace → spoken expansions, with the
    /// composed mapping back to the original characters.
    /// </summary>
    public static TransformedText ForSpeech(string input, IReadOnlySet<int> lineBreakIndices, IReadOnlyDictionary<string, string>? pronunciationDictionary = null)
    {
        ArgumentNullException.ThrowIfNull(input);
        var step1 = ExpandLigatures(input);
        var step2 = Compose(step1, Dehyphenate(step1.Text, lineBreakIndices));
        var step3 = Compose(step2, CollapseWhitespace(step2.Text));
        var step4 = Compose(step3, ExpandForSpeech(step3.Text, pronunciationDictionary));
        return step4 with { SourceText = input };
    }

    /// <summary>
    /// Composes <paramref name="second"/> (which maps within <paramref name="first"/>'s output) onto
    /// <paramref name="first"/>, producing a single mapping back to the original input.
    /// </summary>
    public static TransformedText Compose(TransformedText first, TransformedText second)
    {
        ArgumentNullException.ThrowIfNull(first);
        ArgumentNullException.ThrowIfNull(second);

        var composed = new List<TextMapping>(second.Mappings.Count);
        foreach (var m2 in second.Mappings)
        {
            // Find what the second transform's input range corresponds to in the original.
            var sourceRanges = first.ToSourceRanges(m2.Input);
            if (sourceRanges.Count == 0)
            {
                composed.Add(new TextMapping(m2.Output, TextRange.Empty));
                continue;
            }
            int start = sourceRanges.Min(r => r.Start);
            int end = Math.Max(sourceRanges.Max(r => r.End), start);
            composed.Add(new TextMapping(m2.Output, TextRange.FromBounds(start, end)));
        }
        return new TransformedText(second.Text, composed, first.SourceText);
    }
}

/// <summary>
/// The spoken-word table for symbols and abbreviations, plus a user pronunciation dictionary that
/// takes precedence.
/// </summary>
public static class SpeechExpansions
{
    /// <summary>Built-in expansions. Deliberately small and inspectable; a large rewrite of prose by rule is not the design.</summary>
    public static readonly IReadOnlyDictionary<string, string> BuiltIn = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        ["§"] = "section ", ["§§"] = "sections ",
        ["%"] = " percent", ["‰"] = " per mille",
        ["e.g."] = "for example, ", ["i.e."] = "that is, ", ["et al."] = "and others, ",
        ["cf."] = "compare ", ["vs."] = "versus ", ["viz."] = "namely, ",
        ["Fig."] = "Figure ", ["fig."] = "figure ", ["Figs."] = "Figures ",
        ["Eq."] = "Equation ", ["eq."] = "equation ", ["Eqs."] = "Equations ",
        ["Tab."] = "Table ", ["tbl."] = "table ",
        ["No."] = "number ", ["nos."] = "numbers ",
        ["pp."] = "pages ", ["p."] = "page ",
        ["Dr."] = "Doctor ", ["Prof."] = "Professor ", ["Mr."] = "Mister ", ["Mrs."] = "Misses ", ["Ms."] = "M S ",
        ["St."] = "Saint ",
        ["†"] = "", ["‡"] = "", ["¶"] = "",
        ["°C"] = " degrees Celsius", ["°F"] = " degrees Fahrenheit", ["°"] = " degrees",
        ["µ"] = "micro", ["Ω"] = "ohm", ["€"] = " euro", ["£"] = " pound", ["¥"] = " yen",
        ["→"] = " tends to ", ["←"] = " is assigned ", ["↔"] = " if and only if ",
        ["•"] = "", ["·"] = " ",
    };

    /// <summary>Merges the built-in table with user overrides. A user entry always wins.</summary>
    public static Dictionary<string, string> BuildTable(IReadOnlyDictionary<string, string>? userDictionary)
    {
        var table = new Dictionary<string, string>(BuiltIn, StringComparer.Ordinal);
        if (userDictionary is not null)
            foreach (var (k, v) in userDictionary) table[k] = v;
        return table;
    }

    /// <summary>Longest match at an offset, so "e.g." wins over "e" and "§§" over "§".</summary>
    public static (string Replacement, int Length)? LongestMatchAt(string s, int offset, IReadOnlyDictionary<string, string> table)
    {
        int maxLen = Math.Min(12, s.Length - offset);
        for (int len = maxLen; len >= 1; len--)
        {
            var candidate = s.AsSpan(offset, len).ToString();
            if (table.TryGetValue(candidate, out var replacement)) return (replacement, len);
        }
        return null;
    }
}
