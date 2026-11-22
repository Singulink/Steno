package com.singulink.steno;

import java.util.Arrays;
import java.util.SplittableRandom;

/**
 * The layout of a state (FORMAT.md section 6): the ranges of code points with their sizes and lead bytes.
 *
 * <p>A layout depends only on the context, the kana slot (in the CJK context), the main window and the second window.
 * Layouts are immutable (every field is final and filled in the constructor), so they can be shared between threads
 * without locks. They are kept in a bounded, set-associative cache in which a newer layout pushes out the oldest of its
 * set, and in the last few layouts of each coder; a layout holds no reference to another layout.
 */
final class Layout {
    static final int GENERAL = 0;
    static final int CJK = 1;
    static final int HANGUL = 2;

    static final int LETTERS = 0;
    static final int HIRAGANA = 1;
    static final int KATAKANA = 2;

    static final int NONE = -1;

    static final int MAX_CODE_POINT = 0x10FFFF;

    /**
     * The cuts of a stretch into parts (section 6.3): the parts lie in U+0000..U+10FBF, U+10FC0..U+1FFFF and
     * U+20000..U+10FFFF. U+10FC0 is U+20000 - 248^2.
     */
    static final int CUT_1 = 0x10FC0;

    static final int CUT_2 = 0x20000;

    /** The number of entries of the cache. A layout takes about 0.7 KB, or 1.5 KB in the CJK context. */
    private static final int CACHE_SIZE = 1024;

    /** The cache is set-associative: each key has a set of this many entries, so that a few colliding keys share it. */
    private static final int WAYS = 4;

    /** The number of sets, a power of 2. */
    private static final int SETS = CACHE_SIZE / WAYS;

    /**
     * The odd multiplier of the hash that picks the set, chosen at random when the class loads. It costs the same as a
     * fixed one, and keeps a text from being made in advance to make the cache miss.
     */
    private static final long HASH = new SplittableRandom().nextLong() | 1L;

    private static final Layout[] CACHE = new Layout[CACHE_SIZE];

    /** The layout of the initial state (section 6.6). */
    static final Layout INITIAL = get(GENERAL, LETTERS, 0xC0, NONE);

    final long key;
    final int context;
    final int slot;
    final int main;
    final int second;

    /** The number of ranges. */
    final int count;

    /** The first code point, last code point, size and first lead byte of each range, in code point order. */
    final int[] lo;

    final int[] hi;
    final int[] size;
    final int[] lead;

    /** For each byte value, the index of the range that it is a lead byte of, or -1 if it is unused. */
    final byte[] rangeOfLead;

    /**
     * The last lead byte of U+0020..U+007E (or U+0020..U+0040) at 1 byte: {@code 0x65}, or {@code 0x27} in the CJK
     * context with kana.
     */
    final int asciiLeadHi;

    /**
     * The first and last UTF-16 code unit of the main window at 1 byte (general context, main window below U+10000), or
     * empty.
     */
    final int mainCharLo;

    final int mainCharHi;

    /** The first and last lead byte of the main window in the general context, or an empty run. */
    final int mainLeadLo;

    final int mainLeadHi;

    /** The lead byte of a code point {@code c} of the main window is {@code c + mainDelta}. */
    final int mainDelta;

    /**
     * In a wide context, the ideographs U+4E00..U+9FFF (CJK) or the Hangul syllables U+AC00..U+D7A3 (Hangul), which
     * take 2 bytes in one range (section 14.3) and only set the pending window to none; otherwise an empty run.
     */
    final int wideCharLo;

    final int wideCharHi;

    /** The first code point and the first lead byte of the range that holds the wide run. */
    final int wideBase;

    final int wideLead;

    /** The lead bytes of the first and the last code point of the wide run, or an empty run. */
    final int wideLeadLo;

    final int wideLeadHi;

    /**
     * In the CJK context, the byte of each code point U+3000..U+30FF that takes 1 byte (U+3000..U+300F and the kana of
     * the kana slot), or 0. These code points only set the pending window to none. {@code null} in the other contexts.
     */
    final byte[] cjkByte;

    /** In the CJK context, the code point of each byte that is the encoding of one of those code points, or 0. */
    final char[] cjkChar;

