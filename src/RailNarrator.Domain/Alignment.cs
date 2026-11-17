using System.Text;
using System.Text.RegularExpressions;

namespace RailNarrator.Domain;

/// <summary>A lexical token with an identity given by its position in the exact spoken script.</summary>
public sealed record SpokenWord(int Index, TextRange Range, string Text)
{
    public static IReadOnlyList<SpokenWord> Tokenize(string script)
        => Regex.Matches(script, @"[\p{L}\p{M}\p{N}]+(?:['’][\p{L}\p{M}\p{N}]+)*")
            .Select((m, i) => new SpokenWord(i, new TextRange(m.Index, m.Length), m.Value)).ToArray();

    public static string MatchKey(string text)
        => text.Normalize(NormalizationForm.FormKC).Replace('’', '\'').ToLowerInvariant();
}

/// <summary>Acoustic evidence in source PCM frames. PDF geometry is added by the coordinator.</summary>
public sealed record AlignedWord(TextRange SpokenRange, long StartSample, long EndSample, double? Confidence = null);

public sealed record WordAlignment(IReadOnlyList<AlignedWord> Words, IReadOnlyList<string> Warnings);
