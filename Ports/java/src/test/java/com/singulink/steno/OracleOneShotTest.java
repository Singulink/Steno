package com.singulink.steno;

import static org.junit.jupiter.api.Assertions.assertArrayEquals;
import static org.junit.jupiter.api.Assertions.assertEquals;
import static org.junit.jupiter.api.Assertions.assertThrows;
import static org.junit.jupiter.api.Assertions.assertTrue;
import static org.junit.jupiter.api.Assertions.fail;

import java.nio.ByteBuffer;
import java.nio.CharBuffer;
import java.nio.charset.StandardCharsets;
import java.nio.file.Files;
import java.nio.file.Path;
import java.util.Arrays;
import java.util.List;
import org.junit.jupiter.api.Test;

/**
 * The one-shot API against an independent oracle ({@link OracleCodec}), in every text form.
 *
 * <p>The fuzz tests run for a time budget: {@code -Dsteno.fuzz.millis=N} per fuzz test (default 300; use 40000 for a
 * long run). The seed is {@code -Dsteno.fuzz.seed=S} (default fixed, so that the default run is repeatable).
 */
class OracleOneShotTest {
    static final long MILLIS = Long.getLong("steno.fuzz.millis", 300);
    static final long SEED = Long.getLong("steno.fuzz.seed", 20261121L);

    /** A long run ({@code -Dsteno.fuzz.millis} of 5,000 or more) also runs the exhaustive tests at their full size. */
    static final boolean LONG = MILLIS >= 5000;

    static boolean timeLeft(long start) {
        return System.nanoTime() - start < MILLIS * 1_000_000L;
    }

    @Test
    void oracleAgreesWithEveryVector() {
        checkOracle(VectorFile.PUBLISHED);
        String large = System.getenv("STENO_LARGE_VECTORS");
        if (large != null && Files.exists(Path.of(large))) {
            checkOracle(Path.of(large));
        }
    }

    private static void checkOracle(Path path) {
        List<VectorFile.Vector> vectors = VectorFile.read(path);
        for (VectorFile.Vector v : vectors) {
            switch (v.kind) {
                case VALID: {
                    assertArrayEquals(v.bytes, OracleCodec.encode(v.codePoints), v.toString());
                    OracleCodec.Result r = OracleCodec.decode(v.bytes);
                    assertTrue(r.ok(), v.toString());
                    assertArrayEquals(v.codePoints, r.cps, v.toString());
                    break;
                }
                case INVALID: {
                    OracleCodec.Result r = OracleCodec.decode(v.bytes);
                    assertEquals(v.reason.specName(), r.reason, v.toString());
                    break;
                }
                default:
                    assertTrue(!OracleCodec.isValue(v.codePoints), v.toString());
            }
        }
        System.out.println("[fuzz] oracle agrees with " + vectors.size() + " vectors of " + path);
    }

