using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace RailNarrator.Inference;

/// <summary>
/// A rule-based reader for LaTeX ("mathspeak"): the explicit fallback when no language model is
/// configured, and the reading of a passage the model transcribed but could not interpret.
/// </summary>
/// <remarks>
/// <para>
/// This is not a substitute for interpretation and is never presented as one: the caller labels the
/// resulting segment <c>LocalRule</c> and attaches a warning telling the reader to check it against
/// the original. What it gives is an honest mechanical reading — "one over n times the sum from i
/// equals one to n of x sub i" — instead of a speech engine saying "backslash frac one n".
/// </para>
/// <para>
/// The expression is parsed into a tree first and then spoken, so grouping survives: a fraction's
/// numerator and denominator, a sum's limits, a bracketed quantity raised to a power. The phrasing
/// follows ClearSpeak (the convention MathCAT implements): "x squared", "x to the n-th power",
/// "two thirds", "the fraction with numerator … and denominator …", "the quantity … end quantity",
/// "the square root of …, end root". It still cannot tell what an expression <i>means</i>; that is
/// why the model path is the default and why this reader's output carries a warning.
/// </para>
/// </remarks>
public static partial class LocalLatexSpeaker
{
    /// <summary>Changes whenever the phrasing changes; part of cache keys for readings made with it.</summary>
    public const string Version = "clearspeak/2";

    /// <summary>Renders a LaTeX expression as spoken English. Deterministic and offline.</summary>
    public static string Speak(string latex)
    {
        if (string.IsNullOrWhiteSpace(latex)) return "";
        try
        {
            var parser = new Parser(Tokenize(StripMathDelimiters(latex)));
            var row = parser.ParseRow(_ => false);
            return Tidy(new Speaker().Row(row.Items));
        }
        catch (Exception ex) when (ex is InvalidOperationException or ArgumentException or IndexOutOfRangeException)
        {
            // Malformed input is read token by token rather than not at all.
            return Tidy(string.Join(" ", Tokenize(latex).Select(t => t.Kind == TokenKind.Command ? CommandWord(t.Text) : t.Text)));
        }
    }

    /// <summary>
    /// Text with maths in <c>$…$</c>, <c>$$…$$</c>, <c>\(…\)</c> or <c>\[…\]</c>: the text as it is,
    /// each piece of maths in words. Returns null when a delimiter is left open, so a caller does not
    /// speak a half-converted sentence.
    /// </summary>
    public static string? SpeakMixed(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return "";
        // Environment markers a model leaves outside the delimiters ("…$\mathbf{0}$.\end{equation}").
        text = StrayMarkup().Replace(text, " ");
        var output = new StringBuilder();
        int at = 0;
        while (at < text.Length)
        {
            var (start, open, close) = NextMath(text, at);
            if (start < 0) { output.Append(text, at, text.Length - at); break; }
            int body = start + open.Length;
            int end = text.IndexOf(close, body, StringComparison.Ordinal);
            if (end < 0) return null;
            output.Append(text, at, start - at);
            string spoken = Speak(text[body..end]);
            if (spoken.Length > 0) output.Append(' ').Append(spoken).Append(' ');
            at = end + close.Length;
        }
        return Tidy(SpaceBeforePunctuation().Replace(output.ToString(), "$1"));
    }

    private static (int Start, string Open, string Close) NextMath(string s, int from)
    {
        (int, string, string) best = (-1, "", "");
        foreach (var (open, close) in new[] { ("$$", "$$"), ("\\[", "\\]"), ("\\(", "\\)"), ("$", "$") })
        {
            int i = s.IndexOf(open, from, StringComparison.Ordinal);
            if (i >= 0 && (best.Item1 < 0 || i < best.Item1)) best = (i, open, close);
        }
        return best;
    }

    private static string StripMathDelimiters(string s)
    {
        s = s.Trim();
        foreach (var (open, close) in new[] { ("$$", "$$"), ("\\[", "\\]"), ("$", "$"), ("\\(", "\\)") })
            if (s.StartsWith(open, StringComparison.Ordinal) && s.EndsWith(close, StringComparison.Ordinal)
                && s.Length > open.Length + close.Length)
                return s[open.Length..^close.Length].Trim();
        return s;
    }

    // ── Vocabulary ────────────────────────────────────────────────────────────────────────────

    private static readonly Dictionary<string, string> Symbols = new(StringComparer.Ordinal)
    {
        // Greek, lower then upper.
        ["alpha"] = "alpha", ["beta"] = "beta", ["gamma"] = "gamma", ["delta"] = "delta",
        ["epsilon"] = "epsilon", ["varepsilon"] = "epsilon", ["zeta"] = "zeta", ["eta"] = "eta",
        ["theta"] = "theta", ["vartheta"] = "theta", ["iota"] = "iota", ["kappa"] = "kappa",
        ["lambda"] = "lambda", ["mu"] = "mu", ["nu"] = "nu", ["xi"] = "xi", ["pi"] = "pi",
        ["varpi"] = "pi", ["rho"] = "rho", ["varrho"] = "rho", ["sigma"] = "sigma", ["varsigma"] = "sigma",
        ["tau"] = "tau", ["upsilon"] = "upsilon", ["phi"] = "phi", ["varphi"] = "phi", ["chi"] = "chi",
        ["psi"] = "psi", ["omega"] = "omega", ["ell"] = "ell",
        ["Gamma"] = "capital Gamma", ["Delta"] = "capital Delta", ["Theta"] = "capital Theta",
        ["Lambda"] = "capital Lambda", ["Xi"] = "capital Xi", ["Pi"] = "capital Pi",
        ["Sigma"] = "capital Sigma", ["Upsilon"] = "capital Upsilon", ["Phi"] = "capital Phi",
        ["Psi"] = "capital Psi", ["Omega"] = "capital Omega",
        ["infty"] = "infinity", ["partial"] = "partial", ["nabla"] = "nabla", ["emptyset"] = "the empty set",
        ["varnothing"] = "the empty set", ["ldots"] = "dot dot dot", ["cdots"] = "dot dot dot",
        ["dots"] = "dot dot dot", ["vdots"] = "dot dot dot", ["ddots"] = "dot dot dot", ["top"] = "transpose",
        ["intercal"] = "transpose", ["prime"] = "prime", ["ast"] = "star", ["star"] = "star", ["dagger"] = "dagger",
        ["hbar"] = "h bar", ["imath"] = "i", ["jmath"] = "j", ["aleph"] = "aleph",
    };

