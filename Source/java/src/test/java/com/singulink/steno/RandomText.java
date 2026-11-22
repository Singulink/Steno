package com.singulink.steno;

import java.util.Random;

/** Makes seeded random text that mixes scripts, the way real text and hostile text do. */
final class RandomText {
    /** Pools of code points: first, last. */
    private static final int[][] POOLS = {
        {0x20, 0x7E}, // ASCII
        {0x61, 0x7A}, // ASCII letters
        {0x00, 0x1F}, // controls
        {0x7F, 0xBF}, // DEL, C1 controls, Latin-1 punctuation
        {0xC0, 0x17F}, // Latin-1 and Latin Extended-A
        {0x180, 0x2FF}, // Latin Extended-B, IPA, modifiers
        {0x300, 0x36F}, // combining marks
        {0x370, 0x3FF}, // Greek
        {0x400, 0x4FF}, // Cyrillic
        {0x500, 0x5FF}, // Cyrillic Supplement, Armenian, Hebrew
        {0x600, 0x6FF}, // Arabic
        {0x900, 0x97F}, // Devanagari
        {0xE00, 0xE7F}, // Thai
        {0x1200, 0x137F}, // Ethiopic
        {0x2000, 0x20FF}, // punctuation, currency
        {0x2100, 0x2FFF}, // symbols
        {0x3000, 0x303F}, // CJK punctuation
        {0x3041, 0x3096}, // hiragana
        {0x30A1, 0x30FF}, // katakana
        {0x4E00, 0x9FFF}, // CJK ideographs
        {0xAC00, 0xD7A3}, // Hangul syllables
        {0xA000, 0xABFF}, // Yi and others
        {0xD800, 0xDFFF}, // lone surrogates
        {0xE000, 0xF8FF}, // private use
        {0xFE00, 0xFFFF}, // variation selectors, fullwidth, specials
        {0x1F300, 0x1F6FF}, // emoji
        {0x10000, 0x1FFFF}, // plane 1
        {0x20000, 0x2A6DF}, // CJK extension B
        {0x40000, 0x10FFFF}, // everything above
    };

    private final Random random;

    RandomText(long seed) {
        random = new Random(seed);
    }

    Random random() {
        return random;
    }

    /** Makes a string of about {@code length} code points, in runs of one or a few pools. */
    String next(int length) {
        StringBuilder text = new StringBuilder();
        int[] pool = pick();
        int[] other = pick();

        for (int i = 0; i < length; i++) {
            int r = random.nextInt(100);

            if (r < 8) {
                pool = pick();
            } else if (r < 12) {
                int[] swap = pool;
                pool = other;
                other = swap;
            }

            int[] from = r < 20 ? POOLS[0] : r < 24 ? other : pool;
            text.appendCodePoint(from[0] + random.nextInt(from[1] - from[0] + 1));
        }

        return text.toString();
    }

    /** Makes a string of random UTF-16 code units, with surrogates in any order. */
    String nextUnits(int length) {
        char[] units = new char[length];

        for (int i = 0; i < length; i++) {
            int r = random.nextInt(10);
            units[i] = (char)
                    (r < 3 ? 0xD800 + random.nextInt(0x800) : r < 5 ? random.nextInt(0x80) : random.nextInt(0x10000));
        }

        return new String(units);
    }

    private int[] pick() {
        return POOLS[random.nextInt(POOLS.length)];
    }
}