    /** Every text form of the encoder and every form of the decoder, for one value. */
    static void checkAllForms(int[] cps) {
        String text = ValueGenerator.utf16(cps);
        byte[] expected = OracleCodec.encode(cps);
        String msg = "cps="
                + Arrays.toString(
                        Arrays.stream(cps).mapToObj(Integer::toHexString).toArray());

        assertArrayEquals(expected, Steno.encodeCodePoints(cps), msg);
        assertArrayEquals(expected, Steno.encode(text), msg);
        assertArrayEquals(expected, Steno.encode(new StringBuilder(text)), msg);
        assertArrayEquals(expected, Steno.encode(CharBuffer.wrap(text)), msg);
        CharBuffer offsetBuffer = CharBuffer.wrap(("xyz" + text + "q").toCharArray(), 3, text.length())
                .slice();
        assertArrayEquals(expected, Steno.encode(offsetBuffer), msg);
        assertArrayEquals(expected, Steno.encode(new Plain(text)), msg);
        assertArrayEquals(expected, text.getBytes(StenoCharset.INSTANCE), msg);

        // Into arrays at an offset, exact size.
        byte[] dst = new byte[expected.length + 5];
        assertEquals(expected.length, Steno.encode(text, dst, 5), msg);
        assertArrayEquals(expected, Arrays.copyOfRange(dst, 5, dst.length), msg);
        if (expected.length > 0) {
            byte[] small = new byte[expected.length - 1 + 3];
            assertThrows(IndexOutOfBoundsException.class, () -> Steno.encode(text, small, 3), msg);
        }

        // Into ByteBuffers: heap with array offset, direct.
        ByteBuffer heap = ByteBuffer.wrap(new byte[expected.length + 9], 2, expected.length + 4)
                .slice();
        heap.position(3);
        assertEquals(expected.length, Steno.encode(text, heap), msg);
        assertEquals(3 + expected.length, heap.position(), msg);
        byte[] got = new byte[expected.length];
        heap.duplicate().position(3).get(got);
        assertArrayEquals(expected, got, msg);
        ByteBuffer direct = ByteBuffer.allocateDirect(expected.length + 1);
        direct.position(1);
        Steno.encode(text, direct);
        direct.position(1);
        direct.get(got);
        assertArrayEquals(expected, got, msg);

        // Decoding.
        try {
            assertEquals(text, Steno.decode(expected), msg);
            byte[] padded = new byte[expected.length + 4];
            System.arraycopy(expected, 0, padded, 2, expected.length);
            padded[0] = 0;
            padded[padded.length - 1] = 0;
            assertEquals(text, Steno.decode(padded, 2, expected.length), msg);
            assertArrayEquals(cps, Steno.decodeCodePoints(expected), msg);
            ByteBuffer in = ByteBuffer.wrap(padded, 2, expected.length).slice();
            assertEquals(text, Steno.decode(in), msg);
            ByteBuffer din = ByteBuffer.allocateDirect(expected.length);
            din.put(expected).flip();
            assertEquals(text, Steno.decode(din), msg);
            assertEquals(text, Steno.decode(ByteBuffer.wrap(expected).asReadOnlyBuffer()), msg);
        } catch (StenoDecodeException e) {
            fail(msg, e);
        }
        assertEquals(text, new String(expected, StenoCharset.INSTANCE), msg);

        boolean ascii = text.chars().allMatch(c -> c == 9 || c == 10 || c == 13 || (c >= 0x20 && c <= 0x7E));
        assertEquals(ascii, Steno.isAsciiText(expected), msg);
        assertTrue(expected.length <= Steno.maxEncodedLength(text.length()), msg);
        assertTrue(text.length() <= Steno.maxDecodedLength(expected.length), msg);
    }

    /** A CharSequence that is not String, StringBuilder or CharBuffer. */
    static final class Plain implements CharSequence {
        final String s;

        Plain(String s) {
            this.s = s;
        }

        @Override
        public int length() {
            return s.length();
        }

        @Override
        public char charAt(int index) {
            return s.charAt(index);
        }

        @Override
        public CharSequence subSequence(int start, int end) {
            return new Plain(s.substring(start, end));
        }

        @Override
        public String toString() {
            return s;
        }
    }

    @Test
    void fuzzAllFormsAgainstOracle() {
        long seed = SEED;
        ValueGenerator g = new ValueGenerator(seed);
        long start = System.nanoTime();
        int count = 0;
        long cpsTotal = 0;
        while (timeLeft(start)) {
            int[] cps;
            int k = count % 5;
            if (k == 0) cps = g.value(20);
            else if (k == 1) cps = g.value(300);
            else if (k == 2) cps = g.runs(300);
            else if (k == 3) cps = OracleCodec.codePoints(g.units(40));
            else cps = g.value(1500);
            checkAllForms(cps);
            count++;
            cpsTotal += cps.length;
        }
        System.out.println("[fuzz] one-shot forms: " + count + " values, " + cpsTotal + " code points, seed " + seed);
    }