    /// <summary>Infix words: relations, then binary operators. A relation separates clauses.</summary>
    private static readonly Dictionary<string, string> Relations = new(StringComparer.Ordinal)
    {
        ["="] = "equals", ["<"] = "is less than", [">"] = "is greater than",
        ["le"] = "is less than or equal to", ["leq"] = "is less than or equal to", ["leqslant"] = "is less than or equal to",
        ["ge"] = "is greater than or equal to", ["geq"] = "is greater than or equal to", ["geqslant"] = "is greater than or equal to",
        ["ne"] = "is not equal to", ["neq"] = "is not equal to", ["approx"] = "is approximately",
        ["sim"] = "is distributed as", ["simeq"] = "is approximately equal to", ["equiv"] = "is identically equal to",
        ["cong"] = "is congruent to", ["propto"] = "is proportional to", ["ll"] = "is much less than",
        ["gg"] = "is much greater than", ["in"] = "is an element of", ["notin"] = "is not an element of",
        ["ni"] = "contains", ["subset"] = "is a subset of", ["subseteq"] = "is a subset of or equal to",
        ["supset"] = "is a superset of", ["supseteq"] = "is a superset of or equal to",
        ["implies"] = "implies", ["iff"] = "if and only if", ["Rightarrow"] = "implies",
        ["Leftarrow"] = "is implied by", ["Leftrightarrow"] = "if and only if", ["to"] = "tends to",
        ["rightarrow"] = "tends to", ["longrightarrow"] = "tends to", ["leftarrow"] = "is assigned",
        ["mapsto"] = "maps to", ["perp"] = "is perpendicular to", ["parallel"] = "is parallel to",
        ["coloneqq"] = "is defined as", ["triangleq"] = "is defined as", ["doteq"] = "is defined as",
        [":="] = "is defined as",
    };

    private static readonly Dictionary<string, string> Operators = new(StringComparer.Ordinal)
    {
        ["+"] = "plus", ["-"] = "minus", ["*"] = "times", ["times"] = "times", ["cdot"] = "times",
        ["div"] = "divided by", ["pm"] = "plus or minus", ["mp"] = "minus or plus", ["cup"] = "union",
        ["cap"] = "intersection", ["setminus"] = "minus", ["circ"] = "composed with", ["otimes"] = "tensor",
        ["oplus"] = "direct sum", ["odot"] = "times", ["mid"] = "given", ["nmid"] = "does not divide",
        ["vert"] = "given", ["land"] = "and", ["wedge"] = "and", ["lor"] = "or", ["vee"] = "or",
        ["forall"] = "for every", ["exists"] = "there exists", ["nexists"] = "there does not exist",
        ["neg"] = "not", ["lnot"] = "not", ["/"] = "over", ["!"] = "factorial",
    };

    /// <summary>Named functions: spoken before their argument.</summary>
    private static readonly Dictionary<string, string> Functions = new(StringComparer.Ordinal)
    {
        ["log"] = "the log of", ["ln"] = "the natural log of", ["lg"] = "the log of", ["exp"] = "the exponential of",
        ["sin"] = "the sine of", ["cos"] = "the cosine of", ["tan"] = "the tangent of", ["cot"] = "the cotangent of",
        ["sec"] = "the secant of", ["csc"] = "the cosecant of", ["arcsin"] = "the arc sine of",
        ["arccos"] = "the arc cosine of", ["arctan"] = "the arc tangent of", ["sinh"] = "the hyperbolic sine of",
        ["cosh"] = "the hyperbolic cosine of", ["tanh"] = "the hyperbolic tangent of",
        ["det"] = "the determinant of", ["dim"] = "the dimension of", ["deg"] = "the degree of",
        ["gcd"] = "the greatest common divisor of", ["arg"] = "the argument of", ["ker"] = "the kernel of",
        ["Pr"] = "the probability of", ["Var"] = "the variance of", ["var"] = "the variance of",
        ["Cov"] = "the covariance of", ["cov"] = "the covariance of", ["Corr"] = "the correlation of",
        ["E"] = "the expectation of", ["tr"] = "the trace of", ["trace"] = "the trace of", ["diag"] = "diag",
        ["rank"] = "the rank of", ["vec"] = "vec", ["vech"] = "vech", ["sgn"] = "the sign of", ["sign"] = "the sign of",
    };

    /// <summary>Big operators and limits: their scripts are a range, not a sub- or superscript.</summary>
    private static readonly Dictionary<string, string> BigOperators = new(StringComparer.Ordinal)
    {
        ["sum"] = "the sum", ["prod"] = "the product", ["int"] = "the integral", ["iint"] = "the double integral",
        ["iiint"] = "the triple integral", ["oint"] = "the contour integral", ["lim"] = "the limit",
        ["limsup"] = "the limit superior", ["liminf"] = "the limit inferior", ["max"] = "the maximum",
        ["min"] = "the minimum", ["sup"] = "the supremum", ["inf"] = "the infimum", ["argmax"] = "the arg max",
        ["argmin"] = "the arg min", ["bigcup"] = "the union", ["bigcap"] = "the intersection",
    };

    private static readonly Dictionary<string, (string Word, bool Prefix)> Accents = new(StringComparer.Ordinal)
    {
        ["bar"] = ("bar", false), ["overline"] = ("bar", false), ["hat"] = ("hat", false), ["widehat"] = ("hat", false),
        ["tilde"] = ("tilde", false), ["widetilde"] = ("tilde", false), ["vec"] = ("vector", true),
        ["overrightarrow"] = ("vector", true), ["dot"] = ("dot", false), ["ddot"] = ("double dot", false),
        ["check"] = ("check", false), ["breve"] = ("breve", false), ["acute"] = ("acute", false),
        ["grave"] = ("grave", false), ["underline"] = ("", false),
    };

