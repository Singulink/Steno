package com.singulink.steno;

import java.util.Arrays;
import java.util.SplittableRandom;

/** Values made to hit the fast paths, the state changes and the edges of the format. */
final class ValueGenerator {
    static final int[] EDGES = {
        0x00, 0x08, 0x09, 0x0A, 0x0B, 0x0C, 0x0D, 0x0E, 0x1F, 0x20, 0x3F, 0x40, 0x41, 0x5A, 0x5B, 0x60, 0x61, 0x7A,
        0x7B, 0x7E, 0x7F, 0x80, 0xBF, 0xC0, 0xFF, 0x13F, 0x140, 0x1BF, 0x1C0, 0x2BF, 0x2C0, 0x2FF, 0x300, 0x36F, 0x370,
        0x37F, 0x380, 0x3FF, 0x400, 0x4FF, 0x500, 0x52F, 0x530, 0x58F, 0x590, 0x5FF, 0x600, 0xEFF, 0xF00, 0xFFF, 0x1000,
        0x107F, 0x1200, 0x1FFF, 0x2000, 0x20CF, 0x20D0, 0x20FF, 0x2100, 0x2FFF, 0x3000, 0x300F, 0x3010, 0x303F, 0x3040,
        0x3041, 0x3096, 0x3097, 0x30A0, 0x30A1, 0x30FA, 0x30FB, 0x30FC, 0x30FD, 0x30FF, 0x3100, 0x33FF, 0x3400, 0x4DFF,
        0x4E00, 0x9FFF, 0xA000, 0xABFF, 0xAC00, 0xD7A3, 0xD7A4, 0xD7FF, 0xD800, 0xDBFF, 0xDC00, 0xDFFF, 0xE000, 0xFEFF,
        0xFF00, 0xFF1F, 0xFF20, 0xFFFE, 0xFFFF, 0x10000, 0x10E80, 0x10EFF, 0x10F00, 0x10FBF, 0x10FC0, 0x11200, 0x1F600,
        0x1FFFF, 0x20000, 0x2A6DF, 0x3FFFF, 0x40000, 0x10FF7F, 0x10FF80, 0x10FFFF
    };

    static final int[][] POOLS = {
        {0x20, 0x7E},
        {0x41, 0x5A},
        {0x61, 0x7A},
        {0x09, 0x0D},
        {0x00, 0x1F},
        {0x20, 0x40},
        {0x7F, 0xBF},
        {0xC0, 0x13F},
        {0x140, 0x2FF},
        {0x300, 0x36F},
        {0x370, 0x3FF},
        {0x400, 0x47F},
        {0x480, 0x4FF},
        {0x500, 0x5FF},
        {0x600, 0x6FF},
        {0xE00, 0xE7F},
        {0xE80, 0xF7F},
        {0x1000, 0x107F},
        {0x1200, 0x127F},
        {0x1F00, 0x1FFF},
        {0x2000, 0x20FF},
        {0x2100, 0x2FFF},
        {0x3000, 0x300F},
        {0x3010, 0x303F},
        {0x3041, 0x3096},
        {0x30A1, 0x30FC},
        {0x30FB, 0x30FF},
        {0x3100, 0x33FF},
        {0x3400, 0x4DFF},
        {0x4E00, 0x9FFF},
        {0xA000, 0xABFF},
        {0xAC00, 0xD7A3},
        {0xD800, 0xDBFF},
        {0xDC00, 0xDFFF},
        {0xE000, 0xFEFF},
        {0xFF00, 0xFF1F},
        {0xFF20, 0xFFFF},
        {0x10000, 0x1007F},
        {0x10E80, 0x10FFF},
        {0x10F80, 0x1103F},
        {0x11200, 0x1127F},
        {0x1F600, 0x1F67F},
        {0x1FF80, 0x1FFFF},
        {0x20000, 0x2007F},
        {0x3FF80, 0x4007F},
        {0x10FF00, 0x10FFFF},
        {0, 0x10FFFF}
    };

    final SplittableRandom r;

    ValueGenerator(long seed) {
        r = new SplittableRandom(seed);
    }

    int cpFrom(int[] pool) {
        return pool[0] + r.nextInt(pool[1] - pool[0] + 1);
    }

    int anyWindowCp() {
        // A random window, often ones far apart.
        int c = r.nextInt(0x110000);
        while (OracleCodec.windowStart(c) < 0) c = r.nextInt(0x110000);
        return c;
    }

    /** A value as code points: runs of one or two pools, with edges and random windows mixed in. */
    int[] value(int maxLen) {
        int len = r.nextInt(maxLen + 1);
        int[] cps = new int[len];
        int[] pool = POOLS[r.nextInt(POOLS.length)];
        int[] other = POOLS[r.nextInt(POOLS.length)];
        int winA = anyWindowCp(), winB = anyWindowCp();
        int mode = r.nextInt(4);
        for (int i = 0; i < len; i++) {
            int x = r.nextInt(100);
            if (x < 3 + mode * 3) {
                pool = POOLS[r.nextInt(POOLS.length)];
            }
            int c;
            if (x < 5) c = EDGES[r.nextInt(EDGES.length)];
            else if (x < 9) c = OracleCodec.windowStart(winA) + r.nextInt(16);
            else if (x < 12) c = OracleCodec.windowStart(winB) + r.nextInt(16);
            else if (x < 15) c = anyWindowCp();
            else if (x < 25) c = cpFrom(other);
            else c = cpFrom(pool);
            cps[i] = c;
        }
        return fix(cps);
    }

    /** Long runs of one kind, of length 0..maxRun, with a single different code point at a random place. */
    int[] runs(int maxRun) {
        int[] pool = POOLS[r.nextInt(POOLS.length - 1)];
        int[] prefix = r.nextBoolean() ? value(6) : new int[0];
        int n = r.nextInt(maxRun + 1);
        int[] cps = Arrays.copyOf(prefix, prefix.length + n + 1);
        int odd = r.nextInt(n + 1);
        for (int i = 0; i <= n; i++) {
            cps[prefix.length + i] = i == odd ? EDGES[r.nextInt(EDGES.length)] : cpFrom(pool);
        }
        return fix(cps);
    }

    /** Breaks every high surrogate code point directly followed by a low one. */
    static int[] fix(int[] cps) {
        for (int i = 1; i < cps.length; i++) {
            if (cps[i - 1] >= 0xD800 && cps[i - 1] <= 0xDBFF && cps[i] >= 0xDC00 && cps[i] <= 0xDFFF) {
                cps[i] = 0x41;
            }
        }
        return cps;
    }

    static String utf16(int[] cps) {
        StringBuilder b = new StringBuilder();
        for (int c : cps) b.appendCodePoint(c);
        return b.toString();
    }

    /** Random UTF-16 units, surrogates in any order. */
    String units(int maxLen) {
        int len = r.nextInt(maxLen + 1);
        char[] u = new char[len];
        for (int i = 0; i < len; i++) {
            int x = r.nextInt(10);
            u[i] = (char) (x < 3 ? 0xD800 + r.nextInt(0x800) : x < 6 ? r.nextInt(0x80) : r.nextInt(0x10000));
        }
        return new String(u);
    }
}