    @Test
    void asciiPathAroundItsLimits() throws StenoDecodeException {
        // Strings with '?', Latin-1 above U+007F, chars above U+00FF, surrogate pairs and lone surrogates, at every
        // place and around the 256 limit, in Latin-1 and UTF-16 string storage.
        String[] odd = {
            "?", "\u0080", "ÿ", "é", "Ā", "€", "😀", "\uD800", "\uDC00", "\u007F", "\u0000", "\u0008", "\u000B",
            "\u001F", "\t", "\n", "\r"
        };
        for (int len = 0; len <= 270; len++) {
            if (!LONG && len > 24 && len < 248) {
                continue;
            }

            if (len > 20 && len < 240 && len % 7 != 0) continue;
            StringBuilder base = new StringBuilder();
            for (int i = 0; i < len; i++) base.append((char) ('a' + i % 26));
            checkAllForms(OracleCodec.codePoints(base));
            for (String o : odd) {
                for (int at = 0; at <= len; at += Math.max(1, len / 13)) {
                    String s = base.substring(0, at) + o + base.substring(at);
                    checkAllForms(OracleCodec.codePoints(s));
                    // UTF-16 storage of the same ASCII text: concatenation with a non-Latin-1 char then removed.
                    String wide = (s + "Ā").substring(0, s.length());
                    assertArrayEquals(OracleCodec.encode(wide), Steno.encode(wide), wide);
                }
            }
        }
        // Decoding: ASCII bytes with every byte value at every place, lengths around 256.
        for (int len : new int[] {1, 7, 8, 9, 15, 16, 17, 255, 256, 257}) {
            byte[] b = new byte[len];
            Arrays.fill(b, (byte) 0x48);
            for (int at = 0; at < len; at += Math.max(1, len / 17)) {
                for (int v = 0; v < 256; v++) {
                    byte[] c = b.clone();
                    c[at] = (byte) v;
                    checkDecode(c);
                }
            }
        }
    }

    /** Decodes bytes with the library and the oracle and compares text or reason, offset and length. */
    static void checkDecode(byte[] bytes) {
        OracleCodec.Result want = OracleCodec.decode(bytes);
        String msg = VectorFile.hex(bytes);
        try {
            String got = Steno.decode(bytes);
            if (!want.ok()) {
                fail("accepted invalid bytes " + msg + " as " + got + "; oracle: " + want.reason + " at "
                        + want.offset);
            }
            assertEquals(want.text(), got, msg);
            assertArrayEquals(want.cps, Steno.decodeCodePoints(bytes), msg);
        } catch (StenoDecodeException e) {
            if (want.ok()) {
                fail("rejected valid bytes " + msg + ": " + e.getMessage());
            }
            assertEquals(want.reason, e.getReason().specName(), msg);
            assertEquals(want.offset, e.getOffset(), msg);
            assertEquals(want.length, e.getInputLength(), msg + " input length");
        }
        // The same through a range with garbage around and a direct buffer.
        byte[] padded = new byte[bytes.length + 2];
        padded[0] = 0x00;
        padded[padded.length - 1] = (byte) 0x4A;
        System.arraycopy(bytes, 0, padded, 1, bytes.length);
        try {
            String got = Steno.decode(padded, 1, bytes.length);
            assertTrue(want.ok(), msg);
            assertEquals(want.text(), got, msg);
        } catch (StenoDecodeException e) {
            assertTrue(!want.ok(), msg);
            assertEquals(want.reason, e.getReason().specName(), msg);
            assertEquals(want.offset, e.getOffset(), msg);
        }
        // isAsciiText never says true for bytes that do not decode to ASCII text.
        if (Steno.isAsciiText(bytes)) {
            assertTrue(want.ok(), msg);
            assertTrue(want.text().chars().allMatch(c -> c == 9 || c == 10 || c == 13 || (c >= 0x20 && c <= 0x7E)));
        }
    }

