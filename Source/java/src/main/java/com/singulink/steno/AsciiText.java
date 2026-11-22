package com.singulink.steno;

import java.nio.charset.StandardCharsets;

/**
 * Encodes and decodes values that are all ASCII text (tab, line feed, carriage return and U+0020..U+007E) 8 bytes at a
 * time.
 *
 * <p>Such a value never leaves the initial layout (FORMAT.md section 14.1): U+0020..U+007E are {@code c - 0x19}, and
 * tab, line feed and carriage return are {@code 02}, {@code 03} and {@code 05}. The JDK keeps a string of Latin-1 chars
 * as bytes, and {@link String#getBytes(java.nio.charset.Charset) getBytes(ISO_8859_1)} and {@link String#String(byte[],
 * java.nio.charset.Charset) new String(bytes, ISO_8859_1)} copy them with intrinsics, so these paths work on bytes
 * only.
 */
final class AsciiText {
    private static final long Q_LO = Swar.repeat('?');
    private static final long Q_END = Swar.repeat('?' + 1);
    private static final long PRINT_LO = Swar.repeat(0x20);
    private static final long PRINT_END = Swar.repeat(0x7F);
    private static final long STENO_LO = Swar.repeat(0x07);
    private static final long STENO_END = Swar.repeat(0x66);
    private static final long DELTA = Swar.repeat(0x19);

    private AsciiText() {}

    /**
     * Encodes a string that is all ASCII text, or returns {@code null} for any other string.
     *
     * <p>{@code getBytes(ISO_8859_1)} writes {@code ?} for a char above U+00FF, so a {@code ?} byte cannot be told from
     * a real question mark: a string with one goes the general way.
     */
    static byte[] encode(String text) {
        byte[] bytes = text.getBytes(StandardCharsets.ISO_8859_1);
        int n = bytes.length;
        int i = 0;

        for (; n - i >= 8; i += 8) {
            long w = (long) Swar.LONGS.get(bytes, i);

            if (Swar.inRun(w, PRINT_LO, PRINT_END) == Swar.HIGH && Swar.inRun(w, Q_LO, Q_END) == 0) {
                // Every byte is at least 0x20, so no byte borrows from the next.
                Swar.LONGS.set(bytes, i, w - DELTA);
            } else if (!encodeBytes(bytes, i, i + 8)) {
                return null;
            }
        }

        return encodeBytes(bytes, i, n) ? bytes : null;
    }

    private static boolean encodeBytes(byte[] bytes, int from, int to) {
        for (int i = from; i < to; i++) {
            int b = bytes[i];

            if (b >= 0x20 && b <= 0x7E && b != '?') {
                bytes[i] = (byte) (b - 0x19);
            } else if (b == 0x09) {
                bytes[i] = 0x02;
            } else if (b == 0x0A) {
                bytes[i] = 0x03;
            } else if (b == 0x0D) {
                bytes[i] = 0x05;
            } else {
                return false;
            }
        }

        return true;
    }

    /** Decodes Steno bytes that are all ASCII text, or returns {@code null} for any other bytes. */
    static String decode(byte[] src, int offset, int length) {
        byte[] bytes = new byte[length];
        int i = 0;

        for (; length - i >= 8; i += 8) {
            long w = (long) Swar.LONGS.get(src, offset + i);

            if (Swar.inRun(w, STENO_LO, STENO_END) == Swar.HIGH) {
                // Every byte is at most 0x65, so no byte carries into the next.
                Swar.LONGS.set(bytes, i, w + DELTA);
            } else if (!decodeBytes(src, offset + i, bytes, i, 8)) {
                return null;
            }
        }

        return decodeBytes(src, offset + i, bytes, i, length - i)
                ? new String(bytes, StandardCharsets.ISO_8859_1)
                : null;
    }

    private static boolean decodeBytes(byte[] src, int from, byte[] dst, int to, int count) {
        for (int k = 0; k < count; k++) {
            int b = src[from + k];

            if (b >= 0x07 && b <= 0x65) {
                dst[to + k] = (byte) (b + 0x19);
            } else if (b == 0x02) {
                dst[to + k] = 0x09;
            } else if (b == 0x03) {
                dst[to + k] = 0x0A;
            } else if (b == 0x05) {
                dst[to + k] = 0x0D;
            } else {
                return false;
            }
        }

        return true;
    }
}