    private static readonly Dictionary<string, string> Blackboard = new(StringComparer.Ordinal)
    {
        ["R"] = "the real numbers", ["N"] = "the natural numbers", ["Z"] = "the integers",
        ["Q"] = "the rational numbers", ["C"] = "the complex numbers", ["P"] = "the probability of",
        ["E"] = "the expectation of", ["1"] = "the indicator of", ["I"] = "the indicator of",
    };

    /// <summary>Wrappers whose argument is spoken as it is.</summary>
    private static readonly HashSet<string> StyleWrappers = new(StringComparer.Ordinal)
    {
        "mathrm", "mathbf", "mathit", "mathsf", "mathcal", "mathscr", "mathfrak", "boldsymbol", "bm", "pmb",
        "mathnormal", "displaystyle", "textstyle", "scriptstyle", "rm", "bf", "it", "cal",
    };

    private static readonly HashSet<string> Silent = new(StringComparer.Ordinal)
    {
        ",", ";", ":", "!", " ", "quad", "qquad", "big", "Big", "bigg", "Bigg", "bigl", "bigr", "Bigl", "Bigr",
        "biggl", "biggr", "Biggl", "Biggr", "nonumber", "notag", "label", "limits", "nolimits", "middle", "tag",
        "displaystyle", "textstyle", "scriptstyle",
    };

    /// <summary>Unicode a model may write instead of the command.</summary>
    private static readonly Dictionary<char, string> UnicodeCommands = new()
    {
        ['α'] = "alpha", ['β'] = "beta", ['γ'] = "gamma", ['δ'] = "delta", ['ε'] = "varepsilon", ['ϵ'] = "epsilon",
        ['ζ'] = "zeta", ['η'] = "eta", ['θ'] = "theta", ['ι'] = "iota", ['κ'] = "kappa", ['λ'] = "lambda",
        ['μ'] = "mu", ['ν'] = "nu", ['ξ'] = "xi", ['π'] = "pi", ['ρ'] = "rho", ['σ'] = "sigma", ['τ'] = "tau",
        ['υ'] = "upsilon", ['φ'] = "varphi", ['ϕ'] = "phi", ['χ'] = "chi", ['ψ'] = "psi", ['ω'] = "omega",
        ['Γ'] = "Gamma", ['Δ'] = "Delta", ['Θ'] = "Theta", ['Λ'] = "Lambda", ['Ξ'] = "Xi", ['Π'] = "Pi",
        ['Σ'] = "Sigma", ['Φ'] = "Phi", ['Ψ'] = "Psi", ['Ω'] = "Omega", ['ℓ'] = "ell", ['∂'] = "partial",
        ['∞'] = "infty", ['∑'] = "sum", ['∏'] = "prod", ['∫'] = "int", ['≤'] = "leq", ['≥'] = "geq",
        ['≠'] = "neq", ['≈'] = "approx", ['≡'] = "equiv", ['∈'] = "in", ['∉'] = "notin", ['×'] = "times",
        ['·'] = "cdot", ['±'] = "pm", ['→'] = "to", ['⇒'] = "Rightarrow", ['∀'] = "forall", ['∃'] = "exists",
        ['∇'] = "nabla", ['…'] = "ldots", ['⋯'] = "cdots", ['∗'] = "ast", ['∼'] = "sim", ['⊂'] = "subset",
        ['⊆'] = "subseteq", ['∪'] = "cup", ['∩'] = "cap", ['∣'] = "mid", ['√'] = "sqrt", ['⊤'] = "top",
    };

    private static readonly string[] Ones =
    [
        "zero", "one", "two", "three", "four", "five", "six", "seven", "eight", "nine", "ten",
        "eleven", "twelve", "thirteen", "fourteen", "fifteen", "sixteen", "seventeen", "eighteen", "nineteen",
    ];

    private static readonly string[] Tens = ["", "", "twenty", "thirty", "forty", "fifty", "sixty", "seventy", "eighty", "ninety"];

    private static readonly string[] Ordinals =
    [
        "zeroth", "first", "second", "third", "fourth", "fifth", "sixth", "seventh", "eighth", "ninth", "tenth",
        "eleventh", "twelfth", "thirteenth", "fourteenth", "fifteenth", "sixteenth", "seventeenth", "eighteenth",
        "nineteenth", "twentieth",
    ];

    private static readonly HashSet<string> FunctionLetters = new(StringComparer.Ordinal)
    {
        "f", "g", "h", "F", "G", "H", "P", "L", "N", "Q", "ell", "phi", "varphi", "psi", "Phi", "Psi",
    };

    private static string CommandWord(string name)
        => Symbols.GetValueOrDefault(name) ?? Relations.GetValueOrDefault(name) ?? Operators.GetValueOrDefault(name)
           ?? Functions.GetValueOrDefault(name) ?? BigOperators.GetValueOrDefault(name) ?? (Silent.Contains(name) ? "" : name);

    // ── Tokens ────────────────────────────────────────────────────────────────────────────────

    private enum TokenKind { Command, Letter, Number, Char, Open, Close, Sub, Sup, Align, NewRow, Raw }

    /// <summary>Commands whose braced argument is prose: its spaces are words apart, so it is kept raw.</summary>
    private static readonly HashSet<string> TextCommands = new(StringComparer.Ordinal)
    {
        "text", "textrm", "textit", "textbf", "mbox", "textnormal", "hbox", "operatorname", "mathop",
    };

    private readonly record struct Token(TokenKind Kind, string Text);

