package com.singulink.steno;

import static org.junit.jupiter.api.Assertions.assertEquals;
import static org.junit.jupiter.api.Assertions.assertFalse;
import static org.junit.jupiter.api.Assertions.assertThrows;
import static org.junit.jupiter.api.Assertions.assertTrue;

import java.util.Arrays;
import org.junit.jupiter.api.Test;

/** Code point order, against String.compareTo and the bytes. */
class ComparisonTest {
    @Test
    void supplementaryAgainstHighBmp() {
        String fullwidth = "すごい！"; // U+FF01
        String emoji = "すごい😀"; // U+1F600

        assertTrue(fullwidth.compareTo(emoji) > 0, "String.compareTo puts the emoji first");
        assertTrue(Steno.compareCodePoints(fullwidth, emoji) < 0, "code point order puts it last");
        assertTrue(Arrays.compareUnsigned(Steno.encode(fullwidth), Steno.encode(emoji)) < 0, "and so do the bytes");
    }

    @Test
    void edgeCases() {
        assertEquals(0, Steno.compareCodePoints("", ""));
        assertTrue(Steno.compareCodePoints("", "a") < 0);
        assertTrue(Steno.compareCodePoints("ab", "a") > 0);

        // A lone high surrogate against the same high surrogate in a pair: the shorter text comes first.
        assertTrue(Steno.compareCodePoints("\uD83D", "😀") < 0);

        // The same high surrogate, then a low surrogate against another char: the pair is a supplementary code point.
        assertTrue(Steno.compareCodePoints("😀", "\uD83D￿") > 0);
        assertTrue("😀".compareTo("\uD83D￿") < 0);

        // A lone surrogate comes before every supplementary character.
        assertTrue(Steno.compareCodePoints("\uDFFF", "𐀀") < 0);
        assertTrue(Steno.CODE_POINT_ORDER.compare("b", "a") > 0);
        assertThrows(NullPointerException.class, () -> Steno.CODE_POINT_ORDER.compare(null, "a"));
    }

    @Test
    void sameAsCodePointsAndBytesOnRandomUnits() {
        RandomText random = new RandomText(50);

        for (int i = 0; i < 20000; i++) {
            String x = random.nextUnits(random.random().nextInt(5));
            String y = random.random().nextBoolean()
                    ? random.nextUnits(random.random().nextInt(5))
                    : x + random.nextUnits(1);
            int expected = Integer.signum(
                    Arrays.compare(x.codePoints().toArray(), y.codePoints().toArray()));

            assertEquals(expected, Integer.signum(Steno.compareCodePoints(x, y)));
            assertEquals(expected, Integer.signum(Arrays.compareUnsigned(Steno.encode(x), Steno.encode(y))));
            assertFalse(expected == 0 && !x.equals(y));
        }
    }
}
