using System.Text;

namespace Singulink.Steno.Tests;

/// <summary>
/// Generates random text that mixes runs from many scripts, so that the encoder constantly has to switch, define, quote and evict windows.
/// </summary>
internal static class RandomText
{
    // Inclusive code point ranges. Several are deliberately awkward: blocks that straddle 128-char boundaries, private use, lone surrogates, controls.
    private static readonly (int Start, int End)[] _ranges = [
        (0x0020, 0x007E), // ASCII
        (0x0020, 0x007E), // ASCII (weighted)
        (0x0000, 0x001F), // C0 controls
        (0x007F, 0x009F), // DEL + C1 controls
        (0x00A0, 0x00FF), // Latin-1
        (0x0100, 0x017F), // Latin Extended-A
        (0x0180, 0x024F), // Latin Extended-B
        (0x0300, 0x036F), // Combining marks
        (0x0370, 0x03FF), // Greek
        (0x0400, 0x04FF), // Cyrillic
        (0x0531, 0x058F), // Armenian
        (0x0590, 0x05FF), // Hebrew
        (0x0600, 0x06FF), // Arabic
        (0x0900, 0x097F), // Devanagari
        (0x0980, 0x09FF), // Bengali
        (0x0E00, 0x0E7F), // Thai
        (0x10A0, 0x10FF), // Georgian
        (0x1200, 0x137F), // Ethiopic
        (0x1E00, 0x1EFF), // Latin Extended Additional
        (0x1F00, 0x1FFF), // Greek Extended
        (0x2000, 0x206F), // General Punctuation
        (0x2100, 0x21FF), // Letterlike, arrows
        (0x2600, 0x27BF), // Symbols, dingbats
        (0x3000, 0x303F), // CJK punctuation
        (0x3040, 0x309F), // Hiragana
        (0x30A0, 0x30FF), // Katakana
        (0x4E00, 0x9FFF), // CJK ideographs
        (0xAC00, 0xD7A3), // Hangul
        (0xD800, 0xDFFF), // Lone surrogates
        (0xE000, 0xF8FF), // Private use
        (0xFE00, 0xFE0F), // Variation selectors
        (0xFF00, 0xFFEF), // Fullwidth forms
        (0xFFF0, 0xFFFF), // Specials, non-characters
        (0x0000, 0xFFFF), // Anything in the BMP
        (0x1F300, 0x1F6FF), // Emoji
        (0x1F900, 0x1FAFF), // More emoji
        (0x20000, 0x2A6DF), // CJK Extension B
        (0x10000, 0x10FFFF), // Anything supplementary
    ];

    // Ranges around the places where the layout of Steno changes: kana, kanji and Hangul mixes with the letters that reset the kana slot, the windows
    // next to each other, the ends of the stretches (U+10000 and the cut after U+1FE00), the end of the code space, and the ranges without windows.
    private static readonly (int Start, int End)[] _edgeRanges = [
        (0x0041, 0x005A), // Latin capital letters (reset the kana slot)
        (0x0061, 0x007A), // Latin small letters
        (0x0020, 0x0040), // ASCII that is 1 byte in every layout
        (0x0000, 0x001F), // C0 controls
        (0x00B0, 0x0150), // Around the initial main window
        (0x0370, 0x0390), // Greek, next to a range without windows
        (0x04F0, 0x0600), // The odd-sized windows
        (0x2000, 0x20D0), // General punctuation and the 2-byte piece
        (0x3000, 0x303F), // CJK punctuation
        (0x3040, 0x30FF), // Kana
        (0x3090, 0x30B0), // The ends of the hiragana slot and the start of katakana
        (0x30F0, 0x3100), // The end of katakana
        (0x4E00, 0x4F00), // Kanji
        (0x9F80, 0xA080), // The end of the ideographs and the windows after them
        (0xAC00, 0xAD00), // Hangul
        (0xD780, 0xD830), // The end of Hangul and the high surrogates
        (0xD800, 0xDFFF), // Lone surrogates
        (0xFF00, 0xFF30), // Fullwidth forms
        (0xFFF0, 0x10080), // Across the end of the BMP
        (0x1F300, 0x1F320), // Emoji
        (0x1F600, 0x1F64F), // Emoji
        (0x1FD80, 0x1FE80), // Across the cut after U+1FE00
        (0x1FF80, 0x20080), // Across the end of the windows of plane 1
        (0x20000, 0x20100), // CJK Extension B
        (0x3FF80, 0x40080), // Across the start of the windows of the higher planes
        (0x10FF00, 0x10FFFF), // The end of the code space
    ];

    /// <summary>
    /// Generates text that mixes runs from many scripts, with the edges of the format of Steno, version 1, weighted up.
    /// </summary>
    public static string NextMixed(Random random, int maxLength) => Next(random, maxLength, random.Next(2) == 0 ? _edgeRanges : _ranges);

    public static string Next(Random random, int maxLength) => Next(random, maxLength, _ranges);

    private static string Next(Random random, int maxLength, (int Start, int End)[] source)
    {
        int length = random.Next(0, maxLength + 1);
        var text = new StringBuilder(length + 2);

        // Limit the number of distinct ranges per string so that some strings are single-script and others are chaotic.
        int rangeCount = random.Next(1, 7);
        Span<int> ranges = stackalloc int[rangeCount];

        for (int i = 0; i < rangeCount; i++)
            ranges[i] = random.Next(source.Length);

        while (text.Length < length)
        {
            (int start, int end) = source[ranges[random.Next(rangeCount)]];
            int runLength = random.Next(1, 9);

            // Narrow some runs to a small neighbourhood, which is what real text looks like.
            int spread = random.Next(3) == 0 ? end - start : Math.Min(end - start, random.Next(1, 96));
            int origin = start + random.Next(end - start - spread + 1);

            for (int i = 0; i < runLength && text.Length < length; i++)
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