    private static List<Token> Tokenize(string s)
    {
        var tokens = new List<Token>();
        for (int i = 0; i < s.Length;)
        {
            char c = s[i];
            if (c == '\\')
            {
                int j = i + 1;
                if (j < s.Length && char.IsAsciiLetter(s[j]))
                {
                    while (j < s.Length && char.IsAsciiLetter(s[j])) j++;
                    string name = s[(i + 1)..j];
                    tokens.Add(new(TokenKind.Command, name));
                    int k = j;
                    while (k < s.Length && s[k] == ' ') k++;
                    if (TextCommands.Contains(name) && k < s.Length && s[k] == '{')
                    {
                        int depth = 0, end = k;
                        for (; end < s.Length; end++)
                        {
                            if (s[end] == '{') depth++;
                            else if (s[end] == '}' && --depth == 0) break;
                        }
                        if (end < s.Length)
                        {
                            string raw = s[(k + 1)..end].Replace("{", "", StringComparison.Ordinal).Replace("}", "", StringComparison.Ordinal);
                            tokens.Add(new(TokenKind.Raw, string.Join(' ', raw.Split(' ', StringSplitOptions.RemoveEmptyEntries))));
                            j = end + 1;
                        }
                    }
                }
                else if (j < s.Length && s[j] == '\\') { tokens.Add(new(TokenKind.NewRow, "\\\\")); j++; }
                else if (j < s.Length) { tokens.Add(new(TokenKind.Command, s[j].ToString())); j++; }
                i = j;
                continue;
            }
            if (char.IsWhiteSpace(c)) { i++; continue; }
            if (char.IsAsciiDigit(c) || (c == '.' && i + 1 < s.Length && char.IsAsciiDigit(s[i + 1])))
            {
                int j = i;
                while (j < s.Length && (char.IsAsciiDigit(s[j]) || (s[j] == '.' && j + 1 < s.Length && char.IsAsciiDigit(s[j + 1])))) j++;
                tokens.Add(new(TokenKind.Number, s[i..j]));
                i = j;
                continue;
            }
            if (c == ':' && i + 1 < s.Length && s[i + 1] == '=') { tokens.Add(new(TokenKind.Command, ":=")); i += 2; continue; }
            if (UnicodeCommands.TryGetValue(c, out var command)) { tokens.Add(new(TokenKind.Command, command)); i++; continue; }
            tokens.Add(c switch
            {
                '{' => new(TokenKind.Open, "{"),
                '}' => new(TokenKind.Close, "}"),
                '_' => new(TokenKind.Sub, "_"),
                '^' => new(TokenKind.Sup, "^"),
                '&' => new(TokenKind.Align, "&"),
                '−' or '–' => new(TokenKind.Char, "-"),
                '′' => new(TokenKind.Char, "'"),
                _ when char.IsLetter(c) => new(TokenKind.Letter, c.ToString()),
                _ => new(TokenKind.Char, c.ToString()),
            });
            i++;
        }
        return tokens;
    }

    // ── Tree ──────────────────────────────────────────────────────────────────────────────────

    private abstract record Node;
    private sealed record Row(List<Node> Items) : Node;
    private sealed record Sym(string Name, bool IsCommand) : Node;
    private sealed record Num(string Text) : Node;
    private sealed record Script(Node Base, Node? Sub, Node? Sup) : Node;
    private sealed record Frac(Node Numerator, Node Denominator) : Node;
    private sealed record Root(Node Body, Node? Index) : Node;
    private sealed record Accent(Node Body, string Word, bool Prefix) : Node;
    private sealed record Fenced(string Open, string Close, Row Body) : Node;
    private sealed record Function(string Name) : Node;
    private sealed record BigOp(string Name) : Node;
    private sealed record Words(string Text) : Node;
    private sealed record Arrow(string Label) : Node;
    private sealed record Table(string Environment, List<List<Row>> Rows) : Node;

    private sealed class Parser(List<Token> tokens)
    {
        private int _at;

        private Token? Peek => _at < tokens.Count ? tokens[_at] : null;

        private bool IsChar(Token? t, string text) => t is { Kind: TokenKind.Char } && t.Value.Text == text;

        public Row ParseRow(Func<Token, bool> stop)
        {
            var items = new List<Node>();
            while (Peek is { } t && !stop(t))
            {
                if (t.Kind == TokenKind.Close) { _at++; continue; }   // a stray brace
                var atom = ParseAtom();
                if (atom is null) continue;
                items.Add(ParseScripts(atom));
            }
            return new Row(PairBars(items));
        }

        private Node ParseScripts(Node atom)
        {
            Node? sub = null, sup = null;
            while (Peek is { } t)
            {
                if (t.Kind == TokenKind.Sub && sub is null) { _at++; sub = ParseArgument(); }
                else if (t.Kind == TokenKind.Sup && sup is null) { _at++; sup = ParseArgument(); }
                else if (IsChar(t, "'"))
                {
                    int primes = 0;
                    while (IsChar(Peek, "'")) { _at++; primes++; }
                    sup = new Words(primes switch { 1 => "prime", 2 => "double prime", _ => "triple prime" });
                }
                else break;
            }
            return sub is null && sup is null ? atom : new Script(atom, sub, sup);
        }

        /// <summary>A braced group or a single atom (a script or a command's argument).</summary>
        private Node ParseArgument()
        {
            if (Peek is not { } t) return new Row([]);
            if (t.Kind == TokenKind.Open) { _at++; var row = ParseRow(x => x.Kind == TokenKind.Close); _at++; return Simplify(row); }
            return ParseAtom() ?? new Row([]);
        }

        private string RawArgument()
        {
            if (Peek is not { } t) return "";
            if (t.Kind is not TokenKind.Open) { _at++; return t.Text; }
            _at++;
            var sb = new StringBuilder();
            int depth = 1;
            while (Peek is { } x)
            {
                _at++;
                if (x.Kind == TokenKind.Open) depth++;
                else if (x.Kind == TokenKind.Close && --depth == 0) break;
                if (sb.Length > 0 && (x.Kind is TokenKind.Letter or TokenKind.Number) is false) sb.Append(' ');
                sb.Append(x.Text);
            }
            return sb.ToString();
        }

        private void SkipOptionalArgument()
        {
            if (!IsChar(Peek, "[")) return;
            while (Peek is { } t && !IsChar(t, "]")) _at++;
            _at++;
        }

        private static Node Simplify(Row row) => row.Items.Count == 1 ? row.Items[0] : row;

