using System.Text;
using System.Text.RegularExpressions;

namespace RailNarrator.Domain;

/// <summary>A lexical token with an identity given by its position in the exact spoken script.</summary>
public sealed record SpokenWord(int Index, TextRange Range, string Text)
{
    /// <summary>
    /// The words of a script, in order. A token must hold a letter (not a modifier letter such as
    /// U+02C6) or a digit: accent marks and variation selectors left behind by maths fonts are
    /// silent, so the speech engine does not say them and an aligner cannot find them.
    /// </summary>
    public static IReadOnlyList<SpokenWord> Tokenize(string script)
        => Regex.Matches(script, @"[\p{L}\p{M}\p{N}]+(?:['’][\p{L}\p{M}\p{N}]+)*")
            .Where(m => Regex.IsMatch(m.Value, @"[\p{Lu}\p{Ll}\p{Lt}\p{Lo}\p{N}]"))
            .Select((m, i) => new SpokenWord(i, new TextRange(m.Index, m.Length), m.Value)).ToArray();

    public static string MatchKey(string text)
        => text.Normalize(NormalizationForm.FormKC).Replace('’', '\'').ToLowerInvariant();
}

/// <summary>Acoustic evidence in source PCM frames. PDF geometry is added by the coordinator.</summary>
public sealed record AlignedWord(TextRange SpokenRange, long StartSample, long EndSample, double? Confidence = null);

public sealed record WordAlignment(IReadOnlyList<AlignedWord> Words, IReadOnlyList<string> Warnings);
