using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace RailNarrator.Inference;

/// <summary>
/// A rule-based reader for LaTeX, used only as an explicit fallback when no language model is
/// configured.
/// </summary>
/// <remarks>
/// <para>
/// This is not a substitute for the model path and is never presented as one: the caller labels the
/// resulting segment <c>LocalRule</c> and attaches a warning telling the reader to check it against
/// the original. What it does provide is an honest mechanical reading — "one over n times the sum
/// from i equals one to n of x sub i" — which is far more useful than the alternative, a speech
/// engine saying "backslash frac one n".
/// </para>
/// <para>
/// Its known limitation is grouping. <c>(X^\top X)^{-1}</c> comes out as a flat sequence rather than
/// "the inverse of the quantity X transpose X", because recovering that phrasing requires
/// understanding the expression, not pattern-matching it. That is precisely why the model path is the
/// default and why this reader's output carries a warning.
/// </para>
/// </remarks>
public static partial class LocalLatexSpeaker
{
    private static readonly Dictionary<string, string> Commands = new(StringComparer.Ordinal)
    {
        // Greek, lower then upper.
        ["alpha"] = "alpha", ["beta"] = "beta", ["gamma"] = "gamma", ["delta"] = "delta",
        ["epsilon"] = "epsilon", ["varepsilon"] = "epsilon", ["zeta"] = "zeta", ["eta"] = "eta",
        ["theta"] = "theta", ["vartheta"] = "theta", ["iota"] = "iota", ["kappa"] = "kappa",
        ["lambda"] = "lambda", ["mu"] = "mu", ["nu"] = "nu", ["xi"] = "xi", ["pi"] = "pi",
        ["varpi"] = "pi", ["rho"] = "rho", ["varrho"] = "rho", ["sigma"] = "sigma", ["tau"] = "tau",
        ["upsilon"] = "upsilon", ["phi"] = "phi", ["varphi"] = "phi", ["chi"] = "chi", ["psi"] = "psi",
        ["omega"] = "omega",
        ["Gamma"] = "capital Gamma", ["Delta"] = "capital Delta", ["Theta"] = "capital Theta",
        ["Lambda"] = "capital Lambda", ["Xi"] = "capital Xi", ["Pi"] = "capital Pi",
        ["Sigma"] = "capital Sigma", ["Upsilon"] = "capital Upsilon", ["Phi"] = "capital Phi",
        ["Psi"] = "capital Psi", ["Omega"] = "capital Omega",

        // Relations. Preserve direction: reversing one of these changes the mathematics.
        ["le"] = "is less than or equal to", ["leq"] = "is less than or equal to", ["leqslant"] = "is less than or equal to",
        ["ge"] = "is greater than or equal to", ["geq"] = "is greater than or equal to", ["geqslant"] = "is greater than or equal to",
        ["ne"] = "is not equal to", ["neq"] = "is not equal to",
        ["approx"] = "is approximately", ["sim"] = "is distributed as", ["simeq"] = "is approximately equal to",
        ["equiv"] = "is identically equal to", ["cong"] = "is congruent to", ["propto"] = "is proportional to",
        ["ll"] = "is much less than", ["gg"] = "is much greater than",
        ["in"] = "is an element of", ["notin"] = "is not an element of", ["ni"] = "contains",
        ["subset"] = "is a subset of", ["subseteq"] = "is a subset of or equal to",
        ["supset"] = "is a superset of", ["supseteq"] = "is a superset of or equal to",
        ["cup"] = "union", ["cap"] = "intersection", ["emptyset"] = "the empty set", ["varnothing"] = "the empty set",
        ["forall"] = "for every", ["exists"] = "there exists", ["nexists"] = "there does not exist",
        ["neg"] = "not", ["lnot"] = "not", ["land"] = "and", ["wedge"] = "and",
        ["lor"] = "or", ["vee"] = "or", ["implies"] = "implies", ["iff"] = "if and only if",
        ["Rightarrow"] = "implies", ["Leftarrow"] = "is implied by", ["Leftrightarrow"] = "if and only if",
        ["to"] = "tends to", ["rightarrow"] = "tends to", ["longrightarrow"] = "tends to",
        ["leftarrow"] = "is assigned", ["mapsto"] = "maps to", ["mid"] = "given", ["nmid"] = "does not divide",
        ["perp"] = "is perpendicular to", ["parallel"] = "is parallel to",

        // Operators and arithmetic.
        ["times"] = "times", ["cdot"] = "times", ["ast"] = "star", ["star"] = "star",
        ["div"] = "divided by", ["pm"] = "plus or minus", ["mp"] = "minus or plus",
        ["infty"] = "infinity", ["partial"] = "the partial derivative symbol", ["nabla"] = "nabla",
        ["top"] = "transpose", ["trans"] = "transpose", ["dagger"] = "dagger",
        ["ldots"] = "dot dot dot", ["cdots"] = "dot dot dot", ["dots"] = "dot dot dot", ["vdots"] = "dot dot dot",

        // Functions.
        ["log"] = "log of", ["ln"] = "the natural log of", ["exp"] = "e to the power", ["lim"] = "the limit",
        ["sin"] = "the sine of", ["cos"] = "the cosine of", ["tan"] = "the tangent of",
        ["arcsin"] = "the arc sine of", ["arccos"] = "the arc cosine of", ["arctan"] = "the arc tangent of",
        ["sinh"] = "the hyperbolic sine of", ["cosh"] = "the hyperbolic cosine of",
        ["max"] = "the maximum of", ["min"] = "the minimum of", ["sup"] = "the supremum of", ["inf"] = "the infimum of",
        ["det"] = "the determinant of", ["dim"] = "the dimension of", ["deg"] = "the degree of",
        ["gcd"] = "the greatest common divisor of", ["arg"] = "the argument of",

        // Formatting wrappers handled by taking their argument verbatim.
        ["mathrm"] = "", ["mathbf"] = "", ["mathit"] = "", ["mathsf"] = "", ["mathcal"] = "",
        ["operatorname"] = "", ["text"] = "", ["mbox"] = "", ["bf"] = "", ["it"] = "",

        // Spacing and delimiters that carry no spoken content.
        ["left"] = "", ["right"] = "", ["quad"] = " ", ["qquad"] = "  ", [";"] = " ", [","] = " ",
        [":"] = " ", ["!"] = "", [" "] = " ", ["big"] = "", ["Big"] = "", ["bigg"] = "", ["Bigg"] = "",
        ["lvert"] = "the absolute value of", ["rvert"] = "", ["lVert"] = "the norm of", ["rVert"] = "",
        ["langle"] = "the inner product of", ["rangle"] = "",
    };