    private Layout(int context, int slot, int main, int second, long key) {
        this.key = key;
        this.context = context;
        this.slot = slot;
        this.main = main;
        this.second = second;

        int[][] ranges = build(context, slot, main, second);
        count = ranges[0].length;
        lo = ranges[0];
        hi = ranges[1];
        size = ranges[2];
        lead = ranges[3];

        byte[] leads = new byte[256];
        Arrays.fill(leads, (byte) -1);

        for (int r = 0; r < count; r++) {
            int per = pow248(size[r] - 1);
            int n = hi[r] - lo[r] + 1;
            int leadCount = (n + per - 1) / per;

            for (int b = lead[r]; b < lead[r] + leadCount; b++) {
                leads[b] = (byte) r;
            }
        }

        rangeOfLead = leads;
        asciiLeadHi = context == CJK && slot != LETTERS ? 0x40 - 0x19 : 0x7E - 0x19;

        if (context == GENERAL) {
            int r = find(main);
            int end = windowEnd(main);
            mainDelta = lead[r] - lo[r];
            mainLeadLo = main + mainDelta;
            mainLeadHi = end + mainDelta;
            mainCharLo = end < 0x10000 ? main : 1;
            mainCharHi = end < 0x10000 ? end : 0;
        } else {
            mainDelta = 0;
            mainLeadLo = 256;
            mainLeadHi = 0;
            mainCharLo = 1;
            mainCharHi = 0;
        }

        if (context == GENERAL) {
            wideCharLo = 1;
            wideCharHi = 0;
            wideBase = 0;
            wideLead = 0;
            wideLeadLo = 256;
            wideLeadHi = 0;
        } else {
            wideCharLo = context == CJK ? 0x4E00 : 0xAC00;
            wideCharHi = context == CJK ? 0x9FFF : 0xD7A3;
            int r = find(wideCharLo);
            wideBase = lo[r];
            wideLead = lead[r];
            wideLeadLo = wideLead + (wideCharLo - wideBase) / 248;
            wideLeadHi = wideLead + (wideCharHi - wideBase) / 248;
        }

        if (context == CJK) {
            cjkByte = new byte[0x100];
            cjkChar = new char[0x100];

            for (int c = 0x3000; c <= 0x30FF; c++) {
                int r = find(c);

                if (size[r] == 1) {
                    int b = lead[r] + c - lo[r];
                    cjkByte[c - 0x3000] = (byte) b;
                    cjkChar[b] = (char) c;
                }
            }
        } else {
            cjkByte = null;
            cjkChar = null;
        }
    }

    /**
     * Gets the layout of a state from the cache, or builds it.
     *
     * <p>The cache has {@value #CACHE_SIZE} entries in sets of {@value #WAYS}; a new layout goes first in its set and
     * the oldest one leaves it. It is a plain array that threads read and write without locks. That is safe because a
     * layout is immutable with final fields: a thread that sees a reference to it also sees its fields. Two threads may
     * build the same layout at once, or move a set at the same time; one copy wins, and the worst that a race does is
     * drop or repeat an entry.
     */
    static Layout get(int context, int slot, int main, int second) {
        if (context != CJK) {
            slot = LETTERS;
        }

        long key = key(context, slot, main, second);
        int base = setOf(key) * WAYS;
        Layout[] cache = CACHE;

        for (int way = 0; way < WAYS; way++) {
            Layout layout = cache[base + way];

            if (layout != null && layout.key == key) {
                return layout;
            }
        }

        Layout layout = new Layout(context, slot, main, second, key);

        for (int way = WAYS - 1; way > 0; way--) {
            cache[base + way] = cache[base + way - 1];
        }

        cache[base] = layout;
        return layout;
    }

    /** Gets the key of the fields of a state that make its layout (the kana slot only matters in the CJK context). */
    static long key(int context, int slot, int main, int second) {
        return ((long) context << 60) | ((long) slot << 56) | ((long) main << 24) | (second + 1);
    }

    /** Gets the set of the cache that a key goes in. */
    static int setOf(long key) {
        return (int) ((key * HASH) >>> (64 - Integer.numberOfTrailingZeros(SETS)));
    }

    /** Gets whether this is the layout of the given fields of a state. */
    boolean is(int context, int slot, int main, int second) {
        return this.main == main
                && this.second == second
                && this.context == context
                && (context != CJK || this.slot == slot);
    }

    /** Finds the index of the range that holds a code point. */
    int find(int c) {
        int low = 0;
        int high = count - 1;

        while (low < high) {
            int mid = (low + high + 1) >>> 1;

            if (lo[mid] <= c) {
                low = mid;
            } else {
                high = mid - 1;
            }
        }

        return low;
    }

