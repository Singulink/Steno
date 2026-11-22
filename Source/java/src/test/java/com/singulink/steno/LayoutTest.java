package com.singulink.steno;

import static org.junit.jupiter.api.Assertions.assertEquals;
import static org.junit.jupiter.api.Assertions.assertSame;
import static org.junit.jupiter.api.Assertions.assertTrue;

import java.util.Random;
import org.junit.jupiter.api.Test;

/** The layouts of FORMAT.md sections 6.6 and 11.1 (copied from the specification), and the layout cache. */
class LayoutTest {
    @Test
    void initialLayout() {
        check(
                new int[0],
                "0000 2 01, 0009 1 02, 000B 2 04, 000D 1 05, 000E 2 06, 0020 1 07, 007F 2 66, 00C0 1 67, "
                        + "0140 2 E7, 0F00 3 F6, 2000 2 F7, 20D0 3 F8, 20000 4 FA");
        assertEquals(250, Layout.INITIAL.leadCount());
    }

    @Test
    void generalWithSecondWindow() {
        check(
                new int[] {0x416, 0x416, 0x416, 0x1200},
                "0000 2 01, 0009 1 02, 000B 2 04, 000D 1 05, 000E 2 06, 0020 1 07, 007F 2 66, 0400 1 6A, "
                        + "0480 2 EA, 0F00 3 F5, 1200 2 F6, 1280 3 F7, 2000 2 F8, 20D0 3 F9, 20000 4 FB");
    }

    @Test
    void cjkHiragana() {
        check(
                new int[] {0x3042},
                "0000 2 01, 0009 1 02, 000B 2 04, 000D 1 05, 000E 2 06, 0020 1 07, 0041 2 28, 0140 3 2A, "
                        + "2000 2 2B, 20D0 3 2C, 3000 1 2D, 3010 2 3D, 3041 1 3E, 3097 2 94, 3100 3 95, 4E00 2 96, "
                        + "A000 3 EB, FF00 2 EC, 10000 3 EE, 20000 4 F0");
    }

    @Test
    void hangul() {
        check(
                new int[] {0xD55C},
                "0000 2 01, 0009 1 02, 000B 2 04, 000D 1 05, 000E 2 06, 0020 1 07, 007F 2 66, 0F00 3 75, "
                        + "2000 2 76, 20D0 3 77, 3000 2 78, 3040 3 79, AC00 2 7A, D7A4 3 A8, FF00 2 A9, 10000 3 AB, "
                        + "20000 4 AD");
    }

    @Test
    void fullestLayout() {
        Layout layout = check(
                new int[] {0x1000, 0x1001, 0x1200},
                "0000 2 01, 0009 1 02, 000B 2 04, 000D 1 05, 000E 2 06, 0020 1 07, 007F 2 66, 1000 1 77, "
                        + "1080 2 F7, 1280 3 FA, 2000 2 FB, 20D0 3 FC, 20000 4 FE");
        assertEquals(254, layout.leadCount());
    }

    @Test
    void windows() {
        assertEquals(Layout.NONE, Layout.windowOf(0xBF));
        assertEquals(0xC0, Layout.windowOf(0x13F));
        assertEquals(0x2C0, Layout.windowOf(0x2FF));
        assertEquals(0x2FF, Layout.windowEnd(0x2C0));
        assertEquals(0x300, Layout.windowOf(0x36F));
        assertEquals(Layout.NONE, Layout.windowOf(0x370));
        assertEquals(0x380, Layout.windowOf(0x3FF));
        assertEquals(0x530, Layout.windowOf(0x58F));
        assertEquals(Layout.NONE, Layout.windowOf(0x20CF));
        assertEquals(Layout.NONE, Layout.windowOf(0x30FF));
        assertEquals(0x3100, Layout.windowOf(0x317F));
        assertEquals(Layout.NONE, Layout.windowOf(0xD800));
        assertEquals(0xE000, Layout.windowOf(0xE000));
        assertEquals(Layout.NONE, Layout.windowOf(0x3FFFF));
        assertEquals(0x10FF80, Layout.windowOf(0x10FFFF));

        // There are 7,356 windows (FORMAT.md section 4).
        int windows = 0;
        int last = Layout.NONE;

        for (int c = 0; c <= Layout.MAX_CODE_POINT; c++) {
            int w = Layout.windowOf(c);

            if (w != Layout.NONE && w != last) {
                windows++;
                assertTrue(c == w && Layout.windowEnd(w) >= c);
            }

            last = w;
        }

        assertEquals(7356, windows);
    }

    @Test
    void cacheGivesTheSameLayoutForTheSameState() {
        Layout a = Layout.get(Layout.GENERAL, Layout.LETTERS, 0x400, 0x1200);
        Layout b = Layout.get(Layout.GENERAL, Layout.HIRAGANA, 0x400, 0x1200);
        assertSame(a, b, "the kana slot does not matter outside the CJK context");

        // Thousands of states evict each other from the bounded cache; each layout is still right.
        Random random = new Random(7);

        for (int i = 0; i < 5000; i++) {
            int main = Layout.windowOf(0x400 + random.nextInt(0x1C00));

            if (main == Layout.NONE) {
                continue;
            }

            Layout layout = Layout.get(Layout.GENERAL, Layout.LETTERS, main, Layout.NONE);
            assertTrue(layout.is(Layout.GENERAL, Layout.LETTERS, main, Layout.NONE));
            assertEquals(layout.lead[layout.find(main)], layout.mainLeadLo);
        }
    }

    /**
     * Reaches a state by encoding code points from the initial state, and compares its layout with "first-code-point
     * size lead" rows.
     */
    private static Layout check(int[] codePoints, String expected) {
        EncoderCore encoder = new EncoderCore();
        byte[] out = new byte[4 * codePoints.length];
        int dp = 0;

        for (int c : codePoints) {
            dp = encoder.encodeCodePoint(c, out, dp, out.length);
        }

        Layout layout = encoder.layout;
        StringBuilder actual = new StringBuilder();

        for (int r = 0; r < layout.count; r++) {
            if (r > 0) {
                actual.append(", ");
            }

            actual.append(String.format("%04X %d %02X", layout.lo[r], layout.size[r], layout.lead[r]));
        }

        assertEquals(expected, actual.toString());
        return layout;
    }
}