        private Node? ParseAtom()
        {
            var t = Peek!.Value;
            _at++;
            switch (t.Kind)
            {
                case TokenKind.Open:
                {
                    var row = ParseRow(x => x.Kind == TokenKind.Close);
                    _at++;
                    return Simplify(row);
                }
                case TokenKind.Number: return new Num(t.Text);
                case TokenKind.Letter: return new Sym(t.Text, false);
                case TokenKind.Sub or TokenKind.Sup: return ParseArgument();   // a script with no base
                case TokenKind.Align: return new Words(",");
                case TokenKind.NewRow: return new Words(";");
                case TokenKind.Char:
                    if (t.Text is "(" or "[")
                    {
                        var body = ParseRow(x => IsChar(x, ")") || IsChar(x, "]")
                                                 || (x.Kind == TokenKind.Command && x.Text is "right"));
                        string close = Peek is { Kind: TokenKind.Char } c ? c.Text : ")";
                        if (Peek is { Kind: TokenKind.Char }) _at++;
                        return new Fenced(t.Text, close, body);
                    }
                    if (t.Text is ")" or "]") return null;   // unbalanced
                    return new Sym(t.Text, false);
                case TokenKind.Command:
                    return ParseCommand(t.Text);
                default:
                    return null;
            }
        }

        private Node? ParseCommand(string name)
        {
            switch (name)
            {
                case "frac" or "dfrac" or "tfrac" or "cfrac":
                    return new Frac(ParseArgument(), ParseArgument());
                case "binom" or "dbinom" or "tbinom":
                {
                    var n = ParseArgument();
                    var k = ParseArgument();
                    return new Row([n, new Words("choose"), k]);
                }
                case "sqrt":
                {
                    Node? index = null;
                    if (IsChar(Peek, "["))
                    {
                        _at++;
                        index = Simplify(ParseRow(x => IsChar(x, "]")));
                        _at++;
                    }
                    return new Root(ParseArgument(), index);
                }
                case "left":
                {
                    string open = Peek is { } o ? o.Text : "(";
                    _at++;
                    var body = ParseRow(x => x.Kind == TokenKind.Command && x.Text == "right");
                    _at++;   // \right
                    string close = Peek is { } c ? c.Text : ")";
                    _at++;
                    return new Fenced(open, close, body);
                }
                case "right": return null;
                case "begin":
                {
                    string env = RawArgument().Replace(" ", "", StringComparison.Ordinal);
                    if (env is "array" or "tabular" && Peek is { Kind: TokenKind.Open }) RawArgument();   // column spec
                    var rows = new List<List<Row>> { new() { } };
                    while (Peek is { } t && !(t.Kind == TokenKind.Command && t.Text == "end"))
                    {
                        var cell = ParseRow(x => x.Kind is TokenKind.Align or TokenKind.NewRow
                                                 || (x.Kind == TokenKind.Command && x.Text == "end"));
                        rows[^1].Add(cell);
                        if (Peek is { Kind: TokenKind.Align }) _at++;
                        else if (Peek is { Kind: TokenKind.NewRow }) { _at++; rows.Add([]); }
                    }
                    _at++;
                    RawArgument();
                    rows.RemoveAll(r => r.All(c => c.Items.Count == 0));
                    return new Table(env, rows);
                }
                case "end": RawArgument(); return null;
                case "text" or "textrm" or "textit" or "textbf" or "mbox" or "textnormal" or "hbox":
                    return new Words(RawArgument());
                case "operatorname" or "mathop":
                {
                    string op = RawArgument().Replace(" ", "", StringComparison.Ordinal);
                    if (op.EndsWith('*')) op = op[..^1];
                    return BigOperators.ContainsKey(op) ? new BigOp(op) : new Function(op);
                }
                case "mathbb" or "mathds" or "mathbbm":
                {
                    string letter = RawArgument().Trim();
                    return Blackboard.TryGetValue(letter, out var word)
                        ? (word.EndsWith(" of", StringComparison.Ordinal) ? new Function(word) : new Words(word))
                        : new Words("the set " + letter);
                }
                case "xrightarrow" or "xlongrightarrow":
                    SkipOptionalArgument();
                    return new Arrow(RawArgument().Trim());
                case "overset" or "stackrel" or "underset":
                {
                    var over = RawArgument().Trim();
                    var body = ParseArgument();
                    return body is Sym { IsCommand: true, Name: "to" or "rightarrow" or "longrightarrow" } ? new Arrow(over) : body;
                }
                case "overbrace" or "underbrace":
                    return ParseArgument();
                case "pmod":
                    return new Row([new Words("modulo"), ParseArgument()]);
                case "bmod" or "mod":
                    return new Words("modulo");
            }
            if (Silent.Contains(name)) return null;
            if (StyleWrappers.Contains(name))
            {
                if (Peek is not { Kind: TokenKind.Open }) return null;   // a switch such as \rm
                if (name is "mathrm" && LooksLikeWord(out string word)) return Functions.ContainsKey(word) ? new Function(word) : new Words(word);
                return ParseArgument();
            }
            if (Accents.TryGetValue(name, out var accent)) return new Accent(ParseArgument(), accent.Word, accent.Prefix);
            if (BigOperators.ContainsKey(name)) return new BigOp(name);
            if (Functions.ContainsKey(name)) return new Function(name);
            if (name is "{" or "lbrace") return ParseBraced("{", x => x.Kind == TokenKind.Command && x.Text is "}" or "rbrace");
            if (name is "|" or "Vert" or "lVert") return ParseBraced("‖", x => x.Kind == TokenKind.Command && x.Text is "|" or "Vert" or "rVert");
            if (name is "lvert") return ParseBraced("|", x => x.Kind == TokenKind.Command && x.Text is "rvert");
            if (name is "langle") return ParseBraced("⟨", x => x.Kind == TokenKind.Command && x.Text is "rangle");
            if (name is "lfloor" or "lceil") return ParseBraced(name, x => x.Kind == TokenKind.Command && x.Text is "rfloor" or "rceil");
            return new Sym(name, true);
        }