    private static readonly Dictionary<string, string> Accents = new(StringComparer.Ordinal)
    {
        ["bar"] = "bar", ["overline"] = "bar", ["hat"] = "hat", ["widehat"] = "hat",
        ["tilde"] = "tilde", ["widetilde"] = "tilde", ["vec"] = "vector", ["dot"] = "dot",
        ["ddot"] = "double dot", ["check"] = "check", ["acute"] = "acute", ["grave"] = "grave",
    };

    private static readonly Dictionary<string, string> Blackboard = new(StringComparer.Ordinal)
    {
        ["R"] = "the real numbers", ["N"] = "the natural numbers", ["Z"] = "the integers",
        ["Q"] = "the rational numbers", ["C"] = "the complex numbers", ["P"] = "the probability measure",
        ["E"] = "the expectation of",
    };

    private static readonly string[] Ones =
    [
        "zero", "one", "two", "three", "four", "five", "six", "seven", "eight", "nine", "ten",
        "eleven", "twelve", "thirteen", "fourteen", "fifteen", "sixteen", "seventeen", "eighteen", "nineteen",
    ];

    private static readonly string[] Tens = ["", "", "twenty", "thirty", "forty", "fifty", "sixty", "seventy", "eighty", "ninety"];

    /// <summary>
    /// Renders a LaTeX expression as spoken English. Deterministic and offline.
    /// </summary>
    public static string Speak(string latex)
    {
        if (string.IsNullOrWhiteSpace(latex)) return "";
        string cleaned = StripMathDelimiters(latex);
        var output = new StringBuilder();
        Parse(cleaned, 0, cleaned.Length, output, top: true);
        return Tidy(output.ToString());
    }

