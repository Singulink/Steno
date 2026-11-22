package com.singulink.steno;

import static org.junit.jupiter.api.Assertions.assertArrayEquals;
import static org.junit.jupiter.api.Assertions.assertEquals;
import static org.junit.jupiter.api.Assertions.assertThrows;

import java.util.Arrays;
import java.util.Random;
import org.junit.jupiter.api.Test;

/**
 * The fast paths against the plain path, which encodes and decodes every code point the general way, and the word
 * helpers they rest on.
 */
class FastPathTest {
    /** Code points that change the state in different ways, to put at every offset around an 8-byte boundary. */
    private static final int[] CHANGES = {
        0x09, 0x0A, 0x3F, 0x7F, 0xE9, 0x416, 0x3B1, 0x142, 0x1200, 0x4E2D, 0x3042, 0x30A2, 0xD55C, 0xFF01, 0x1F600,
        0xD800, 0xDC00, 0x00, 0x2014, 0x20000,
    };

    /** Runs of 1-byte code points in several layouts. */
    private static final String[] RUNS = {
        "abcdefghijklmnopqrstuvwxyz0123456789",
        "Москва и Санкт Петербург",
        "ελληνικά γράμματα",
        "ひらがなのぶんしょう",
        "カタカナノブンショウ",
        "漢字漢字漢字漢字漢字漢字",
        "한국어한국어한국어",
        "😀😁😂🤣😃😄😅😆",
        "\t\r\n",
    };

    @Test
    void stateChangesAroundWordBoundaries() throws StenoDecodeException {
        for (String run : RUNS) {
            for (String next : RUNS) {
                for (int at = 0; at <= 20; at++) {
                    for (int change : CHANGES) {
                        StringBuilder text = new StringBuilder();
                        text.append(run.repeat(2), 0, Math.min(at, 2 * run.length()));
                        text.appendCodePoint(change);
                        text.append(next.repeat(2));
                        check(text.toString());
                    }
                }
            }
        }
    }

    @Test
    void randomTextAndUnits() throws StenoDecodeException {
        RandomText random = new RandomText(60);

        for (int i = 0; i < 3000; i++) {
            check(
                    i % 3 == 0
                            ? random.nextUnits(random.random().nextInt(40))
                            : random.next(random.random().nextInt(300)));
        }
    }

    @Test
    void asciiTextPath() throws StenoDecodeException {
        String[] texts = {
            "a", "Hello, world!", "tab\there", "line\nfeed\r\n", "question?", "?", "????????????????", "café",
                    "naïve text",
            "ÿ", "€uro", "x\u0000y", "\u0001", "del\u007F", "1234567", "12345678", "123456789",
                    "a\tb\nc\rd\te\nf\rg\th",
            "Ж", "a".repeat(255), "a".repeat(256), "a".repeat(257), "?".repeat(300), "a".repeat(255) + "Ж",
        };

        for (String text : texts) {
            check(text);
            check(text + "?");
            check("?" + text);
        }

        assertEquals("a", AsciiText.decode(new byte[] {0x48}, 0, 1));
        assertEquals(null, AsciiText.decode(new byte[] {0x48, 0x66}, 0, 2));
        assertEquals(null, AsciiText.encode("why?"));
        assertEquals(null, AsciiText.encode("Ж"));
        assertThrows(
                StenoDecodeException.class,
                () -> Steno.decode(new byte[] {0x48, 0x48, 0x48, 0x48, 0x48, 0x48, 0x48, 0}));
    }

    @Test
    void inRunMatchesEveryByte() {
        Random random = new Random(61);
        int[][] runs = {
            {0x07, 0x66},
            {0x07, 0x28},
            {0x20, 0x7F},
            {0x3F, 0x40},
            {0x67, 0xE7},
            {0x79, 0xF9},
            {0x7E, 0xFE},
            {0x80, 0x81},
            {0xC0, 0xF0},
            {0x00, 0x80},
            {0x01, 0x02}
        };

        for (int[] run : runs) {
            long lo = Swar.repeat(run[0]);
            long end = Swar.repeat(run[1]);

            for (int b = 0; b < 256; b++) {
                for (int position = 0; position < 8; position++) {
                    long w = random.nextLong() & ~(0xFFL << (position << 3)) | ((long) b << (position << 3));
                    boolean expected = b >= run[0] && b < run[1];
                    long bit = Swar.inRun(w, lo, end) & (0x80L << (position << 3));
                    if (expected != (bit != 0)) {
                        throw new AssertionError(Integer.toHexString(b) + " in " + Arrays.toString(run));
                    }
                }
            }
        }
    }

