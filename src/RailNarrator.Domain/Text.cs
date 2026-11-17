namespace RailNarrator.Domain;

/// <summary>
/// The unit a text offset is counted in. Offsets are <b>not</b> interchangeable between units,
/// and treating them as such is a silent corruption: a mathematical expression or an emoji can
/// move every later offset by one or more positions.
/// </summary>
/// <remarks>
/// .NET strings are UTF-16, so <see cref="Utf16"/> is what <see cref="string.Substring(int,int)"/>
/// and RailReaderCore's <c>CharBox.Index</c> / <c>WordSpan.Start</c> use, and it is the unit every
/// Rail Narrator contract stores. External APIs differ: most LLM and speech endpoints count
/// Unicode code points (<see cref="CodePoint"/>), and wire formats count UTF-8 bytes
/// (<see cref="Utf8Byte"/>). <see cref="TextOffsets"/> converts between them; nothing else may
/// reinterpret an offset.
/// </remarks>
public enum TextOffsetUnit
{
    /// <summary>UTF-16 code units — the .NET <see cref="string"/> index space. A surrogate pair counts as two.</summary>
    Utf16,

    /// <summary>Unicode code points (scalar values). A surrogate pair counts as one.</summary>
    CodePoint,

    /// <summary>UTF-8 bytes, as a wire format would count them.</summary>
    Utf8Byte,
}

/// <summary>
/// A half-open range <c>[Start, Start + Length)</c> of characters in a specific string.
/// Offsets are always <see cref="TextOffsetUnit.Utf16"/> unless a contract says otherwise.
/// </summary>
/// <remarks>
/// Half-open so that adjacent ranges tile a string exactly and an empty range is expressible
/// (<c>Length == 0</c>) without a sentinel. A range that ends one past the last character is
/// valid; one that starts there is only valid when empty.
/// </remarks>
public readonly record struct TextRange(int Start, int Length)
{
    /// <summary>Exclusive end offset — the first offset <i>not</i> in the range.</summary>
    public int End => Start + Length;

    public bool IsEmpty => Length == 0;

    public static TextRange Empty => new(0, 0);

    /// <summary>The whole of <paramref name="s"/>.</summary>
    public static TextRange OfString(string s) => new(0, s.Length);

    /// <summary>Half-open constructor, for symmetry with <see cref="End"/>.</summary>
    public static TextRange FromBounds(int start, int end) => new(start, end - start);

    public bool Contains(int offset) => offset >= Start && offset < End;

    /// <summary>True when this range lies entirely within <paramref name="other"/>.</summary>
    public bool Within(TextRange other) => Start >= other.Start && End <= other.End;

    public bool Overlaps(TextRange other) => Start < other.End && other.Start < End;

    /// <summary>The intersection, or <see cref="Empty"/> when the ranges are disjoint.</summary>
    public TextRange Intersect(TextRange other)
    {
        int s = Math.Max(Start, other.Start);
        int e = Math.Min(End, other.End);
        return e > s ? new TextRange(s, e - s) : Empty;
    }

    /// <summary>Extracts this range's characters. Throws when the range exceeds the string.</summary>
    public string Substring(string s)
    {
        ValidateAgainst(s);
        return s.Substring(Start, Length);
    }

    /// <summary>
    /// Throws unless the range fits inside <paramref name="s"/>. Ranges are persisted and cross
    /// process boundaries, so a stale or corrupted one must fail loudly here rather than produce
    /// a truncated highlight later.
    /// </summary>
    public void ValidateAgainst(string s)
    {
        ArgumentNullException.ThrowIfNull(s);
        if (Start < 0) throw new ArgumentOutOfRangeException(nameof(s), $"Range start {Start} is negative.");
        if (Length < 0) throw new ArgumentOutOfRangeException(nameof(s), $"Range length {Length} is negative.");
        if (End > s.Length)
            throw new ArgumentOutOfRangeException(nameof(s), $"Range [{Start},{End}) exceeds string length {s.Length}.");
    }

    public override string ToString() => $"[{Start},{End})";
}

/// <summary>
/// Conversion between offset units, and the surrogate-pair discipline that makes it safe.
/// </summary>
/// <remarks>
/// Every method here refuses to split a surrogate pair. That is the whole point of the type:
/// an LLM that reports a code-point offset, or a wire format that reports a byte offset, cannot
/// be handed to <see cref="string.Substring(int,int)"/> directly, and the resulting off-by-one is
/// invisible until a highlight lands on the wrong character.
/// </remarks>
public static class TextOffsets
{
    /// <summary>
    /// True when <paramref name="offset"/> falls between a high and a low surrogate, i.e. splitting
    /// the string there would produce two invalid halves.
    /// </summary>
    public static bool SplitsSurrogatePair(string s, int offset)
        => offset > 0 && offset < s.Length && char.IsHighSurrogate(s[offset - 1]) && char.IsLowSurrogate(s[offset]);