    private static string StripMathDelimiters(string s)
    {
        s = s.Trim();
        foreach (var pair in new[] { ("$$", "$$"), ("\\[", "\\]"), ("$", "$"), ("\\(", "\\)") })
            if (s.StartsWith(pair.Item1, StringComparison.Ordinal) && s.EndsWith(pair.Item2, StringComparison.Ordinal)
                && s.Length > pair.Item1.Length + pair.Item2.Length)
                return s[pair.Item1.Length..^pair.Item2.Length].Trim();
        return s;
    }

    private static void Parse(string s, int start, int end, StringBuilder output, bool top)
    {
        int i = start;
        while (i < end)
        {
            char c = s[i];

            if (c == '\\')
            {
                i = HandleCommand(s, i, end, output);
                continue;
            }
            if (c == '{')
            {
                int close = MatchingBrace(s, i, end);
                Parse(s, i + 1, close, output, top: false);
                i = close + 1;
                continue;
            }
            if (c == '}') { i++; continue; }

            if (c == '^')
            {
                var (arg, next) = ReadArgument(s, i + 1, end);
                string spoken = SpeakExponent(arg, s);
                output.Append(' ').Append(spoken);
                i = next;
                continue;
            }
            if (c == '_')
            {
                var (arg, next) = ReadArgument(s, i + 1, end);
                output.Append(" sub ").Append(ReadSubscript(arg, s));
                i = next;
                continue;
            }
            if (char.IsDigit(c))
            {
                int j = i;
                while (j < end && (char.IsDigit(s[j]) || s[j] == '.')) j++;
                output.Append(' ').Append(NumberToWords(s[i..j]));
                i = j;
                continue;
            }
            if (c == '/')
            {
                // A simple a/b fraction reads better as "a over b" than "a slash b".
                output.Append(" over");
                i++;
                continue;
            }
            if (InfixOperator(c, out string? word))
            {
                output.Append(' ').Append(word);
                i++;
                continue;
            }
            // A differential: "dx" inside an integral is spoken as "with respect to x", not as the
            // two letters d and x. Without this the integral reads as though it were missing its
            // variable of integration.
            if (c == 'd' && i + 1 < end && char.IsLetter(s[i + 1])
                && (i + 2 >= end || !char.IsLetter(s[i + 2]))
                && output.ToString().Contains("integral", StringComparison.OrdinalIgnoreCase))
            {
                output.Append(" with respect to ").Append(s[i + 1]).Append(' ');
                i += 2;
                continue;
            }
            if (char.IsWhiteSpace(c)) { output.Append(' '); i++; continue; }
            if (c is '(' or '[') { output.Append(" open ").Append(c == '(' ? "parenthesis" : "bracket"); i++; continue; }
            if (c is ')' or ']') { output.Append(" close ").Append(c == ')' ? "parenthesis" : "bracket"); i++; continue; }
            if (c == ',') { output.Append(','); i++; continue; }

            output.Append(c);
            i++;
        }
    }

    private static bool InfixOperator(char c, out string word)
    {
        switch (c)
        {
            case '=': word = "equals"; return true;
            case '+': word = "plus"; return true;
            case '-': word = "minus"; return true;
            case '*': word = "times"; return true;
            case '<': word = "is less than"; return true;
            case '>': word = "is greater than"; return true;
            case '!': word = "factorial"; return true;
            default: word = ""; return false;
        }
    }

