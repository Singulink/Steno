using System.Text;

namespace Singulink.Steno.Benchmarks;

/// <summary>
/// Texts for the vectors of the format: random text in the shape of words over the scripts that the layouts treat specially, and the texts that reach
/// the fullest layouts near the end of the code space.
/// </summary>
internal static class LabTexts
{
    // Inclusive code point ranges for random text, next to the ones of RandomText: scripts that span several windows, the Japanese and Chinese mixes,
    // and the chars that the layouts treat specially.
    private static readonly (int Start, int End)[] _ranges = [
        (0x0061, 0x007A), // ASCII letters
        (0x0020, 0x0040), // ASCII punctuation and digits
        (0x0000, 0x001F), // C0 controls
        (0x007F, 0x00BF), // DEL, C1 controls, Latin-1 punctuation
        (0x00C0, 0x00FF), // Latin-1 letters
        (0x0100, 0x017F), // Latin Extended-A (Polish, Czech)
        (0x0180, 0x02FF), // Latin Extended-B, IPA, modifiers
        (0x0300, 0x036F), // Combining marks
        (0x0370, 0x03FF), // Greek
        (0x0400, 0x052F), // Cyrillic
        (0x0530, 0x05FF), // Armenian, Hebrew
        (0x0600, 0x06FF), // Arabic, Urdu
        (0x0750, 0x077F), // Arabic Supplement
        (0x08A0, 0x08FF), // Arabic Extended-A
        (0x0900, 0x097F), // Devanagari
        (0x0E00, 0x0E7F), // Thai
        (0x1100, 0x11FF), // Hangul Jamo
        (0x1200, 0x139F), // Ethiopic and its supplement: 4 windows
        (0x2D80, 0x2DDF), // Ethiopic Extended
        (0x1EA0, 0x1EF9), // Vietnamese letters
        (0x1F00, 0x1FFF), // Greek Extended
        (0x2000, 0x20FF), // General punctuation, currency
        (0x2100, 0x27BF), // Symbols, arrows, dingbats
        (0x3000, 0x303F), // CJK punctuation
        (0x3040, 0x309F), // Hiragana
        (0x30A0, 0x30FF), // Katakana
        (0x30FB, 0x30FC), // The marks that both kana use
        (0x3100, 0x33FF), // Bopomofo, compatibility
        (0x3400, 0x4DBF), // CJK Extension A
        (0x4E00, 0x9FFF), // CJK ideographs
        (0xA000, 0xA0FF), // Yi, next to the ideographs
        (0xAB80, 0xABFF), // next to the Hangul syllables
        (0xAC00, 0xD7A3), // Hangul syllables
        (0xD7A4, 0xD7FF), // Hangul Jamo Extended-B
        (0xD800, 0xDFFF), // Lone surrogates
        (0xE000, 0xF8FF), // Private use
        (0xFB50, 0xFDFF), // Arabic presentation forms A
        (0xFE70, 0xFEFF), // Arabic presentation forms B, next to the fullwidth forms
        (0xFF00, 0xFF1F), // Fullwidth punctuation
        (0xFF20, 0xFFFF), // Fullwidth letters, halfwidth forms, specials
        (0x10000, 0x1007F), // Linear B
        (0x1F300, 0x1F6FF), // Emoji
        (0x1F900, 0x1FAFF), // More emoji
        (0x20000, 0x2A6DF), // CJK Extension B
        (0xE0000, 0xE007F), // Tags
        (0x10FF80, 0x10FFFF), // The end of the code space
    ];

    /// <summary>
    /// Gets texts that reach full layouts near the end of the code space: a 1-byte window and, after a char of another window that takes 3 or 4 bytes,
    /// that window as the second window (the 35 states that did not fit before every stretch took exactly one lead byte). The second char of that
    /// window is encoded with the second window in the layout.
    /// </summary>
    internal static IEnumerable<string> EndOfCodeSpaceTexts()
    {
        (int Hot, int[] Seconds)[] states = [
            (0xFE00, [0xFFF80, 0x100000, 0x100080, 0x100100]),
            (0xFE80, [0xFFF80, 0x100000, 0x100080, 0x100100]),
            (0xEFE80, [0xFFF80]),
            (0xEFF00, [0xFFF80, 0x100000]),
            (0xEFF80, [0xFFF80, 0x100000, 0x100080]),
            (0xF0000, [0x100000, 0x100080, 0x100100]),
            (0xF0080, [0x100080, 0x100100]),
            (0xF0100, [0x100100]),
            (0xFFF00, [0xFF00, 0x10080, 0xEFE00, 0xEFE80, 0xEFF00]),
            (0xFFF80, [0xFF00, 0x10080, 0xEFE80, 0xEFF00, 0xEFF80]),
            (0x100000, [0xFF00, 0x10080, 0xEFF00, 0xEFF80, 0xF0000]),
        ];

        foreach ((int hot, int[] seconds) in states)
        {
            foreach (int second in seconds)
            {
                int[] codePoints = [hot + 1, hot + 2, hot + 3, second + 5, second + 6, 'a', hot + 4, second + 7, second + 8, 0x10FFFF, 0x100000, 0xFFFF];
                yield return string.Concat(codePoints.Select(char.ConvertFromUtf32));
            }
        }
    }

    /// <summary>
    /// Random text with runs of chars from a few ranges, separated by spaces and punctuation the way words are, with now and then a char from anywhere.
    /// </summary>
    internal static string NextText(Random random, int maxLength)
    {
        int length = random.Next(0, maxLength + 1);
        var text = new StringBuilder(length + 2);

        Span<int> picked = stackalloc int[random.Next(1, 5)];

        for (int i = 0; i < picked.Length; i++)
            picked[i] = random.Next(_ranges.Length);

        while (text.Length < length)
        {
            int roll = random.Next(16);

            if (roll < 2)
            {
                text.Append(" .,\n-:"[random.Next(6)]);
                continue;
            }

            (int start, int end) = _ranges[roll is 2 ? random.Next(_ranges.Length) : picked[random.Next(picked.Length)]];
            int spread = random.Next(3) == 0 ? end - start : Math.Min(end - start, random.Next(1, 64));
            int origin = start + random.Next(end - start - spread + 1);

            for (int i = random.Next(1, 12); i > 0 && text.Length < length; i--)
            {
                int codePoint = origin + random.Next(spread + 1);

                if (codePoint <= 0xFFFF)
                    text.Append((char)codePoint);
                else
                    text.Append(char.ConvertFromUtf32(codePoint));
            }
        }

        return text.ToString();
    }
}
