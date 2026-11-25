namespace RailNarrator.Domain;

/// <summary>
/// The text a speech engine is given for a passage: the script, with a letter that stands alone as
/// a symbol written as its name ("a sub t" is sent as "eh sub tee").
/// </summary>
/// <remarks>
/// An engine reads the script as prose, so a lone "a" is the article ("uh sub t") and a short run
/// of single letters can be slurred into a word. The names are for the engine only: the script the
/// reader sees, the source links and the aligner's words stay as written, and a letter's name is
/// what the aligner's dictionary expects to hear for it. Anything that changes what this returns
/// changes the audio, which is keyed by <see cref="NarrationVersions.ProsePipeline"/>: bump it.
/// </remarks>
public static class LetterNames
{
    /// <summary>The engine's text and where it differs from the script.</summary>
    /// <param name="Text">What the engine is given.</param>
    /// <param name="Changes">Each respelled letter: its range in <paramref name="Text"/> and its offset in the script, in order.</param>
    public sealed record SpeechForm(string Text, IReadOnlyList<(TextRange Output, int ScriptOffset)> Changes)
    {
        /// <summary>The script range that a range of the engine's text was made from.</summary>
        public TextRange ToScript(TextRange output)
        {
            int start = Offset(output.Start, end: false);
            return TextRange.FromBounds(start, Math.Max(start, Offset(output.End, end: true)));

            int Offset(int position, bool end)
            {
                int shift = 0;   // how far the engine's text has run ahead of the script so far
                foreach (var (name, script) in Changes)
                {
                    if (position <= name.Start) break;
                    // Inside a name: its one letter, from its start or to its end.
                    if (position < name.End) return end ? script + 1 : script;
                    shift = name.End - (script + 1);
                }
                return position - shift;
            }
        }
    }

    // Chosen by what two engines say, as a recogniser heard it (status.md item 89): Kokoro reads
    // "ay" and "ey" as "eye" and a capital "A" as the article; "eh" is the letter on both.
    private static readonly string[] Names =
    [
        "eh", "bee", "see", "dee", "ee", "eff", "gee", "aitch", "eye", "jay", "kay", "ell", "em",
        "en", "oh", "pee", "cue", "ar", "ess", "tee", "you", "vee", "double you", "ex", "why", "zed",
    ];

    // Words that only a symbol stands beside: "a sub t", "a squared", "plus a", "where a is …".
    private static readonly HashSet<string> AfterASymbol = new(StringComparer.OrdinalIgnoreCase)
    {
        "sub", "super", "squared", "cubed", "hat", "bar", "tilde", "prime", "star", "dagger", "transpose", "inverse",
        "plus", "minus", "times", "equals", "over", "divided", "is", "are", "and", "or", "denotes", "represents", "has", "must",
        // ...and words an article is never followed by: "the sum over a of x", "from a to b",
        // "let a denote the intercept", "the point a lies on the line". Not "lie" ("a lie"), "by"
        // ("a by now classic argument") or "then" ("a then unknown quantity").
        "be", "of", "in", "to", "from", "with", "at", "on", "than",
        "denote", "satisfy", "satisfies", "lies", "represent", "belong", "belongs", "exceed", "exceeds",
        "converges", "tends",
    };

    // Words that only a symbol follows: "x sub a", "the constant a".
    private static readonly HashSet<string> BeforeASymbol = new(StringComparer.OrdinalIgnoreCase)
    {
        "sub", "super", "parameter", "constant", "coefficient", "scalar", "vector", "matrix", "variable", "letter",
    };

    // Words the article follows as often as the symbol: "x plus a" but "plus a constant", "let a be"
    // but "let a model", "given a" but "given a sample". Beside them "a" is the symbol only when what
    // comes next says so (AfterASymbol, another lone letter, an operator); otherwise a noun is to come.
    private static readonly HashSet<string> BeforeEither = new(StringComparer.OrdinalIgnoreCase)
    {
        "plus", "minus", "times", "equals", "over", "given", "point", "let",
    };

    // Signs an article never stands before: "a + b", "a = 2", "a^2".
    private const string Operators = "+-−=<>^_*/×·≤≥≠≈∈|";

    /// <summary>The name of an ASCII letter, as a word an engine pronounces as the letter.</summary>
    public static string Of(char letter) => Names[char.ToLowerInvariant(letter) - 'a'];

