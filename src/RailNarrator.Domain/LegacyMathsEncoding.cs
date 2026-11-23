namespace RailNarrator.Domain;

/// <summary>
/// Puts back the signs that some publishers' older maths fonts hide in a text layer: "(" and ")"
/// arrive as "ð" and "Þ", "=" as "¼" and "+" as "þ" (Elsevier's and Oxford's journals, among others).
/// </summary>
/// <remarks>
/// As extracted, "ri;j;t ¼ ɛ tanhðfi;j;tÞ" is noise to a listener, hides the "=" that marks a
/// sentence as having maths, and gives a language model a garbled copy of the passage. The four
/// characters are real letters and a real fraction elsewhere (Icelandic, "¼ cup"), so nothing is
/// changed unless the page shows the pattern: "ð" and "Þ" standing as matched pairs, as brackets
/// do and as the two letters in running Icelandic do not. Each character is replaced by one
/// character, so every offset and character box of the page stays valid.
/// </remarks>
public static class LegacyMathsEncoding
{
    /// <summary>Matched "ð…Þ" pairs a page must have before its text is taken to use the encoding.</summary>
    public const int PairsNeeded = 3;

    /// <summary>True when "ð" and "Þ" stand in <paramref name="text"/> as brackets do.</summary>
    public static bool IsUsedIn(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        int depth = 0, pairs = 0, unmatched = 0;
        foreach (char c in text)
        {
            if (c == 'ð') depth++;
            else if (c == 'Þ')
            {
                if (depth > 0) { depth--; pairs++; }
                else unmatched++;
            }
        }
        unmatched += depth;
        // A bracket cut off by a column or page break leaves a few unmatched; letters leave most.
        return pairs >= PairsNeeded && unmatched * 4 <= pairs;
    }

    /// <summary>
    /// <paramref name="text"/> with the four signs restored when it uses the encoding, and
    /// unchanged (the same instance) when it does not. The length never changes.
    /// </summary>
    public static string Restore(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        return IsUsedIn(text) ? RestoreAll(text) : text;
    }

    /// <summary>
    /// The four signs restored without asking whether the text uses the encoding: for the pages of
    /// a document another of whose pages showed that it does. A page with "ν ¼ 7" and no bracket
    /// has no evidence of its own.
    /// </summary>
    public static string RestoreAll(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        if (text.AsSpan().IndexOfAny("ðÞ¼þ") < 0) return text;
        return string.Create(text.Length, text, static (span, source) =>
        {
            for (int i = 0; i < source.Length; i++)
                span[i] = source[i] switch { 'ð' => '(', 'Þ' => ')', '¼' => '=', 'þ' => '+', var c => c };
        });
    }
}
