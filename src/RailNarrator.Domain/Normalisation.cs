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

    // An author name: capitalised, possibly with an initial particle, a hyphen or diacritics.
    private const string Author = @"(?:(?:van|von|de|der|den|du|da|di|le|la)\s+)*\p{Lu}[\p{L}'’\-]+";
    private const string Year = @"(?:1[5-9]|20)\d{2}[a-z]?";

    // "(Hamilton, 1989; Ang & Timmermann, 2012)", "(see Engle, 1982)", "(Glosten et al., 1993, p. 4)".
    private static readonly System.Text.RegularExpressions.Regex Parenthetical = new(
        @"\s*\((?=[^()]*" + Author + @"(?:\s*(?:,|and|&)\s*" + Author + @")*(?:\s+et\s+al\.?)?,?\s+" + Year + @")[^()]{1,300}\)",
        System.Text.RegularExpressions.RegexOptions.CultureInvariant);

    // "Engle (1982)", "Amado and Teräsvirta (2008, 2013)": the bracket holds only years.
    private static readonly System.Text.RegularExpressions.Regex Narrative = new(
        @"(?<=(?:" + Author + @"|al\.)\s*)\s*\(\s*" + Year + @"(?:\s*[,;]\s*" + Year + @")*\s*\)",
        System.Text.RegularExpressions.RegexOptions.CultureInvariant);

    // " [12]", " [3, 7]", " [4–6]": after a space, so an index such as "v[2]" is not a citation.
    private static readonly System.Text.RegularExpressions.Regex Numeric = new(
        @"\s+\[\d{1,3}(?:\s*[,–\-]\s*\d{1,3})*\]", System.Text.RegularExpressions.RegexOptions.CultureInvariant);

    /// <summary>
    /// Leaves out in-line citation markers, as a reader does on a first reading: "Engle (1982)
    /// proposed" becomes "Engle proposed", "volatility (Hamilton, 1989)." becomes "volatility.",
    /// and "[12]" disappears. The dropped characters keep a mapping to an empty output range, so
    /// highlights around them are unaffected.
    /// </summary>
    /// <remarks>
    /// A bracket counts as a citation only with an author or a name before it: "(1990–1995)" in
    /// "the period (1990–1995)" is content and is read.
    /// </remarks>
    public static TransformedText DropCitations(string input)
    {
        ArgumentNullException.ThrowIfNull(input);
        var drop = new bool[input.Length];
        foreach (var regex in new[] { Parenthetical, Narrative, Numeric })
            foreach (System.Text.RegularExpressions.Match m in regex.Matches(input))
                for (int i = m.Index; i < m.Index + m.Length; i++) drop[i] = true;

        var sb = new System.Text.StringBuilder(input.Length);
        var mappings = new List<TextMapping>(input.Length);
        for (int i = 0; i < input.Length; i++)
        {
            if (drop[i])
            {
                mappings.Add(new TextMapping(new TextRange(sb.Length, 0), new TextRange(i, 1)));
                continue;
            }
            mappings.Add(new TextMapping(new TextRange(sb.Length, 1), new TextRange(i, 1)));
            sb.Append(input[i]);
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
            if (EnglishNumbers.At(input, i) is { } number)
            {
                mappings.Add(new TextMapping(new TextRange(sb.Length, number.Words.Length), new TextRange(i, number.Length)));
                sb.Append(number.Words);
                i += number.Length;
                continue;
            }
            if (MathsLetters.At(input, i) is { } letter)
            {
                // A name next to another letter or digit needs a space to stay a word of its own
                // ("μh" is "mu h", not "muh").
                string spoken = (sb.Length > 0 && char.IsLetterOrDigit(sb[^1]) && letter.IsName ? " " : "")
                    + letter.Spoken
                    + (i + letter.Length < input.Length && char.IsLetterOrDigit(input[i + letter.Length])
                       && (letter.IsName || MathsLetters.At(input, i + letter.Length) is not null) ? " " : "");
                mappings.Add(new TextMapping(new TextRange(sb.Length, spoken.Length), new TextRange(i, letter.Length)));
                sb.Append(spoken);
                i += letter.Length;
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
/// Letters styled as maths, spoken as the letters they are: Mathematical Alphanumeric Symbols
/// (U+1D400–U+1D7FF, which pdfTeX uses for maths italics), letterlike symbols such as ℎ (U+210E),
/// and Greek letters, which are read by name.
/// </summary>
/// <remarks>
/// Kokoro's phonemiser throws on the U+1D400 block, so a sentence holding one could not be spoken at
/// all; it reads plain Greek letters by name, so writing the name changes nothing that is heard and
/// gives the aligner a word it can find. A styled letter keeps its source link to both UTF-16 units.
/// </remarks>
internal static class MathsLetters
{
    private static readonly string[] Greek =
    [
        "alpha", "beta", "gamma", "delta", "epsilon", "zeta", "eta", "theta", "iota", "kappa", "lambda", "mu",
        "nu", "xi", "omicron", "pi", "rho", "sigma", "sigma", "tau", "upsilon", "phi", "chi", "psi", "omega",
    ];

    /// <summary>True for a code point that is a letter or digit styled for maths.</summary>
    public static bool IsStyled(int codePoint)
        => codePoint is >= 0x1D400 and <= 0x1D7FF || codePoint is 0x210E or 0x2113 or 0x2102 or 0x2115 or 0x211A or 0x211D or 0x2124;

    public static (int Length, string Spoken, bool IsName)? At(string text, int offset)
    {
        int length = 1, codePoint = text[offset];
        if (char.IsHighSurrogate(text[offset]))
        {
            if (offset + 1 >= text.Length || !char.IsLowSurrogate(text[offset + 1])) return null;
            codePoint = char.ConvertToUtf32(text[offset], text[offset + 1]);
            length = 2;
        }
        string plain = IsStyled(codePoint)
            ? char.ConvertFromUtf32(codePoint).Normalize(System.Text.NormalizationForm.FormKC)
            : char.ConvertFromUtf32(codePoint);
        // Greek variant forms (ϵ, ϑ, ϕ, ϱ, ϖ) fold to their ordinary letters too.
        if (plain.Length == 1 && plain[0] is 'ϵ' or 'ϑ' or 'ϕ' or 'ϱ' or 'ϖ' or 'ϰ')
            plain = plain.Normalize(System.Text.NormalizationForm.FormKC);
        if (plain.Length == 1 && GreekName(plain[0]) is { } name) return (length, name, true);
        if (!IsStyled(codePoint) || plain.Length == 0 || plain == char.ConvertFromUtf32(codePoint)) return null;
        return (length, plain, false);
    }

    private static string? GreekName(char c) => c switch
    {
        >= 'α' and <= 'ω' => Greek[c - 'α'],
        >= 'Α' and <= 'Ω' when c != '\u03A2' => Capitalise(Greek[c - 'Α']),
        _ => null,
    };

    private static string Capitalise(string s) => char.ToUpperInvariant(s[0]) + s[1..];
}

/// <summary>English prose numbers are expanded before synthesis, so aligner and TTS see the same words.</summary>
internal static class EnglishNumbers
{
    private static readonly string[] Small = ["zero", "one", "two", "three", "four", "five", "six", "seven", "eight", "nine", "ten",
        "eleven", "twelve", "thirteen", "fourteen", "fifteen", "sixteen", "seventeen", "eighteen", "nineteen"];
    private static readonly string[] Tens = ["", "", "twenty", "thirty", "forty", "fifty", "sixty", "seventy", "eighty", "ninety"];
    public static (int Length, string Words)? At(string text, int offset)
    {
        if (!(char.IsAsciiDigit(text[offset]) || text[offset] is '-' or '+')
            || offset > 0 && (char.IsLetterOrDigit(text[offset - 1]) || text[offset - 1] is '.' or '_')) return null;
        var match = System.Text.RegularExpressions.Regex.Match(text[offset..], @"^[+-]?(?:[0-9]{1,3}(?:,[0-9]{3})+|[0-9]+)(?:\.[0-9]+)?");
        if (!match.Success || offset + match.Length < text.Length && (char.IsLetterOrDigit(text[offset + match.Length]) || text[offset + match.Length] == '_')) return null;
        if (IdentifierAt(text, offset) is { } identifier) return identifier;
        // Four characters are not four digits: "0.75" and "-100" match the same length.
        if (match.Length == 4 && match.Value.All(char.IsAsciiDigit) && IsYearContext(text, offset, 4)
            && int.Parse(match.Value, System.Globalization.CultureInfo.InvariantCulture) is >= 1100 and <= 1999 and var year)
            return (4, Year(year));
        string value = match.Value.Replace(",", "", StringComparison.Ordinal);
        string prefix = value[0] == '-' ? "minus " : value[0] == '+' ? "plus " : "";
        value = value.TrimStart('-', '+');
        var parts = value.Split('.');
        string words = parts[0].Length > 1 && parts[0][0] == '0' || !long.TryParse(parts[0], out var n) || n >= 1_000_000_000_000
            ? string.Join(" ", parts[0].Select(c => Small[c - '0'])) : Integer(n);
        if (parts.Length == 2) words += " point " + string.Join(" ", parts[1].Select(c => Small[c - '0']));
        return (match.Length, prefix + words);
    }
    /// <summary>"nineteen eighty-nine", "nineteen oh five", "nineteen hundred". Only 1100–1999 are read
    /// this way: "two thousand twelve" is an ordinary reading of 2012 and needs no special case.</summary>
    private static string Year(int year)
    {
        int high = year / 100, low = year % 100;
        string tail = low == 0 ? "hundred" : low < 10 ? "oh " + Small[low] : Integer(low);
        return Integer(high) + " " + tail;
    }

    private static readonly HashSet<string> Months = new(StringComparer.OrdinalIgnoreCase)
    {
        "january", "february", "march", "april", "may", "june", "july", "august", "september", "october",
        "november", "december", "jan", "feb", "mar", "apr", "jun", "jul", "aug", "sep", "sept", "oct", "nov", "dec",
    };

    private static readonly HashSet<string> YearCues = new(StringComparer.OrdinalIgnoreCase)
    {
        "in", "since", "from", "until", "till", "by", "during", "before", "after", "circa", "c.", "year",
        "january", "february", "march", "april", "may", "june", "july", "august", "september", "october",
        "november", "december", "jan.", "feb.", "mar.", "apr.", "jun.", "jul.", "aug.", "sep.", "sept.",
        "oct.", "nov.", "dec.", "al.",
    };

    /// <summary>
    /// Whether a four-digit number reads as a year where it stands. A bare 1500 may be a count
    /// ("1500 observations"), so the context has to say so: a citation ("(Hamilton, 1989; …)",
    /// "et al. 1993"), a temporal word or month before it, or the other end of a year range.
    /// </summary>
    private static bool IsYearContext(string text, int offset, int length)
    {
        int end = offset + length;
        // A citation's year is followed by its closing bracket, a separator or a letter suffix
        // ("1989a" never reaches here: a following letter rejects the match).
        char after = end < text.Length ? text[end] : ' ';
        int p = offset - 1;
        while (p >= 0 && text[p] == ' ') p--;
        if (p < 0) return false;
        char before = text[p];
        if (before == '(' && after is ')' or ';' or ',' or ':') return true;
        // "May 1, 1997": a day of the month and a comma before the year.
        if (before == ',' && PreviousWord(text, p) is { } day && int.TryParse(day, out int d) && d is >= 1 and <= 31
            && PreviousWord(text, p - day.Length) is { } month && Months.Contains(month.TrimEnd('.')))
            return true;
        // "Hamilton, 1989" / "Ang & Timmermann, 2012" inside a citation list.
        if (before == ',' && after is ')' or ';' && PreviousWord(text, p) is { Length: > 0 } name && char.IsUpper(name[0]))
            return true;
        // Either end of a range, "1990–1995" ("1990-95" is not attempted) — unless it is a page
        // range: "48:1749-1779", "pp. 1187-1211".
        if (before is '-' or '–' && p >= 4 && p == offset - 1 && IsFourDigitYear(text, p - 4)) return !IsPageRange(text, p - 4);
        if (after is '-' or '–' && IsFourDigitYear(text, end + 1)) return !IsPageRange(text, offset);
        string? word = PreviousWord(text, p + 1);
        return word is not null && YearCues.Contains(word) && !CountedNounFollows(text, end);
    }

    /// <summary>"in 1500 patients": a lowercase plural after the number makes it a count.</summary>
    private static bool CountedNounFollows(string text, int end)
    {
        int b = end;
        while (b < text.Length && text[b] == ' ') b++;
        int e = b;
        while (e < text.Length && char.IsLetter(text[e])) e++;
        return e - b >= 4 && char.IsLower(text[b]) && text[e - 1] == 's';
    }

    /// <summary>Whether the range starting at <paramref name="start"/> follows a volume colon or a page label.</summary>
    private static bool IsPageRange(string text, int start)
    {
        if (start > 0 && text[start - 1] == ':') return true;
        string? word = PreviousWord(text, start);
        return word is not null && word.TrimEnd(':') is "pp." or "p." or "pp" or "pages" or "page" or "Pages" or "Page";
    }

    private static bool IsFourDigitYear(string text, int start)
        => start >= 0 && start + 4 <= text.Length
           && text.AsSpan(start, 4).ToString() is var digits && digits.All(char.IsAsciiDigit)
           && (start + 4 == text.Length || !char.IsAsciiDigit(text[start + 4]))
           && (start == 0 || !char.IsAsciiDigit(text[start - 1]))
           && int.Parse(digits) is >= 1100 and <= 2099;

    /// <summary>The word ending just before <paramref name="end"/>, skipping spaces and a trailing comma.</summary>
    private static string? PreviousWord(string text, int end)
    {
        int e = end;
        while (e > 0 && text[e - 1] is ' ' or ',') e--;
        int b = e;
        while (b > 0 && !char.IsWhiteSpace(text[b - 1]) && text[b - 1] is not '(' and not ';') b--;
        return b < e ? text[b..e] : null;
    }

    /// <summary>
    /// ISSN and ISBN digits are spoken one at a time: "1532-4168" read as two cardinals joined by
    /// a hyphen is neither the identifier nor a number.
    /// </summary>
    private static (int Length, string Words)? IdentifierAt(string text, int offset)
    {
        string? label = PreviousWord(text, offset)?.TrimEnd(':');
        bool labelled = label is not null && IdentifierLabels.Contains(label);
        // A second ISSN later in the same line: "ISSN: 0747-4938 (Print) 1532-4168 (Online)". The
        // ISSN shape (four digits, hyphen, three digits and a check digit) is only trusted with the
        // label nearby, because "1990-1995" has the same shape.
        if (!labelled && !(IsIssnShaped(text, offset)
                && text.AsSpan(Math.Max(0, offset - 48), Math.Min(48, offset)).Contains("ISSN", StringComparison.OrdinalIgnoreCase)))
            return null;
        int end = offset;
        while (end < text.Length && (char.IsAsciiDigit(text[end]) || text[end] is '-' or 'X' or 'x')) end++;
        while (end > offset && text[end - 1] == '-') end--;
        var words = text[offset..end].Where(c => c != '-').Select(c => c is 'X' or 'x' ? "ex" : Small[c - '0']);
        return (end - offset, string.Join(" ", words));
    }

    private static readonly HashSet<string> IdentifierLabels = new(StringComparer.OrdinalIgnoreCase) { "ISSN", "ISBN", "e-ISSN", "eISSN" };

    private static bool IsIssnShaped(string text, int offset)
        => offset + 9 <= text.Length
           && text.AsSpan(offset, 4).IndexOfAnyExceptInRange('0', '9') < 0
           && text[offset + 4] == '-'
           && text.AsSpan(offset + 5, 3).IndexOfAnyExceptInRange('0', '9') < 0
           && (char.IsAsciiDigit(text[offset + 8]) || text[offset + 8] is 'X' or 'x')
           && (offset + 9 == text.Length || !char.IsLetterOrDigit(text[offset + 9]));

    private static string Integer(long n)
    {
        if (n < 20) return Small[n];
        if (n < 100) return Tens[n / 10] + (n % 10 == 0 ? "" : " " + Small[n % 10]);
        foreach (var (scale, label) in new[] { (1_000_000_000L, "billion"), (1_000_000L, "million"), (1000L, "thousand"), (100L, "hundred") })
            if (n >= scale) return Integer(n / scale) + " " + label + (n % scale == 0 ? "" : " " + Integer(n % scale));
        throw new InvalidOperationException();
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
        ["e.g."] = "for example, ", ["i.e."] = "that is, ", ["et al."] = "and others",
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
    /// <remarks>
    /// An entry that begins or ends with a letter or digit is a word, and matches only as a whole
    /// word: "p." is not the end of "Chap." or "overlap.", nor the start of "p.adjust", and "SVR" is
    /// not the start of "SVRs". Symbols ("%", "§", "°C") match anywhere.
    /// </remarks>
    public static (string Replacement, int Length)? LongestMatchAt(string s, int offset, IReadOnlyDictionary<string, string> table)
    {
        int maxLen = Math.Min(12, s.Length - offset);
        for (int len = maxLen; len >= 1; len--)
        {
            var candidate = s.AsSpan(offset, len).ToString();
            if (table.TryGetValue(candidate, out var replacement) && IsWholeWord(s, offset, candidate))
                return (replacement, len);
        }
        return null;
    }

    private static bool IsWholeWord(string s, int offset, string key)
    {
        int end = offset + key.Length;
        if (char.IsLetterOrDigit(key[0]) && offset > 0 && char.IsLetterOrDigit(s[offset - 1])) return false;
        if (end < s.Length)
        {
            char next = s[end];
            if (char.IsLetterOrDigit(key[^1]) && char.IsLetterOrDigit(next)) return false;
            // An abbreviation's full stop followed by a letter is part of a name ("p.adjust").
            if (key[^1] == '.' && char.IsLetter(key[0]) && char.IsLetter(next)) return false;
        }
        return true;
    }
}
