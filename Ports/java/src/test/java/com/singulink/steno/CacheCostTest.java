package com.singulink.steno;

import static org.junit.jupiter.api.Assertions.assertArrayEquals;
import static org.junit.jupiter.api.Assertions.assertEquals;
import static org.junit.jupiter.api.Assertions.assertSame;
import static org.junit.jupiter.api.Assertions.assertTrue;

import org.junit.jupiter.api.Test;

/**
 * The layout cache under text that changes the layout at every code point: two states whose keys share a set of the
 * shared cache must not evict each other, and text that alternates between them must cost about as much as text that
 * alternates between two states that do not collide.
 */
class CacheCostTest {
    private static final int A = 0x600;

    @Test
    void collidingStatesShareTheirSet() {
        int b = collidingWindow();
        Layout ab = Layout.get(Layout.GENERAL, Layout.LETTERS, A, b);
        Layout ba = Layout.get(Layout.GENERAL, Layout.LETTERS, b, A);

        for (int i = 0; i < 100; i++) {
            assertSame(ab, Layout.get(Layout.GENERAL, Layout.LETTERS, A, b));
            assertSame(ba, Layout.get(Layout.GENERAL, Layout.LETTERS, b, A));
        }
    }

    @Test
    void alternatingBetweenCollidingStatesCostsAboutTheSame() throws StenoDecodeException {
        int collide = collidingWindow();
        int plain = plainWindow();
        double best = Double.MAX_VALUE;
        double bestColliding = Double.MAX_VALUE;

        // Best of several rounds, so that a busy machine does not decide the test.
        for (int round = 0; round < 5; round++) {
            best = Math.min(best, cost(A, plain, round == 0));
            bestColliding = Math.min(bestColliding, cost(A, collide, round == 0));
        }

        System.out.printf(
                "[cache] alternating U+%X/U+%X: %.1f ns per code point; colliding U+%X/U+%X: %.1f%n",
                A, plain, best, A, collide, bestColliding);
        assertTrue(bestColliding < 4 * best, "colliding states cost " + bestColliding + " ns against " + best);
    }

    /** Finds a block window B in plane 1 whose states (A, B) and (B, A) fall in the same set of the shared cache. */
    private static int collidingWindow() {
        // Plane 1 and the planes from 4 have about 8,600 block windows, against 256 sets: one collides.
        for (int b = 0x10000; b <= 0x10FF80; b += 0x80) {
            if (b == 0x20000) {
                b = 0x40000;
            }

            if (sameSet(A, b)) {
                return b;
            }
        }

        throw new AssertionError("no colliding window");
    }

    private static int plainWindow() {
        for (int b = 0x13580; ; b += 0x80) {
            if (!sameSet(A, b)) {
                return b;
            }
        }
    }

    private static boolean sameSet(int a, int b) {
        long ab = Layout.key(Layout.GENERAL, Layout.LETTERS, a, b);
        long ba = Layout.key(Layout.GENERAL, Layout.LETTERS, b, a);
        return Layout.setOf(ab) == Layout.setOf(ba);
    }

    /** Encodes and decodes 50,000 code points that alternate between two windows; returns the ns per code point. */
    private static double cost(int a, int b, boolean check) throws StenoDecodeException {
        StringBuilder text = new StringBuilder();

        for (int i = 0; i < 50_000; i++) {
            text.appendCodePoint((i & 1) == 0 ? a + i % 50 : b + i % 50);
        }

        String value = text.toString();
        long start = System.nanoTime();
        byte[] bytes = Steno.encode(value);
        String back = Steno.decode(bytes);
        long time = System.nanoTime() - start;

        if (check) {
            assertArrayEquals(OracleCodec.encode(value), bytes);
            assertEquals(value, back);
        }

        return time / 100_000.0;
    }
}