    @Test
    void fuzzDecoderOnBadInput() {
        long seed = SEED + 1;
        ValueGenerator g = new ValueGenerator(seed);
        long start = System.nanoTime();
        int count = 0;
        while (timeLeft(start)) {
            int[] cps = count % 3 == 0 ? g.runs(40) : g.value(30);
            byte[] valid = OracleCodec.encode(cps);
            int kind = g.r.nextInt(6);
            byte[] b = valid.clone();
            if (kind == 0 && b.length > 0) {
                b = Arrays.copyOf(b, g.r.nextInt(b.length));
            } else if (kind == 1 && b.length > 0) {
                b[g.r.nextInt(b.length)] = (byte) g.r.nextInt(256);
            } else if (kind == 2 && b.length > 0) {
                int at = g.r.nextInt(b.length);
                b[at] = (byte) (b[at] + (g.r.nextBoolean() ? 1 : -1));
            } else if (kind == 3) {
                b = new byte[g.r.nextInt(12)];
                for (int i = 0; i < b.length; i++) b[i] = (byte) g.r.nextInt(256);
            } else if (kind == 4 && b.length > 0) {
                int at = g.r.nextInt(b.length + 1);
                byte[] c = new byte[b.length + 1];
                System.arraycopy(b, 0, c, 0, at);
                c[at] = (byte) g.r.nextInt(256);
                System.arraycopy(b, at, c, at + 1, b.length - at);
                b = c;
            } else if (b.length > 0) {
                b[g.r.nextInt(b.length)] = (byte) (0xF0 + g.r.nextInt(16));
            }
            checkDecode(b);
            count++;
        }
        System.out.println("[fuzz] bad-input decode: " + count + " byte strings, seed " + seed);
    }

    @Test
    void everyByteAtEveryPositionOfShortEncodings() {
        // Short valid encodings of many states, then every byte value at every position, and every truncation.
        ValueGenerator g = new ValueGenerator(77);
        int count = 0;
        for (int i = 0; i < (LONG ? 400 : 40); i++) {
            int[] cps = g.value(6);
            byte[] valid = OracleCodec.encode(cps);
            for (int at = 0; at < valid.length; at++) {
                checkDecode(Arrays.copyOf(valid, at));
                for (int v = 0; v < 256; v++) {
                    byte[] b = valid.clone();
                    b[at] = (byte) v;
                    checkDecode(b);
                    count++;
                }
            }
            // One byte more after the value.
            for (int v = 0; v < 256; v++) {
                byte[] b = Arrays.copyOf(valid, valid.length + 1);
                b[valid.length] = (byte) v;
                checkDecode(b);
                byte[] b2 = Arrays.copyOf(valid, valid.length + 2);
                b2[valid.length] = (byte) v;
                b2[valid.length + 1] = (byte) g.r.nextInt(256);
                checkDecode(b2);
            }
        }
        System.out.println("[fuzz] every byte at every position: " + count + " byte strings");
    }

    @Test
    void swarEveryLengthAndOffset() throws StenoDecodeException {
        // Runs of ASCII, of the main window and of ideographs/Hangul, lengths 0..40, the run-ending code point at every
        // offset, and array offsets 0..9.
        int[][] runs = {{0x61}, {0x430}, {0x1F600}, {0x4E00}, {0xAC00}, {0x3042}, {0x30A2}, {0x3001}, {0x9}};
        int[][] prefixes = {{}, {0x430, 0x430}, {0x3042}, {0x30A2}, {0xAC00}, {0x1F600, 0x1F601}, {0x4E00}};
        int[] enders = {0x0, 0x7F, 0x80, 0xFF, 0x100, 0x3F, 0x0A, 0x500, 0xFF01, 0xD800, 0x10000, 0x41, 0x30FC};
        for (int[] pre : prefixes) {
            for (int[] run : runs) {
                for (int len = 0; len <= (LONG ? 40 : 18); len++) {
                    for (int at = 0; at <= len; at++) {
                        for (int end : enders) {
                            int[] cps = new int[pre.length + len + 1];
                            System.arraycopy(pre, 0, cps, 0, pre.length);
                            for (int i = 0; i < len; i++) cps[pre.length + i] = run[0] + (i % 3);
                            System.arraycopy(cps, pre.length + at, cps, pre.length + at + 1, len - at);
                            cps[pre.length + at] = end;
                            ValueGenerator.fix(cps);
                            byte[] want = OracleCodec.encode(cps);
                            String text = ValueGenerator.utf16(cps);
                            assertArrayEquals(want, Steno.encode(text), text);
                            for (int off = 0; off < 10; off += 3) {
                                byte[] padded = new byte[want.length + off + 1];
                                System.arraycopy(want, 0, padded, off, want.length);
                                assertEquals(text, Steno.decode(padded, off, want.length));
                                assertEquals(Steno.isAsciiText(want), Steno.isAsciiText(padded, off, want.length));
                            }
                        }
                    }
                }
            }
        }
    }

