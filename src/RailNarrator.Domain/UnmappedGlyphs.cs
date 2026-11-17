namespace RailNarrator.Domain;

/// <summary>
/// Identifies glyphs that a PDF text layer returned without a Unicode value.
/// </summary>
/// <remarks>
/// <para>
/// Measured on a typeset journal article: its fonts name their ligature glyphs <c>f_i</c>,
/// <c>f_f</c>, <c>f_f_i</c>, <c>f_l</c> and <c>f_t</c>. PDFium does not decompose underscore-joined
/// glyph names, so each ligature arrived as its raw character code — U+0002, U+0000, U+0008,
/// U+0003, U+000F — about 250 times in 27 pages. Dropping those, as any control character used to be,
/// turned "sufficiently" into "su ciently" and "specification" into "speci cation", in the script
/// and therefore in the audio.
/// </para>
/// <para>
/// The codes are assigned by each font's own encoding, so there is no table to look them up in, and
/// the adapter cannot see which font a character came from. What a document does offer is
/// repetition: one code stands for one glyph throughout a font, and most of the words it occurs in
/// are ordinary ones. Every occurrence inside a word is tried against each ligature a text face
/// commonly carries; an occurrence that only one of them turns into a known word is a vote; a code
/// whose votes agree is resolved for the whole document, including in words no list contains ("misspecification",
/// "robustified").
/// </para>
/// <para>
/// A code that does not win a clear majority is left unidentified, and an unidentified glyph is
/// never guessed at: mathematical symbols leak out of symbol fonts as control characters in exactly
/// the same way, and inventing letters for them would put words in the narration that are not in
/// the document.
/// </para>
/// </remarks>
public static class UnmappedGlyphResolver
{
    /// <summary>Lower-case ligatures a Latin text face may carry as one glyph.</summary>
    private static readonly string[] Candidates = ["fi", "fl", "ff", "ffi", "ffl", "ft", "fj", "fb", "fh", "fk"];

    /// <summary>The only common ligature that starts with a capital. Considered at the start of a word.</summary>
    private const string CapitalTh = "Th";

    private static readonly Lazy<HashSet<string>> Words = new(LoadWords);

    /// <summary>True for a character value no text glyph legitimately has.</summary>
    public static bool IsUnmapped(char c) => char.IsControl(c);

    /// <summary>
    /// Works out which ligature each unmapped code stands for.
    /// </summary>
    /// <param name="tokens">Space-delimited tokens of the document that still contain unmapped codes.</param>
    /// <param name="minVotes">Occurrences that must agree before a code is resolved.</param>
    /// <param name="minShare">Share of a code's votes the winning ligature must hold.</param>
    public static IReadOnlyDictionary<char, string> Infer(IEnumerable<string> tokens, int minVotes = 2, double minShare = 0.6)
    {
        ArgumentNullException.ThrowIfNull(tokens);
        var votes = new Dictionary<char, Dictionary<string, int>>();
        var valid = new List<string>();

        foreach (string token in tokens)
        {
            if (string.IsNullOrEmpty(token)) continue;
            for (int at = 0; at < token.Length; at++)
            {
                if (!IsUnmapped(token[at])) continue;

                // The letters on either side, up to anything that is not a letter. A second unmapped
                // glyph in the same word ends the run, so such a word simply fails to match.
                int start = at;
                while (start > 0 && char.IsLetter(token[start - 1])) start--;
                int end = at + 1;
                while (end < token.Length && char.IsLetter(token[end])) end++;
                string prefix = token[start..at], suffix = token[(at + 1)..end];
                if (prefix.Length + suffix.Length < 2) continue;

                valid.Clear();
                foreach (string candidate in Candidates)
                    if (Words.Value.Contains((prefix + candidate + suffix).ToLowerInvariant())) valid.Add(candidate);
                if (prefix.Length == 0 && Words.Value.Contains(("th" + suffix).ToLowerInvariant())) valid.Add(CapitalTh);
                if (valid.Count == 0) continue;

                // Only an occurrence that fits exactly one ligature is evidence. A large word list
                // holds enough rare words ("affer", "offen") that "a?er" and "o?en" fit both ff and
                // ft; counting those as half a vote each left the ft glyph of a real paper two
                // points short of a majority, while "shi?s" and "therea?er" settle it outright.
                if (valid.Count != 1) continue;
                if (!votes.TryGetValue(token[at], out var tally)) votes[token[at]] = tally = new(StringComparer.Ordinal);
                tally[valid[0]] = tally.GetValueOrDefault(valid[0]) + 1;
            }
        }

        var resolved = new Dictionary<char, string>();
        foreach (var (code, tally) in votes)
        {
            var best = tally.MaxBy(kv => kv.Value);
            int total = tally.Values.Sum();
            if (best.Value >= minVotes && best.Value >= minShare * total) resolved[code] = best.Key;
        }
        return resolved;
    }

    private static HashSet<string> LoadWords()
    {
        var words = new HashSet<string>(StringComparer.Ordinal);
        using var stream = typeof(UnmappedGlyphResolver).Assembly
            .GetManifestResourceStream("RailNarrator.Domain.Resources.ligature-words.txt");
        if (stream is null) return words;   // no list: nothing is ever resolved, which is the safe failure
        using var reader = new StreamReader(stream);
        while (reader.ReadLine() is { } line)
            if (line.Length > 0 && line[0] != '#') words.Add(line);
        return words;
    }
}