    @Test
    void div248IsExact() {
        // Every offset in a layout is at most U+10FFFF: check all of them, and more, exhaustively.
        for (int x = 0; x < 1 << 24; x++) {
            if (Swar.div248(x) != x / 248) {
                throw new AssertionError("div248(" + x + ")");
            }
        }

        // The rest of the int range, in steps that hit every residue modulo 248, and its end.
        for (long x = 1 << 24; x <= Integer.MAX_VALUE; x += 997) {
            if (Swar.div248((int) x) != (int) x / 248) {
                throw new AssertionError("div248(" + x + ")");
            }
        }

        for (int x = Integer.MAX_VALUE - 1000; x > 0 && x <= Integer.MAX_VALUE - 1; x++) {
            assertEquals(x / 248, Swar.div248(x));
        }

        assertEquals(Integer.MAX_VALUE / 248, Swar.div248(Integer.MAX_VALUE));
    }

    /** Compares the fast and the plain paths on one text: one-shot, in pieces that end inside words, and decoding. */
    private static void check(String text) throws StenoDecodeException {
        char[] chars = text.toCharArray();
        byte[] plain = encode(chars, new EncoderCore(true), chars.length);
        byte[] steno = Steno.encode(text);
        assertArrayEquals(plain, steno, text);
        assertArrayEquals(plain, encode(chars, new EncoderCore(), chars.length), text);
        assertArrayEquals(plain, encode(chars, new EncoderCore(), 7), text);
        assertArrayEquals(plain, encode(chars, new EncoderCore(), 3), text);

        char[] decodedPlain = decode(plain, new DecoderCore(true), plain.length);
        assertEquals(text, new String(decodedPlain), text);
        assertEquals(text, Steno.decode(plain));
        assertEquals(text, new String(decode(plain, new DecoderCore(), plain.length)), text);
        assertEquals(text, new String(decode(plain, new DecoderCore(), 9)), text);
        assertEquals(text, new String(decode(plain, new DecoderCore(), 5)), text);

        // Cut-off bytes are rejected the same way on both paths.
        if (plain.length > 1) {
            byte[] cut = Arrays.copyOf(plain, plain.length - 1);
            DecoderCore slow = new DecoderCore(true);
            DecoderCore fast = new DecoderCore();
            char[] out = new char[2 * cut.length + 2];
            assertEquals(
                    slow.decode(cut, 0, cut.length, out, 0, out.length),
                    fast.decode(cut, 0, cut.length, out, 0, out.length));
            assertEquals(slow.srcPos, fast.srcPos);
            assertEquals(slow.dstPos, fast.dstPos);
        }
    }

    /** Encodes in pieces of {@code piece} chars into a destination with exactly enough room. */
    private static byte[] encode(char[] chars, EncoderCore encoder, int piece) {
        byte[] out = new byte[3 * chars.length + 3];
        int dp = 0;

        for (int pos = 0; pos < chars.length; pos += piece) {
            int end = Math.min(chars.length, pos + piece);
            assertEquals(true, encoder.encode(chars, pos, end, out, dp, out.length));
            dp = encoder.dstPos;
        }

        assertEquals(true, encoder.flush(out, dp, out.length));
        return Arrays.copyOf(out, encoder.dstPos);
    }

    /** Decodes in pieces of {@code piece} bytes; the bytes of a code point that a piece ends inside go to the next. */
    private static char[] decode(byte[] bytes, DecoderCore decoder, int piece) {
        char[] out = new char[2 * bytes.length + 2];
        int sp = 0;
        int dp = 0;

        while (sp < bytes.length) {
            int end = Math.min(bytes.length, sp + piece);
            assertEquals(DecoderCore.UNDERFLOW, decoder.decode(bytes, sp, end, out, dp, out.length));

            if (decoder.srcPos == sp && end < bytes.length) {
                end = Math.min(bytes.length, sp + 4);
                assertEquals(DecoderCore.UNDERFLOW, decoder.decode(bytes, sp, end, out, dp, out.length));
            }

            sp = decoder.srcPos;
            dp = decoder.dstPos;
        }

        return Arrays.copyOf(out, dp);
    }
}