    private static int HandleCommand(string s, int i, int end, StringBuilder output)
    {
        int j = i + 1;
        if (j < end && !char.IsLetter(s[j]))
        {
            // A single-character escape such as \, \; \% \{.
            string single = s[j].ToString();
            if (Commands.TryGetValue(single, out var singleWord))
            {
                if (singleWord.Length > 0) output.Append(' ').Append(singleWord).Append(' ');
                return j + 1;
            }
            output.Append(s[j]);
            return j + 1;
        }

        while (j < end && char.IsLetter(s[j])) j++;
        string name = s[(i + 1)..j];
        // Skip a single trailing space, as LaTeX does.
        if (j < end && s[j] == ' ') j++;

        switch (name)
        {
            case "frac" or "dfrac" or "tfrac":
            {
                var (num, afterNum) = ReadArgument(s, j, end);
                var (den, afterDen) = ReadArgument(s, afterNum, end);
                var numText = new StringBuilder();
                Parse(num.Item1, num.Item2, num.Item3, numText, top: false);
                var denText = new StringBuilder();
                Parse(den.Item1, den.Item2, den.Item3, denText, top: false);
                output.Append(' ').Append(Tidy(numText.ToString())).Append(" over ").Append(Tidy(denText.ToString())).Append(' ');
                return afterDen;
            }
            case "sqrt":
            {
                var (arg, after) = ReadArgument(s, j, end);
                var text = new StringBuilder();
                Parse(arg.Item1, arg.Item2, arg.Item3, text, top: false);
                output.Append(" the square root of ").Append(Tidy(text.ToString())).Append(' ');
                return after;
            }
            case "sum":
                EmitBigOperator(s, j, end, output, "the sum", "of");
                return SkipScripts(s, j, end);
            case "prod":
                EmitBigOperator(s, j, end, output, "the product", "of");
                return SkipScripts(s, j, end);
            case "int":
            case "iint":
            case "oint":
                EmitBigOperator(s, j, end, output, name == "oint" ? "the contour integral" : "the integral", "of");
                return SkipScripts(s, j, end);
            case "lim":
                EmitBigOperator(s, j, end, output, "the limit", "of");
                return SkipScripts(s, j, end);
            case "mathbb":
            {
                var (arg, after) = ReadArgument(s, j, end);
                string letters = arg.Item1[arg.Item2..arg.Item3].Trim();
                output.Append(' ').Append(Blackboard.GetValueOrDefault(letters, $"the set {letters}")).Append(' ');
                return after;
            }
            default:
                if (Accents.TryGetValue(name, out var accent))
                {
                    var (arg, after) = ReadArgument(s, j, end);
                    var text = new StringBuilder();
                    Parse(arg.Item1, arg.Item2, arg.Item3, text, top: false);
                    output.Append(' ').Append(Tidy(text.ToString())).Append(' ').Append(accent).Append(' ');
                    return after;
                }
                if (Commands.TryGetValue(name, out var word))
                {
                    // A trailing space is required: the next token may be a bare letter with no
                    // separator of its own, and "not" followed by "p" must read as "not p".
                    if (word.Length > 0) output.Append(' ').Append(word).Append(' ');
                    // A formatting wrapper still has to render its argument.
                    if (name is "mathrm" or "mathbf" or "mathit" or "mathsf" or "mathcal" or "operatorname" or "text" or "mbox")
                    {
                        var (arg, after) = ReadArgument(s, j, end);
                        output.Append(' ').Append(arg.Item1[arg.Item2..arg.Item3].Trim());
                        return after;
                    }
                    return j;
                }
                // An unknown command is spoken as its name rather than dropped: dropping it would
                // silently change the expression, and saying it out loud is audibly wrong in a way the
                // listener can report.
                output.Append(' ').Append(name);
                return j;
        }
    }

    /// <summary>
    /// Emits "the sum from i equals one to n of" style phrasing for a big operator with limits.
    /// </summary>
    private static void EmitBigOperator(string s, int pos, int end, StringBuilder output, string noun, string trailer)
    {
        output.Append(' ').Append(noun);
        var (lower, afterLower) = ReadOptionalScript(s, pos, end, '_');
        var (upper, _) = ReadOptionalScript(s, afterLower, end, '^');

        if (lower is { } lo && upper is { } hi)
            output.Append(" from ").Append(Render(lo)).Append(" to ").Append(Render(hi));
        else if (lower is { } onlyLower)
            output.Append(" as ").Append(Render(onlyLower));
        output.Append(' ').Append(trailer).Append(' ');

        static string Render((string Text, int Start, int End) span)
        {
            var text = new StringBuilder();
            Parse(span.Text, span.Start, span.End, text, top: false);
            return Tidy(text.ToString());
        }
    }

    private static int SkipScripts(string s, int pos, int end)
    {
        int i = pos;
        while (i < end)
        {
            while (i < end && char.IsWhiteSpace(s[i])) i++;
            if (i >= end || (s[i] != '_' && s[i] != '^')) break;
            (_, i) = ReadArgument(s, i + 1, end);
        }
        return i;
    }