        private Fenced ParseBraced(string open, Func<Token, bool> close)
        {
            var body = ParseRow(close);
            string closing = Peek is { } c ? c.Text : "";
            _at++;
            return new Fenced(open, closing, body);
        }

        /// <summary>\mathrm{Var}: a multi-letter upright name, spoken as a word.</summary>
        private bool LooksLikeWord(out string word)
        {
            word = "";
            int at = _at + 1;
            var sb = new StringBuilder();
            while (at < tokens.Count && tokens[at].Kind == TokenKind.Letter) sb.Append(tokens[at++].Text);
            if (at >= tokens.Count || tokens[at].Kind != TokenKind.Close || sb.Length < 2) return false;
            word = sb.ToString();
            _at = at + 1;
            return true;
        }

        /// <summary>|x| as an absolute value when the bars pair up; a lone bar is "given".</summary>
        private static List<Node> PairBars(List<Node> items)
        {
            var result = new List<Node>();
            for (int i = 0; i < items.Count; i++)
            {
                if (items[i] is Sym { Name: "|", IsCommand: false })
                {
                    int close = items.FindIndex(i + 1, n => n is Sym { Name: "|", IsCommand: false } or Script { Base: Sym { Name: "|", IsCommand: false } });
                    if (close > i + 1)
                    {
                        var body = new Row(items.GetRange(i + 1, close - i - 1));
                        Node fenced = new Fenced("|", "|", body);
                        if (items[close] is Script s) fenced = s with { Base = fenced };
                        result.Add(fenced);
                        i = close;
                        continue;
                    }
                }
                result.Add(items[i]);
            }
            return result;
        }
    }

    // ── Speech ────────────────────────────────────────────────────────────────────────────────

    private sealed class Speaker
    {
        private int _integrals;

        public string Row(IReadOnlyList<Node> items)
        {
            var parts = new List<string>();
            for (int i = 0; i < items.Count; i++)
            {
                var node = items[i];
                var next = i + 1 < items.Count ? items[i + 1] : null;
                var previous = i > 0 ? items[i - 1] : null;

                // A named function, or a function letter followed by brackets: "f of x".
                if (node is Function f && next is not null)
                {
                    parts.Add(FunctionWord(f.Name));
                    parts.Add(Argument(next));
                    i++;
                    continue;
                }
                if (IsFunctionApplication(node, next, previous))
                {
                    parts.Add(Speak(node) + " of " + Row(((Fenced)next!).Body.Items));
                    i++;
                    continue;
                }
                if (node is Script { Base: Function sf } && next is not null)
                {
                    parts.Add(FunctionWord(sf.Name) + " " + ScriptTail((Script)node));
                    parts.Add(Argument(next));
                    i++;
                    continue;
                }
                // The differential of an integral: "with respect to x".
                if (_integrals > 0 && node is Sym { Name: "d", IsCommand: false } && next is Sym or Script)
                {
                    parts.Add("with respect to " + Speak(next));
                    _integrals--;
                    i++;
                    continue;
                }
                if (node is Sym { Name: "-", IsCommand: false } && (previous is null || IsInfix(previous)))
                {
                    parts.Add("negative");
                    continue;
                }
                parts.Add(Speak(node));
            }
            return string.Join(" ", parts.Where(p => p.Length > 0));
        }

        private static bool IsInfix(Node node) => node is Sym s
            && (Relations.ContainsKey(s.Name) || Operators.ContainsKey(s.Name) || s.Name is "," or ";" or "(" or ":");

        private static bool IsFunctionApplication(Node node, Node? next, Node? previous)
        {
            if (next is not Fenced { Open: "(" } fenced) return false;
            string? name = node switch
            {
                Sym s => s.Name,
                Script { Base: Sym s, Sup: null } => s.Name,   // f_t(x), not f^2(x)
                Accent { Body: Sym s } => s.Name,
                _ => null,
            };
            if (name is null) return false;
            // A known function letter, or any letter applied to a list ("G(s, c)").
            return FunctionLetters.Contains(name) || fenced.Body.Items.Any(n => n is Sym { Name: "," or ";" })
                   || (node is Script && fenced.Body.Items.Count <= 3 && previous is not Num);
        }

        private string FunctionWord(string name)
            => Functions.TryGetValue(name, out var word) ? word : name.EndsWith(" of", StringComparison.Ordinal) ? name : name + " of";

        /// <summary>A function's argument: brackets around it are not spoken.</summary>
        private string Argument(Node node) => node switch
        {
            Fenced { Open: "(" or "[" or "{" } f => Row(f.Body.Items),
            _ => Speak(node),
        };

        public string Speak(Node node) => node switch
        {
            Row r => Row(r.Items),
            Num n => NumberToWords(n.Text),
            Sym s => SymbolWord(s),
            Words w => w.Text,
            Function f => FunctionWord(f.Name),
            BigOp b => BigOperators[b.Name] + " of",
            Arrow a => a.Label switch
            {
                "d" or "D" or "\\mathcal{D}" or "L" => "converges in distribution to",
                "p" or "P" => "converges in probability to",
                "a.s." or "as" => "converges almost surely to",
                "" => "tends to",
                _ => "tends, as " + a.Label + ", to",
            },
            Frac f => Fraction(f),
            Root r => RootWords(r),
            Accent a => a.Word.Length == 0 ? Speak(a.Body) : a.Prefix ? a.Word + " " + Speak(a.Body) : Speak(a.Body) + " " + a.Word,
            Fenced f => FencedWords(f),
            Script s => ScriptWords(s),
            Table t => TableWords(t),
            _ => "",
        };

        private static string SymbolWord(Sym s)
        {
            if (!s.IsCommand)
            {
                if (Relations.TryGetValue(s.Name, out var r)) return r;
                if (Operators.TryGetValue(s.Name, out var o)) return o;
                return s.Name switch { "," => ",", ";" => ";", ":" => "such that", "'" => "prime", "." => "", _ => s.Name };
            }
            return Symbols.GetValueOrDefault(s.Name) ?? Relations.GetValueOrDefault(s.Name) ?? Operators.GetValueOrDefault(s.Name)
                   // An unknown command is spoken as its name, not dropped: dropping it would silently
                   // change the expression; saying it is audibly wrong in a way the listener can report.
                   ?? s.Name;
        }