    /// <summary>
    /// <paramref name="script"/> with each letter that stands alone as a symbol written as its name.
    /// "I" is left alone (the pronoun and the letter sound the same), an abbreviation's letters
    /// ("e.g.") are not symbols, and "a" is the article unless what stands beside it says otherwise.
    /// </summary>
    public static SpeechForm ForSpeech(string script)
    {
        ArgumentNullException.ThrowIfNull(script);
        System.Text.StringBuilder? sb = null;
        var changes = new List<(TextRange, int)>();
        int copied = 0;
        for (int i = 0; i < script.Length; i++)
        {
            char c = script[i];
            if (!char.IsAsciiLetter(c) || c == 'I') continue;
            if (i > 0 && IsWordChar(script[i - 1])) continue;
            if (i + 1 < script.Length && IsWordChar(script[i + 1])) continue;
            if (IsAbbreviationLetter(script, i)) continue;
            if (c is 'a' or 'A' && !IsSymbolA(script, i)) continue;

            sb ??= new System.Text.StringBuilder(script.Length + 16);
            sb.Append(script, copied, i - copied);
            string name = Of(c);
            changes.Add((new TextRange(sb.Length, name.Length), i));
            sb.Append(name);
            copied = i + 1;
        }
        if (sb is null) return new SpeechForm(script, []);
        sb.Append(script, copied, script.Length - copied);
        return new SpeechForm(sb.ToString(), changes);
    }

    // A letter is alone when nothing that makes a word touches it; the apostrophe keeps "don't" and "y's" whole.
    private static bool IsWordChar(char c) => char.IsLetterOrDigit(c) || c is '\'' or '’';

    private static bool IsAbbreviationLetter(string s, int i)
    {
        // "e.g.", "i.e.", "U.S.": a full stop with a letter straight after it, or straight before a single letter.
        if (i + 2 < s.Length && s[i + 1] == '.' && char.IsLetter(s[i + 2])) return true;
        return i >= 2 && s[i - 1] == '.' && char.IsLetter(s[i - 2]) && (i < 3 || !char.IsLetter(s[i - 3]));
    }

    private static bool IsSymbolA(string s, int i)
    {
        // Nothing glues an article to a bracket or a prime: "a(t)", "a′", "a[i]".
        if (i + 1 < s.Length && s[i + 1] is '(' or '[' or '′' or '″') return true;
        int after = i + 1;
        while (after < s.Length && s[after] == ' ') after++;
        // An article has a noun to come: "a" before the end of a clause is a symbol.
        if (after >= s.Length || s[after] is ',' or '.' or ';' or ':' or ')' or '?' or '!') return true;
        if (Operators.Contains(s[after])) return true;
        string next = WordAt(s, after, out int nextEnd);
        // "a sub-sample" is an article before a hyphenated word.
        bool hyphenated = nextEnd < s.Length && s[nextEnd] == '-';
        if (AfterASymbol.Contains(next) && !hyphenated) return true;

        int before = i - 1;
        while (before >= 0 && s[before] == ' ') before--;
        // An opening bracket or quotation mark can begin a sentence too: "(A similar bound holds.)",
        // and a closing one after a sentence's end: "(see Fig. 2.) A similar", "tight.” A model".
        bool sentenceStart = before < 0 || s[before] is '.' or '?' or '!' or ':' or '(' or '[' or '"' or '“' or '‘'
            || (s[before] is ')' or ']' or '”' or '’' && before > 0 && s[before - 1] is '.' or '?' or '!');
        // A capital "A" inside a sentence is a name ("the matrix A times x"), not the article;
        // before a capitalised word it is a title's or a heading's article ("3.2 A Simple Model").
        bool titleCase = next.Length > 1 && char.IsUpper(next[0]);
        if (s[i] == 'A' && !sentenceStart && !titleCase) return true;
        if (before < 0) return false;
        int start = before;
        while (start >= 0 && char.IsLetter(s[start])) start--;
        string previous = s.Substring(start + 1, before - start);
        if (BeforeASymbol.Contains(previous)) return true;
        // "plus a b": another lone letter after it. "over a window": a noun.
        return BeforeEither.Contains(previous) && next.Length == 1 && !hyphenated;
    }

    private static string WordAt(string s, int start, out int end)
    {
        end = start;
        while (end < s.Length && char.IsLetter(s[end])) end++;
        return s[start..end];
    }
}