    private static ((string Text, int Start, int End)? Arg, int Next) ReadOptionalScript(string s, int pos, int end, char marker)
    {
        int i = pos;
        while (i < end && char.IsWhiteSpace(s[i])) i++;
        if (i >= end || s[i] != marker) return (null, i);
        var (arg, next) = ReadArgument(s, i + 1, end);
        return ((arg.Item1, arg.Item2, arg.Item3), next);
    }

    /// <summary>Reads one LaTeX argument: a braced group or a single token.</summary>
    private static ((string, int, int) Arg, int Next) ReadArgument(string s, int pos, int end)
    {
        int i = pos;
        while (i < end && char.IsWhiteSpace(s[i])) i++;
        if (i >= end) return ((s, end, end), end);

        if (s[i] == '{')
        {
            int close = MatchingBrace(s, i, end);
            return ((s, i + 1, close), close + 1);
        }
        if (s[i] == '\\')
        {
            int j = i + 1;
            if (j < end && !char.IsLetter(s[j])) return ((s, i, j + 1), j + 1);
            while (j < end && char.IsLetter(s[j])) j++;
            return ((s, i, j), j);
        }
        return ((s, i, i + 1), i + 1);
    }

    private static int MatchingBrace(string s, int open, int end)
    {
        int depth = 0;
        for (int i = open; i < end; i++)
        {
            if (s[i] == '\\' && i + 1 < end) { i++; continue; }
            if (s[i] == '{') depth++;
            else if (s[i] == '}') { depth--; if (depth == 0) return i; }
        }
        return end;   // unbalanced: treat the rest as the group rather than losing it
    }

    private static string SpeakExponent((string, int, int) arg, string s)
    {
        string raw = s[arg.Item2..arg.Item3].Trim();
        // Common cases get natural phrasing; everything else falls back to "to the power".
        return raw switch
        {
            "2" => "squared",
            "3" => "cubed",
            "-1" => "to the minus one",
            "-2" => "to the minus two",
            "T" or "\\top" or "\\mathrm{T}" => "transpose",
            "*" or "\\ast" => "star",
            _ => "to the power " + RenderSubExpression(arg, s),
        };
    }

    private static string ReadSubscript((string, int, int) arg, string s)
    {
        string raw = s[arg.Item2..arg.Item3].Trim();
        // "i=1" inside a subscript reads as "i equals one"; a bare symbol reads as itself.
        return RenderSubExpression(arg, s);
    }

    private static string RenderSubExpression((string, int, int) arg, string s)
    {
        var text = new StringBuilder();
        Parse(arg.Item1, arg.Item2, arg.Item3, text, top: false);
        return Tidy(text.ToString());
    }

    /// <summary>
    /// Renders a numeric literal in words, including the simple fractions and decimals that appear
    /// constantly in prose mathematics.
    /// </summary>
    public static string NumberToWords(string literal)
    {
        if (literal.Contains('.'))
        {
            var parts = literal.Split('.', 2);
            string whole = IntegerToWords(parts[0]);
            string fractionDigits = string.Join(" ", parts[1].Select(d => Ones[d - '0']));
            return $"{whole} point {fractionDigits}".Trim();
        }
        return IntegerToWords(literal);
    }

    private static string IntegerToWords(string digits)
    {
        if (!long.TryParse(digits, NumberStyles.Integer, CultureInfo.InvariantCulture, out long n))
            return digits;
        return IntegerToWords(n);
    }

    private static string IntegerToWords(long n)
    {
        if (n < 0) return "minus " + IntegerToWords(-n);
        if (n < 20) return Ones[n];
        if (n < 100) return Tens[n / 10] + (n % 10 == 0 ? "" : " " + Ones[n % 10]);
        if (n < 1000) return Ones[n / 100] + " hundred" + (n % 100 == 0 ? "" : " " + IntegerToWords(n % 100));
        if (n < 1_000_000) return IntegerToWords(n / 1000) + " thousand" + (n % 1000 == 0 ? "" : " " + IntegerToWords(n % 1000));
        if (n < 1_000_000_000) return IntegerToWords(n / 1_000_000) + " million" + (n % 1_000_000 == 0 ? "" : " " + IntegerToWords(n % 1_000_000));
        return n.ToString(CultureInfo.InvariantCulture);
    }

    private static string Tidy(string s)
        => WhitespaceRun().Replace(s.Trim(), " ");

    [GeneratedRegex(@"\s+")]
    private static partial Regex WhitespaceRun();
}