        private static bool IsSimple(Node node) => node switch
        {
            Sym or Num => true,
            Accent a => IsSimple(a.Body),
            Script { Sub: { } sub, Sup: null } s => IsSimple(s.Base) && sub is Sym or Num,
            Row { Items.Count: 1 } r => IsSimple(r.Items[0]),
            _ => false,
        };

        private string Fraction(Frac f)
        {
            // Derivatives: d y over d x, partial f over partial x.
            if (Derivative(f) is { } derivative) return derivative;
            if (f.Numerator is Num { Text: var a } && f.Denominator is Num { Text: var b }
                && int.TryParse(a, CultureInfo.InvariantCulture, out int n) && int.TryParse(b, CultureInfo.InvariantCulture, out int d)
                && n is > 0 and < 20 && d is > 1 and <= 10)
            {
                string unit = d switch { 2 => "half", 4 => "quarter", _ => Ordinals[d] };
                if (n > 1) unit = unit == "half" ? "halves" : unit + "s";
                return NumberToWords(a) + " " + unit;
            }
            if (IsSimple(f.Numerator) && IsSimple(f.Denominator))
                return Speak(f.Numerator) + " over " + Speak(f.Denominator);
            return "the fraction with numerator " + Speak(f.Numerator) + ", and denominator " + Speak(f.Denominator) + ",";
        }

        private string? Derivative(Frac f)
        {
            static List<Node> Items(Node n) => n is Row r ? r.Items : [n];
            static (string Kind, string? Order)? Leading(Node n) => n switch
            {
                Sym { Name: "partial", IsCommand: true } => ("partial", null),
                Sym { Name: "d", IsCommand: false } => ("", null),
                Words { Text: "d" } => ("", null),
                Script { Base: Sym { Name: "partial", IsCommand: true }, Sup: Num o } => ("partial", o.Text),
                Script { Base: Sym { Name: "d", IsCommand: false }, Sup: Num o } => ("", o.Text),
                _ => null,
            };
            var top = Items(f.Numerator);
            var bottom = Items(f.Denominator);
            if (top.Count == 0 || bottom.Count < 2 || Leading(top[0]) is not { } lead || Leading(bottom[0]) is not { } under) return null;
            if (lead.Kind != under.Kind) return null;
            string order = lead.Order is { } o && int.TryParse(o, CultureInfo.InvariantCulture, out int k) && k < Ordinals.Length
                ? Ordinals[k] + " " : "";
            string noun = "the " + order + (lead.Kind == "partial" ? "partial derivative" : "derivative");
            // Bottom may be "∂x ∂y" for a mixed partial, or ∂x^2.
            string variables = Row(bottom.Skip(1).Select(n => n is Script { Sup: Num } s && s.Sub is null ? s.Base : n)
                .Where(n => Leading(n) is null).ToList());
            string of = top.Count > 1 ? " of " + Row(top.Skip(1).ToList()) : "";
            return top.Count > 1
                ? noun + of + " with respect to " + variables + ","
                : noun + " with respect to " + variables + " of";
        }

        private string RootWords(Root r)
        {
            string body = Speak(r.Body);
            string tail = IsSimple(r.Body) ? "" : ", end root,";
            string index = r.Index switch
            {
                null => "square",
                Num { Text: "2" } => "square",
                Num { Text: "3" } => "cube",
                Num { Text: var t } when int.TryParse(t, CultureInfo.InvariantCulture, out int k) && k < Ordinals.Length => Ordinals[k],
                var other => Speak(other) + "-th",
            };
            return $"the {index} root of {body}{tail}";
        }

        private string FencedWords(Fenced f)
        {
            string body = Row(f.Body.Items);
            switch (f.Open)
            {
                case "|": return "the absolute value of " + body + ",";
                case "‖": return "the norm of " + body + ",";
                case "⟨": return "the inner product of " + body + ",";
                case "lfloor": return "the floor of " + body + ",";
                case "lceil": return "the ceiling of " + body + ",";
                case "." when f.Close == "|": return body;   // \left. … \right|: the bar is the evaluation
            }
            if (f.Open is "[" && f.Close is ")" or "]" || f.Open is "(" && f.Close is "]")
            {
                // An interval: [0, 1).
                var parts = SplitAtComma(f.Body.Items);
                if (parts.Count == 2)
                {
                    string kind = (f.Open, f.Close) switch { ("[", "]") => "the closed interval", ("(", ")") => "the open interval", _ => "the interval" };
                    return $"{kind} from {Row(parts[0])} to {Row(parts[1])},";
                }
            }
            if (f.Open is "{" && f.Body.Items.Count > 0) return body;
            // Brackets are not spoken; a bracketed sum or difference is a quantity, so "a(b + c)"
            // is not heard as "a b plus c".
            return f.Body.Items.Any(n => n is Sym { Name: "+" or "-" or "pm" or "mp" or "times" or "cdot" })
                ? "the quantity " + body + ", end quantity,"
                : body;
        }

        private static List<List<Node>> SplitAtComma(List<Node> items)
        {
            var parts = new List<List<Node>> { new() };
            foreach (var n in items)
            {
                if (n is Sym { Name: ",", IsCommand: false }) parts.Add([]);
                else parts[^1].Add(n);
            }
            return parts;
        }