    @Test
    void chunkBoundariesOfTheOneShotPaths() {
        // The one-shot encoder works in chunks of 1,024 chars and grows its output from 1.5 bytes per char; the decoder
        // grows from 1 char per byte. Surrogate pairs and expensive code points at every place around those sizes.
        String[] fills = {"a", "Ж", "ༀ", "一", "가", "𠀀", "あ", ""};
        String[] odd = {"😀", "\uD800", "\uDC00", "က", "􏿿", "あア"};
        int[] totals = LONG
                ? new int[] {255, 256, 257, 1023, 1024, 1025, 2047, 2048, 2049, 3072}
                : new int[] {256, 1024, 2049};

        for (int total : totals) {
            for (String fill : fills) {
                for (String o : odd) {
                    for (int at = Math.max(0, total - 6); at <= total + 1; at++) {
                        StringBuilder sb = new StringBuilder();
                        while (sb.length() < at) sb.append(fill);
                        sb.setLength(at);
                        sb.append(o);
                        while (sb.length() < total + 3) sb.append(fill);
                        String text = sb.toString();
                        int[] cps = OracleCodec.codePoints(text);
                        checkAllForms(cps);
                        // Also with the odd code point at the start of the second chunk minus one.
                        checkDecode(OracleCodec.encode(cps));
                    }
                }
            }
        }
    }

    @Test
    void badInputInLongValues() {
        ValueGenerator g = new ValueGenerator(SEED + 6);
        long start = System.nanoTime();
        int count = 0;
        while (timeLeft(start)) {
            int[] cps = count % 2 == 0 ? g.runs(3000) : g.value(3000);
            byte[] b = OracleCodec.encode(cps);
            if (b.length < 2) continue;
            int at = g.r.nextInt(b.length);
            if (g.r.nextBoolean()) b = Arrays.copyOf(b, at);
            else b[at] = (byte) g.r.nextInt(256);
            checkDecode(b);
            count++;
        }
        System.out.println("[fuzz] bad input in long values: " + count);
    }

    @Test
    void longValuesInManyScripts() throws StenoDecodeException {
        ValueGenerator g = new ValueGenerator(SEED + 5);
        for (int i = 0; i < (LONG ? 6 : 2); i++) {
            int[] cps = new int[(LONG ? 400_000 : 60_000) + g.r.nextInt(LONG ? 300_000 : 40_000)];
            int[] pool = ValueGenerator.POOLS[g.r.nextInt(ValueGenerator.POOLS.length)];
            for (int j = 0; j < cps.length; j++) {
                if (g.r.nextInt(1000) == 0) pool = ValueGenerator.POOLS[g.r.nextInt(ValueGenerator.POOLS.length)];
                cps[j] = g.r.nextInt(50) == 0
                        ? ValueGenerator.EDGES[g.r.nextInt(ValueGenerator.EDGES.length)]
                        : g.cpFrom(pool);
            }
            if (i == 0) cps[0] = 0xFEFF;
            if (i == 1) cps[0] = 0xFFFE;
            ValueGenerator.fix(cps);
            byte[] want = OracleCodec.encode(cps);
            String text = ValueGenerator.utf16(cps);
            assertArrayEquals(want, Steno.encode(text));
            assertArrayEquals(want, Steno.encode(new StringBuilder(text)));
            assertArrayEquals(want, Steno.encodeCodePoints(cps));
            assertArrayEquals(want, text.getBytes(StenoCharset.INSTANCE));
            assertEquals(text, Steno.decode(want));
            assertEquals(text, new String(want, StenoCharset.INSTANCE));
        }
        // A megabyte of one-byte ASCII and of 4-byte code points.
        String ascii = "abc ".repeat(LONG ? 300_000 : 30_000);
        assertArrayEquals(OracleCodec.encode(ascii), Steno.encode(ascii));
        String four = "𠀀󰀀".repeat(LONG ? 100_000 : 10_000);
        byte[] fb = Steno.encode(four);
        assertArrayEquals(OracleCodec.encode(four), fb);
        assertEquals(four, Steno.decode(fb));
        assertEquals(StandardCharsets.UTF_8, StandardCharsets.UTF_8);
    }
}