    /** Gets the number of lead bytes that the layout uses. */
    int leadCount() {
        int n = 0;

        for (byte b : rangeOfLead) {
            if (b >= 0) {
                n++;
            }
        }

        return n;
    }

    static int pow248(int e) {
        switch (e) {
            case 0:
                return 1;
            case 1:
                return 248;
            case 2:
                return 248 * 248;
            default:
                return 248 * 248 * 248;
        }
    }

    // ---------- Section 4: windows ----------

    /** Gets the first code point of the window of a code point, or {@link #NONE}. */
    static int windowOf(int c) {
        if (c < 0x300) {
            if (c < 0xC0) {
                return NONE;
            }

            // U+00C0..U+02FF: windows of 128 code points from U+00C0, the last one shorter.
            return 0xC0 + ((c - 0xC0) & ~0x7F);
        }

        if (c < 0x380) {
            return c <= 0x36F ? 0x300 : NONE;
        }

        if (c < 0x500) {
            return c & ~0x7F;
        }

        if (c < 0x600) {
            return c < 0x530 ? 0x500 : c < 0x590 ? 0x530 : 0x590;
        }

        if (c < 0x2000
                || (c >= 0x2100 && c < 0x3000)
                || (c >= 0x3100 && c < 0x3400)
                || (c >= 0xA000 && c < 0xAC00)
                || (c >= 0xE000 && c < 0x20000)
                || c >= 0x40000) {
            return c & ~0x7F;
        }

        return NONE;
    }

    /** Gets the last code point of the window that starts at {@code start}. */
    static int windowEnd(int start) {
        switch (start) {
            case 0x2C0:
                return 0x2FF;
            case 0x300:
                return 0x36F;
            case 0x500:
                return 0x52F;
            case 0x530:
                return 0x58F;
            case 0x590:
                return 0x5FF;
            default:
                return start + 0x7F;
        }
    }

    // ---------- Section 6: building a layout ----------