        private string ScriptWords(Script s)
        {
            // A big operator's scripts are its range: "the sum from i equals one to n of".
            if (s.Base is BigOp b)
            {
                string noun = BigOperators[b.Name];
                if (b.Name is "int" or "iint" or "iiint" or "oint") _integrals++;
                if (b.Name is "lim" or "limsup" or "liminf" && s.Sub is not null)
                    return noun + " as " + Speak(s.Sub) + " of";
                if (s.Sub is not null && s.Sup is not null)
                    return noun + " from " + Speak(s.Sub) + " to " + Speak(s.Sup) + " of";
                if (s.Sub is not null)
                    return noun + " over " + Speak(s.Sub) + " of";
                return noun + " of";
            }
            // \left. … \right|_{a}: "evaluated at a".
            if (s.Base is Fenced { Open: ".", Close: "|" } evaluated && s.Sub is not null)
                return Speak(evaluated) + ", evaluated at " + Speak(s.Sub) + ",";
            if (s.Base is Sym { Name: "|", IsCommand: false } && s.Sub is not null)
                return "evaluated at " + Speak(s.Sub) + ",";

            bool quantity = s.Base is Fenced { Open: "(" or "[" } f && !IsSimple(f.Body) && s.Sup is not null;
            string head = quantity ? "the quantity " + Row(((Fenced)s.Base).Body.Items) + ", end quantity," : Speak(s.Base);
            // A pause after a quantity's power, so what follows is not heard as part of the exponent.
            return head + " " + ScriptTail(s) + (quantity ? "," : "");
        }

        private string ScriptTail(Script s)
        {
            var words = new StringBuilder();
            if (s.Sub is not null)
            {
                words.Append("sub ").Append(SubscriptWords(s.Sub));
                if (s.Sup is not null && !IsSimple(s.Sub)) words.Append(',');
            }
            if (s.Sup is not null)
            {
                if (words.Length > 0) words.Append(' ');
                words.Append(Exponent(s.Sup));
            }
            return words.ToString();
        }

        /// <summary>"s sub one one" for s₁₁: in a subscript, digits are indices.</summary>
        private string SubscriptWords(Node sub) => sub switch
        {
            Num { Text: var t } when t.Length is 2 or 3 && t.All(char.IsAsciiDigit) => string.Join(" ", t.Select(c => Ones[c - '0'])),
            _ => Speak(sub),
        };

        private string Exponent(Node sup)
        {
            switch (sup)
            {
                case Num { Text: "2" }: return "squared";
                case Num { Text: "3" }: return "cubed";
                case Num { Text: var t } when int.TryParse(t, CultureInfo.InvariantCulture, out int k) && k < Ordinals.Length:
                    return "to the " + Ordinals[k] + " power";
                case Sym { Name: "ast" or "*" or "star" }:
                    return "star";
                case Sym { Name: "T" or "top" or "intercal" or "prime" or "dagger" or "'" } s:
                    return SymbolWord(s);
                case Words w: return w.Text;
                case Sym { IsCommand: false } s when s.Name.Length == 1 && char.IsAsciiLetterLower(s.Name[0]):
                    return "to the " + s.Name + "-th power";
                case Row { Items: [Sym { Name: "-", IsCommand: false }, Num { Text: var t }] }:
                    return "to the negative " + (int.TryParse(t, CultureInfo.InvariantCulture, out int m) && m < Ordinals.Length && m > 1 ? Ordinals[m] : NumberToWords(t)) + " power";
                case Row { Items: [Sym { Name: "ast" or "*" or "star" }, Node rest] }:
                    return "star " + Exponent(rest);
                case Row { Items: [Num a, Sym { Name: "/", IsCommand: false }, Num b] }:
                    return Exponent(new Frac(a, b));
                case Frac f when Fraction(f) is var words && !words.StartsWith("the fraction", StringComparison.Ordinal) && IsSimple(f.Numerator) && IsSimple(f.Denominator):
                    return "to the " + words + " power";
                case Sym s:
                    return "to the power " + SymbolWord(s);
            }
            return "raised to the exponent, " + Speak(sup) + ", end exponent,";
        }

        private string TableWords(Table t)
        {
            if (t.Environment.StartsWith("cases", StringComparison.Ordinal))
                return "cases: " + string.Join("; ", t.Rows.Select(r =>
                    Row(r[0].Items) + (r.Count > 1 ? ", " + CaseCondition(r[1]) : "")));
            if (t.Environment.Contains("matrix", StringComparison.Ordinal))
            {
                int columns = t.Rows.Max(r => r.Count);
                return $"the {NumberToWords(t.Rows.Count.ToString(CultureInfo.InvariantCulture))} by {NumberToWords(columns.ToString(CultureInfo.InvariantCulture))} matrix with rows: "
                       + string.Join("; ", t.Rows.Select(r => string.Join(", ", r.Select(c => Row(c.Items))))) + ";";
            }
            return string.Join("; ", t.Rows.Select(r => string.Join(" ", r.Select(c => Row(c.Items)))));
        }

        private string CaseCondition(Row condition)
        {
            string words = Row(condition.Items);
            return words.StartsWith("if ", StringComparison.Ordinal) || words.StartsWith("otherwise", StringComparison.Ordinal) ? words : "if " + words;
        }
    }

    // ── Numbers ───────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Renders a numeric literal in words, including the simple fractions and decimals that appear
    /// constantly in prose mathematics.
    /// </summary>
    public static string NumberToWords(string literal)
    {
        if (literal.Contains('.'))
        {
            var parts = literal.Split('.', 2);
            string whole = parts[0].Length == 0 ? "zero" : IntegerToWords(parts[0]);
            string fractionDigits = string.Join(" ", parts[1].Where(char.IsAsciiDigit).Select(d => Ones[d - '0']));
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
    {
        s = WhitespaceRun().Replace(s.Trim(), " ");
        s = SpaceBeforePunctuation().Replace(s, "$1");
        s = RepeatedPunctuation().Replace(s, "$1");
        return s.Trim().TrimEnd(',', ';').TrimStart(',', ';', ' ');
    }

    [GeneratedRegex(@"\s+")]
    private static partial Regex WhitespaceRun();

    [GeneratedRegex(@"\\(?:begin|end)\{(?:equation|align|gather|multline|eqnarray|displaymath|math)\*?\}|\\(?:label|tag)\{[^}]*\}|\\(?:nonumber|notag)")]
    private static partial Regex StrayMarkup();

    [GeneratedRegex(@"\s+([,;.:?!])")]
    private static partial Regex SpaceBeforePunctuation();

    [GeneratedRegex(@"([,;])(?:\s*[,;])+")]
    private static partial Regex RepeatedPunctuation();
}
