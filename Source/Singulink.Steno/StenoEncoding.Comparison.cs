using System.Runtime.CompilerServices;

namespace Singulink.Steno;

/// <content>
/// Contains the comparison of text in code point order, the order of Steno bytes.
/// </content>
public sealed partial class StenoEncoding
{
    /// <summary>
    /// Gets a string comparer that orders text by code point, the order in which the Steno bytes of the text sort.
    /// </summary>
    /// <remarks>
    /// The order is the one of <see cref="CompareCodePoints(ReadOnlySpan{char}, ReadOnlySpan{char})"/>; see there for how it differs from ordinal
    /// order. A <see langword="null"/> string comes before every other string and is equal to <see langword="null"/>, as with <see
    /// cref="StringComparer.Ordinal"/>. Equality is ordinal equality, since two strings have the same code points exactly when they have the same code
    /// units.
    /// </remarks>
    public static StringComparer CodePointComparer { get; } = new CodePointStringComparer();

    /// <summary>
    /// Compares two texts in code point order, the order in which their Steno bytes sort.
    /// </summary>
    /// <param name="x">The first text.</param>
    /// <param name="y">The second text.</param>
    /// <returns>A negative number if <paramref name="x"/> comes before <paramref name="y"/>, zero if they are equal, and a positive number if
    /// <paramref name="x"/> comes after <paramref name="y"/>.</returns>
    /// <remarks>
    /// <para>
    /// Code point order compares the texts code point by code point, a surrogate pair as the one supplementary code point (U+10000..U+10FFFF) that it
    /// stands for, and an unpaired surrogate as a code point of its own (U+D800..U+DFFF), as the text model of the Steno Encoding has it. It is the
    /// order of the Steno bytes of the texts compared byte by byte, and, for text without unpaired surrogates, also the order of their UTF-8 bytes and
    /// of their UTF-32 code units.
    /// </para>
    /// <para>
    /// It differs from ordinal comparison (<see cref="string.CompareOrdinal(string, string)"/>, <see cref="StringComparison.Ordinal"/>), which
    /// compares UTF-16 code units. In text without unpaired surrogates they differ in exactly one case: where the texts first differ, one has a
    /// supplementary character (a surrogate pair) and the other has a character in U+E000..U+FFFF. Ordinal comparison puts the supplementary character
    /// first, because its first code unit is in U+D800..U+DBFF; code point order puts it last. For example <c>"すごい！"</c> (with U+FF01) comes after
    /// <c>"すごい😀"</c> (with U+1F600) in ordinal order but before it in code point order. Everything else is ordered the same way by both.
    /// </para>
    /// <para>
    /// An unpaired surrogate adds cases of the same kind. Code point order puts an unpaired surrogate (U+D800..U+DFFF) before every supplementary
    /// character, while ordinal comparison compares its code unit with the high surrogate of the character: it puts the supplementary character first
    /// when the unpaired surrogate's code unit is greater, and when the two are the same code unit and the unpaired one is followed by a code unit in
    /// U+E000..U+FFFF.
    /// </para>
    /// <para>
    /// Use it where strings must be in the order of their Steno bytes, such as keys that are also kept as Steno bytes in a byte-ordered index.
    /// Like ordinal comparison, it ignores language and culture. It does not allocate.
    /// </para>
    /// </remarks>
    public static int CompareCodePoints(ReadOnlySpan<char> x, ReadOnlySpan<char> y)
    {
        int index = x.CommonPrefixLength(y);

        // One text is a prefix of the other in code units: the shorter comes first. That is code point order too, even if the shorter one ends in a high
        // surrogate that the longer one pairs (the high surrogate as a code point is below every supplementary code point).
        if (index == x.Length || index == y.Length)
            return x.Length - y.Length;

        // The texts differ at the code unit index. If a high surrogate that both share comes before it and either text pairs it with a low surrogate
        // here, the code points that differ start at that high surrogate; otherwise they start here. Either way they are different code points.
        if (index > 0 && char.IsHighSurrogate(x[index - 1]) && (char.IsLowSurrogate(x[index]) || char.IsLowSurrogate(y[index])))
            index--;

        return CodePointAt(x, index) - CodePointAt(y, index);
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static int CodePointAt(ReadOnlySpan<char> text, int index)
    {
        char c = text[index];

        if (char.IsHighSurrogate(c) && index + 1 < text.Length && char.IsLowSurrogate(text[index + 1]))
            return char.ConvertToUtf32(c, text[index + 1]);

        return c;
    }

    private sealed class CodePointStringComparer : StringComparer
    {
        public override int Compare(string? x, string? y)
        {
            if (ReferenceEquals(x, y))
                return 0;

            if (x is null)
                return -1;

            if (y is null)
                return 1;

            return CompareCodePoints(x, y);
        }

        public override bool Equals(string? x, string? y) => string.Equals(x, y, StringComparison.Ordinal);

        public override int GetHashCode(string obj)
        {
            ArgumentNullException.ThrowIfNull(obj);
            return obj.GetHashCode(StringComparison.Ordinal);
        }
    }
}