    /// <summary>
    /// Moves <paramref name="offset"/> forward to the next boundary that does not split a
    /// surrogate pair. Returns the offset unchanged when it is already safe, and clamps to the
    /// string length.
    /// </summary>
    public static int AlignForward(string s, int offset)
    {
        ArgumentNullException.ThrowIfNull(s);
        if (offset <= 0) return 0;
        if (offset >= s.Length) return s.Length;
        return SplitsSurrogatePair(s, offset) ? offset + 1 : offset;
    }

    /// <summary>UTF-16 offset → code-point offset.</summary>
    public static int ToCodePointOffset(string s, int utf16Offset)
    {
        ArgumentNullException.ThrowIfNull(s);
        if (utf16Offset < 0) throw new ArgumentOutOfRangeException(nameof(utf16Offset));
        if (utf16Offset > s.Length) throw new ArgumentOutOfRangeException(nameof(utf16Offset));
        int codePoints = 0;
        for (int i = 0; i < utf16Offset;)
        {
            i += char.IsHighSurrogate(s[i]) && i + 1 < s.Length && char.IsLowSurrogate(s[i + 1]) ? 2 : 1;
            if (i > utf16Offset) throw new ArgumentException($"UTF-16 offset {utf16Offset} splits a surrogate pair.", nameof(utf16Offset));
            codePoints++;
        }
        return codePoints;
    }

    /// <summary>Code-point offset → UTF-16 offset.</summary>
    public static int FromCodePointOffset(string s, int codePointOffset)
    {
        ArgumentNullException.ThrowIfNull(s);
        if (codePointOffset < 0) throw new ArgumentOutOfRangeException(nameof(codePointOffset));
        int codePoints = 0, i = 0;
        while (codePoints < codePointOffset)
        {
            if (i >= s.Length) throw new ArgumentOutOfRangeException(nameof(codePointOffset), $"String holds only {codePoints} code points.");
            i += char.IsHighSurrogate(s[i]) && i + 1 < s.Length && char.IsLowSurrogate(s[i + 1]) ? 2 : 1;
            codePoints++;
        }
        return i;
    }

    /// <summary>UTF-16 offset → UTF-8 byte offset.</summary>
    public static int ToUtf8ByteOffset(string s, int utf16Offset)
    {
        ArgumentNullException.ThrowIfNull(s);
        if (utf16Offset < 0 || utf16Offset > s.Length) throw new ArgumentOutOfRangeException(nameof(utf16Offset));
        return System.Text.Encoding.UTF8.GetByteCount(s.AsSpan(0, utf16Offset));
    }

    /// <summary>UTF-8 byte offset → UTF-16 offset.</summary>
    public static int FromUtf8ByteOffset(string s, int byteOffset)
    {
        ArgumentNullException.ThrowIfNull(s);
        if (byteOffset < 0) throw new ArgumentOutOfRangeException(nameof(byteOffset));
        int bytes = 0, i = 0;
        while (bytes < byteOffset)
        {
            if (i >= s.Length) throw new ArgumentOutOfRangeException(nameof(byteOffset), $"String encodes to only {bytes} UTF-8 bytes.");
            int size = System.Text.Encoding.UTF8.GetByteCount(s.AsSpan(i, char.IsHighSurrogate(s[i]) && i + 1 < s.Length && char.IsLowSurrogate(s[i + 1]) ? 2 : 1));
            bytes += size;
            i += char.IsHighSurrogate(s[i]) && i + 1 < s.Length && char.IsLowSurrogate(s[i + 1]) ? 2 : 1;
        }
        if (bytes != byteOffset) throw new ArgumentException($"UTF-8 byte offset {byteOffset} splits an encoded character.", nameof(byteOffset));
        return i;
    }

    /// <summary>
    /// Re-expresses a range whose offsets are in <paramref name="unit"/> as UTF-16 offsets, the
    /// unit every Rail Narrator contract stores. Use at the boundary where an external response
    /// enters the domain, never internally.
    /// </summary>
    public static TextRange ToUtf16Range(string s, int start, int length, TextOffsetUnit unit)
    {
        int u16Start = unit switch
        {
            TextOffsetUnit.Utf16 => start,
            TextOffsetUnit.CodePoint => FromCodePointOffset(s, start),
            TextOffsetUnit.Utf8Byte => FromUtf8ByteOffset(s, start),
            _ => throw new ArgumentOutOfRangeException(nameof(unit)),
        };
        int u16End = unit switch
        {
            TextOffsetUnit.Utf16 => start + length,
            TextOffsetUnit.CodePoint => FromCodePointOffset(s, start + length),
            TextOffsetUnit.Utf8Byte => FromUtf8ByteOffset(s, start + length),
            _ => throw new ArgumentOutOfRangeException(nameof(unit)),
        };
        return TextRange.FromBounds(u16Start, u16End);
    }

    /// <summary>Number of Unicode code points in <paramref name="s"/>.</summary>
    public static int CodePointCount(string s)
    {
        ArgumentNullException.ThrowIfNull(s);
        int n = 0;
        for (int i = 0; i < s.Length; i++)
        {
            if (char.IsHighSurrogate(s[i]) && i + 1 < s.Length && char.IsLowSurrogate(s[i + 1])) i++;
            n++;
        }
        return n;
    }
}