    /** Builds the ranges of a state: {lo, hi, size, first lead}, each an array over the ranges. */
    private static int[][] build(int context, int slot, int m0, int s0) {
        int m1 = windowEnd(m0);
        int[] pieces = new int[3 * 24];
        int np = 0;

        // Piece 1: the neighbours, in the general context only.
        if (context == GENERAL) {
            np = piece(pieces, np, Math.max(0x80, m0 - 248), m0 - 1, 2);

            if (m1 < MAX_CODE_POINT) {
                np = piece(pieces, np, m1 + 1, Math.min(MAX_CODE_POINT, m1 + 248), 2);
            }
        }

        // Piece 2.
        np = piece(pieces, np, 0x00, 0x1F, 2);
        np = piece(pieces, np, 0x09, 0x0A, 1);
        np = piece(pieces, np, 0x0D, 0x0D, 1);

        // Piece 3.
        if (context == CJK && slot != LETTERS) {
            np = piece(pieces, np, 0x20, 0x40, 1);
            np = piece(pieces, np, 0x41, 0x7E, 2);
        } else {
            np = piece(pieces, np, 0x20, 0x7E, 1);
        }

        // Pieces 4 to 6.
        np = piece(pieces, np, 0x7F, 0xBF, 2);

        if (context != CJK) {
            np = piece(pieces, np, 0x80, 0xEFF, 2);
        }

        np = piece(pieces, np, 0x2000, 0x20CF, 2);

        // Piece 7: the second window.
        if (s0 != NONE) {
            np = piece(pieces, np, s0, windowEnd(s0), 2);
        }

        // Piece 8.
        if (context == GENERAL) {
            np = piece(pieces, np, m0, m1, 1);
        } else if (context == HANGUL) {
            np = piece(pieces, np, m0, m1, 2);
            np = piece(pieces, np, 0x3000, 0x303F, 2);
            np = piece(pieces, np, 0xAC00, 0xD7A3, 2);
            np = piece(pieces, np, 0xFF00, 0xFFFF, 2);
        } else {
            np = piece(pieces, np, m0, m1, 2);
            np = piece(pieces, np, 0x3000, 0x300F, 1);
            np = piece(pieces, np, 0x3010, 0x30FF, 2);

            if (slot == HIRAGANA) {
                np = piece(pieces, np, 0x3041, 0x3096, 1);
            } else if (slot == KATAKANA) {
                np = piece(pieces, np, 0x30A1, 0x30FC, 1);
            }

            np = piece(pieces, np, 0x4E00, 0x9FFF, 2);
            np = piece(pieces, np, 0xFF00, 0xFFFF, 2);

            if (slot == LETTERS) {
                np = piece(pieces, np, 0xFF00, 0xFF1F, 1);
            }
        }

        // Cut the code points into elementary intervals at every piece boundary and at the part boundaries of section
        // 6.3.
        int pieceCount = np / 3;
        int[] bounds = new int[2 * pieceCount + 4];
        int nb = 0;
        bounds[nb++] = 0;
        bounds[nb++] = MAX_CODE_POINT + 1;
        bounds[nb++] = CUT_1;
        bounds[nb++] = CUT_2;

        for (int p = 0; p < np; p += 3) {
            bounds[nb++] = pieces[p];
            bounds[nb++] = pieces[p + 1] + 1;
        }

        // An insertion sort: there are about 30 bounds, and most of them come in order.
        for (int i = 1; i < nb; i++) {
            int v = bounds[i];
            int j = i - 1;

            while (j >= 0 && bounds[j] > v) {
                bounds[j + 1] = bounds[j];
                j--;
            }

            bounds[j + 1] = v;
        }

        int unique = 0;

        for (int i = 0; i < nb; i++) {
            if (unique == 0 || bounds[unique - 1] != bounds[i]) {
                bounds[unique++] = bounds[i];
            }
        }

        // The size of each interval: the last piece that covers it, or 0 if none does (section 6.2).
        int intervals = unique - 1;
        int[] ilo = new int[intervals];
        int[] ihi = new int[intervals];
        int[] isize = new int[intervals];

        for (int i = 0; i < intervals; i++) {
            ilo[i] = bounds[i];
            ihi[i] = bounds[i + 1] - 1;

            for (int p = np - 3; p >= 0; p -= 3) {
                if (pieces[p] <= ilo[i] && ilo[i] <= pieces[p + 1]) {
                    isize[i] = pieces[p + 2];
                    break;
                }
            }
        }

        // Join the uncovered intervals into parts, which do not cross U+10FC0 or U+20000, and size each part (section
        // 6.3).
        int[] plo = new int[intervals];
        int[] phi = new int[intervals];
        int[] psize = new int[intervals];
        int nparts = 0;

        for (int i = 0; i < intervals; i++) {
            if (isize[i] == 0 && nparts > 0 && psize[nparts - 1] == 0 && ilo[i] != CUT_1 && ilo[i] != CUT_2) {
                phi[nparts - 1] = ihi[i];
                continue;
            }

            plo[nparts] = ilo[i];
            phi[nparts] = ihi[i];
            psize[nparts] = isize[i];
            nparts++;
        }

        for (int i = 0; i < nparts; i++) {
            if (psize[i] == 0) {
                int n = phi[i] - plo[i] + 1;
                psize[i] = n <= 248 ? 2 : n <= 248 * 248 ? 3 : 4;
            }
        }

        // Join runs of the same size into ranges (section 6.4) and assign lead bytes (section 6.5).
        int[] rlo = new int[nparts];
        int[] rhi = new int[nparts];
        int[] rsize = new int[nparts];
        int[] rlead = new int[nparts];
        int nr = 0;

        for (int i = 0; i < nparts; i++) {
            if (nr > 0 && rsize[nr - 1] == psize[i]) {
                rhi[nr - 1] = phi[i];
            } else {
                rlo[nr] = plo[i];
                rhi[nr] = phi[i];
                rsize[nr] = psize[i];
                nr++;
            }
        }

        int next = 1;

        for (int r = 0; r < nr; r++) {
            rlead[r] = next;
            int per = pow248(rsize[r] - 1);
            next += (rhi[r] - rlo[r] + 1 + per - 1) / per;
        }

        if (next - 1 > 254) {
            throw new IllegalStateException(
                    "A layout needs more than 254 lead bytes, which FORMAT.md section 13.6 rules out.");
        }

        return new int[][] {
            Arrays.copyOf(rlo, nr), Arrays.copyOf(rhi, nr), Arrays.copyOf(rsize, nr), Arrays.copyOf(rlead, nr)
        };
    }

    private static int piece(int[] pieces, int np, int lo, int hi, int size) {
        if (lo > hi) {
            return np;
        }

        pieces[np] = lo;
        pieces[np + 1] = hi;
        pieces[np + 2] = size;
        return np + 3;
    }
}
