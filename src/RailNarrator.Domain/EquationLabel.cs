using System.Text.RegularExpressions;

namespace RailNarrator.Domain;

/// <summary>
/// Finds the number printed beside a display equation, "(2.1)", among the equation's own characters.
/// </summary>
/// <remarks>
/// A layout model with a class for equation numbers (PP-DocLayoutV3) keeps the number out of the
/// equation's block. One without (Docling Heron) includes it, and then it is part of the equation
/// for everything downstream: a formula model transcribes it ("\eqno(1.1)", read as "eqno one
/// one"), it narrows the formula in the image the model is given, and the comparison with the text
/// layer finds a number the transcription may lack. The text alone cannot say which "(2)" is a
/// label, since the text layer's order puts a label in the middle of a multi-line equation, so
/// geometry decides: a label stands apart at the equation's edge.
/// </remarks>
public static partial class EquationLabel
{
    /// <summary>One character of the equation with its box; an empty box for a space.</summary>
    public readonly record struct Glyph(char Character, PageRect Rect, int PageIndex);

    // "(1)", "(2.1)", "(A.3)", "(12a)", "(3.2.1)". Older Elsevier fonts put their parentheses in
    // the text layer as "ð" and "Þ", so "ð13Þ" is a label too.
    [GeneratedRegex(@"[\(ð]\s*(?:[A-Z]\s*\.?\s*)?\d{1,3}(?:\s*\.\s*\d{1,3}){0,2}\s*[a-z]?\s*[\)Þ]")]
    private static partial Regex Label();

    /// <summary>
    /// The indices of the characters that make up equation labels: every parenthesised number that
    /// reaches the equation's right or left edge with clear space between it and the ink beside it.
    /// Empty when there is none, or when the label is all there is.
    /// </summary>
    public static IReadOnlySet<int> Find(IReadOnlyList<Glyph> glyphs)
    {
        ArgumentNullException.ThrowIfNull(glyphs);
        var found = new HashSet<int>();
        if (glyphs.Count < 4) return found;
        var text = new char[glyphs.Count];
        for (int i = 0; i < glyphs.Count; i++) text[i] = glyphs[i].Character;

        foreach (Match match in Label().Matches(new string(text)))
        {
            int start = match.Index, end = match.Index + match.Length;
            int page = glyphs[start].PageIndex;
            PageRect? union = null;
            bool onePage = true;
            for (int i = start; i < end; i++)
            {
                if (glyphs[i].PageIndex != page) { onePage = false; break; }
                var r = glyphs[i].Rect;
                if (r.IsEmpty) continue;
                union = union is { } u ? PageRect.UnionAll([u, r]) : r;
            }
            if (!onePage || union is not { } label) continue;

            // A label is set a quad or more from its equation; an argument, "g(2)", touches its function.
            float gap = Math.Max(4f, 0.8f * label.Height);
            bool others = false, rightmost = true, leftmost = true, clearOnLeft = true, clearOnRight = true;
            for (int i = 0; i < glyphs.Count; i++)
            {
                if (i >= start && i < end) continue;
                var r = glyphs[i].Rect;
                if (r.IsEmpty || glyphs[i].PageIndex != page) continue;
                others = true;
                if (r.Right > label.Right + 2) rightmost = false;
                if (r.Left < label.Left - 2) leftmost = false;
                bool sameBand = Math.Min(r.Bottom, label.Bottom) - Math.Max(r.Top, label.Top) > 0.2f * Math.Min(r.Height, label.Height);
                if (!sameBand) continue;
                if (r.Right > label.Left - gap) clearOnLeft = false;
                if (r.Left < label.Right + gap) clearOnRight = false;
            }
            if (!others) continue;
            if ((rightmost && clearOnLeft) || (leftmost && clearOnRight))
                for (int i = start; i < end; i++) found.Add(i);
        }
        // An equation that is nothing but labels is not an equation with a label.
        bool anythingElse = false;
        for (int i = 0; i < glyphs.Count && !anythingElse; i++)
            anythingElse = !found.Contains(i) && !char.IsWhiteSpace(glyphs[i].Character) && !glyphs[i].Rect.IsEmpty;
        if (!anythingElse) found.Clear();
        return found;
    }
}
