package com.singulink.steno;

import static org.junit.jupiter.api.Assertions.assertArrayEquals;
import static org.junit.jupiter.api.Assertions.assertEquals;
import static org.junit.jupiter.api.Assertions.assertFalse;
import static org.junit.jupiter.api.Assertions.assertNull;
import static org.junit.jupiter.api.Assertions.assertSame;
import static org.junit.jupiter.api.Assertions.assertThrows;
import static org.junit.jupiter.api.Assertions.assertTrue;

import java.io.BufferedReader;
import java.io.BufferedWriter;
import java.io.ByteArrayInputStream;
import java.io.ByteArrayOutputStream;
import java.io.IOException;
import java.io.InputStream;
import java.io.InputStreamReader;
import java.io.OutputStreamWriter;
import java.io.Reader;
import java.io.Writer;
import java.nio.ByteBuffer;
import java.nio.CharBuffer;
import java.nio.charset.Charset;
import java.nio.charset.CharsetDecoder;
import java.nio.charset.CharsetEncoder;
import java.nio.charset.CoderResult;
import java.nio.charset.CodingErrorAction;
import java.nio.charset.MalformedInputException;
import java.nio.file.Files;
import java.nio.file.Path;
import java.util.Arrays;
import java.util.Random;
import org.junit.jupiter.api.Test;
import org.junit.jupiter.api.io.TempDir;

/** The charset's encoder and decoder: one value in pieces, over any split. */
class StreamingTest {
    private static final Charset STENO = StenoCharset.INSTANCE;

    @Test
    void encoderGivesTheSameBytesForAnySplit() {
        RandomText random = new RandomText(10);
        Random r = random.random();

        for (int i = 0; i < 1500; i++) {
            String text = i % 4 == 0 ? random.nextUnits(r.nextInt(30)) : random.next(r.nextInt(120));
            byte[] expected = Steno.encode(text);
            int mode = i % 3;

            assertArrayEquals(
                    expected,
                    encodeInPieces(text, r, mode == 0 ? 1 : 1 + r.nextInt(10), 4 + r.nextInt(12), mode == 1),
                    text);
        }
    }

    @Test
    void encoderSplitsInsideSurrogatePairs() {
        String text = "a😀b\uD800😀\uDC00😀😀\uD83D";
        byte[] expected = Steno.encode(text);

        for (int split = 0; split <= text.length(); split++) {
            CharsetEncoder encoder = STENO.newEncoder();
            ByteBuffer out = ByteBuffer.allocate(64);
            encoder.encode(CharBuffer.wrap(text, 0, split), out, false);
            encoder.encode(CharBuffer.wrap(text, split, text.length()), out, true);
            encoder.flush(out);
            assertArrayEquals(expected, Arrays.copyOf(out.array(), out.position()), "split at " + split);
        }
    }

    @Test
    void decoderGivesTheSameTextForAnySplit() {
        RandomText random = new RandomText(11);
        Random r = random.random();

        for (int i = 0; i < 1500; i++) {
            String text = i % 4 == 0 ? random.nextUnits(r.nextInt(30)) : random.next(r.nextInt(120));
            byte[] bytes = Steno.encode(text);
            int mode = i % 3;

            assertEquals(
                    text,
                    decodeInPieces(bytes, r, mode == 0 ? 1 : 1 + r.nextInt(10), 2 + r.nextInt(12), mode == 1),
                    text);
        }
    }

    @Test
    void decoderReportsAnUnfinishedCodePointAtTheEnd() throws Exception {
        byte[] bytes = Steno.encode("ab日");

        for (int cut = 3; cut < bytes.length; cut++) {
            CharsetDecoder decoder = STENO.newDecoder();
            ByteBuffer in = ByteBuffer.wrap(bytes, 0, cut);
            CharBuffer out = CharBuffer.allocate(16);

            assertTrue(decoder.decode(in, out, false).isUnderflow(), "more bytes may come");
            assertEquals(2, in.position(), "the unfinished code point stays in the input");

            CoderResult result = decoder.decode(in, out, true);
            assertTrue(result.isMalformed());
            assertEquals(cut - 2, result.length());
            assertNull(((StenoCharsetDecoder) decoder).failure(), "an unfinished code point does not stop the decoder");
            assertEquals(
                    StenoDecodeException.Reason.TRUNCATED,
                    assertThrows(StenoDecodeException.class, () -> Steno.decode(Arrays.copyOf(bytes, in.limit())))
                            .getReason());
        }

        assertEquals("ab�", new String(bytes, 0, 3, STENO));
        assertThrows(MalformedInputException.class, () -> STENO.newDecoder().decode(ByteBuffer.wrap(bytes, 0, 3)));
    }

    @Test
    void decoderStopsAtInvalidData() throws Exception {
        // 61, then a zero byte, then valid bytes again.
        byte[] bytes = {0x48, 0x00, 0x48, 0x49};
        CharsetDecoder decoder = STENO.newDecoder();
        CharBuffer out = CharBuffer.allocate(16);
        ByteBuffer in = ByteBuffer.wrap(bytes);

        CoderResult result = decoder.decode(in, out, false);
        assertTrue(result.isMalformed());
        assertEquals(3, result.length(), "the report covers the rest of the input");
        assertEquals(1, in.position());
        assertSame(StenoDecodeException.Reason.ZERO_BYTE, ((StenoCharsetDecoder) decoder).failure());

        // After the error the decoder takes and drops every later byte, without another report, until a reset.
        ByteBuffer later = ByteBuffer.wrap(new byte[] {0x48, 0x49});
        assertTrue(decoder.decode(later, out, false).isUnderflow());
        assertEquals(2, later.position());
        assertEquals(1, out.position(), "nothing more than the \"a\" before the error");
        assertTrue(decoder.decode(ByteBuffer.wrap(new byte[0]), out, true).isUnderflow());
        decoder.reset();
        out.clear();
        assertTrue(decoder.decode(ByteBuffer.wrap(new byte[] {0x48}), out, true).isUnderflow());
        assertEquals("a", out.flip().toString());

        assertEquals("a�", new String(bytes, STENO));
        assertEquals("a�", STENO.decode(ByteBuffer.wrap(bytes)).toString());
        assertEquals(
                "a",
                STENO.newDecoder()
                        .onMalformedInput(CodingErrorAction.IGNORE)
                        .decode(ByteBuffer.wrap(bytes))
                        .toString());
    }

    @Test
    void replacementIsWrittenWhenTheErrorMeetsAFullOutput() {
        // With REPLACE, an error found when the output has no room for U+FFFD is held back until there is room, so the
        // replacement is not lost when the decoder stops.
        byte[] bytes = {0x48, 0x49, 0x00, 0x48};
        CharsetDecoder decoder = STENO.newDecoder().onMalformedInput(CodingErrorAction.REPLACE);
        ByteBuffer in = ByteBuffer.wrap(bytes);
        StringBuilder text = new StringBuilder();
        CharBuffer out = CharBuffer.allocate(2);

        while (true) {
            CoderResult result = decoder.decode(in, out, true);
            out.flip();
            text.append(out);
            out.clear();

            if (result.isUnderflow()) {
                break;
            }

            assertTrue(result.isOverflow());
        }

        assertTrue(decoder.flush(out).isUnderflow());
        assertEquals("ab�", text.toString());
    }

    @Test
    void decoderReportsTheSameReasonAsDecode() {
        for (VectorFile.Vector v : VectorFile.read(VectorFile.PUBLISHED)) {
            if (v.kind != VectorFile.Kind.INVALID) {
                continue;
            }

            CharsetDecoder decoder = STENO.newDecoder();
            CoderResult result =
                    decoder.decode(ByteBuffer.wrap(v.bytes), CharBuffer.allocate(2 * v.bytes.length + 2), true);
            assertTrue(result.isMalformed(), v.toString());

            StenoDecodeException.Reason reason = ((StenoCharsetDecoder) decoder).failure();
            assertEquals(v.reason == StenoDecodeException.Reason.TRUNCATED ? null : v.reason, reason, v.toString());
        }
    }

    @Test
    void resetStartsANewValueAndFlushEndsOne() throws Exception {
        CharsetEncoder encoder = STENO.newEncoder();
        assertArrayEquals(Steno.encode("Москва"), toArray(encoder.encode(CharBuffer.wrap("Москва"))));
        assertArrayEquals(
                Steno.encode("Ζ"), toArray(encoder.encode(CharBuffer.wrap("Ζ"))), "encode(CharBuffer) resets first");

        ByteBuffer out = ByteBuffer.allocate(32);
        encoder.reset();
        encoder.encode(CharBuffer.wrap("ЖЖ\uD83D"), out, true);
        encoder.flush(out);
        assertArrayEquals(
                Steno.encode("ЖЖ\uD83D"),
                Arrays.copyOf(out.array(), out.position()),
                "flush writes a held high surrogate");
        assertThrows(
                IllegalStateException.class,
                () -> encoder.encode(CharBuffer.wrap("x"), ByteBuffer.allocate(8), false),
                "a flushed encoder must be reset");

        encoder.reset();
        out.clear();
        encoder.encode(CharBuffer.wrap("Ж"), out, false);
        encoder.reset();
        out.clear();
        encoder.encode(CharBuffer.wrap("Ж"), out, true);
        encoder.flush(out);
        assertArrayEquals(Steno.encode("Ж"), Arrays.copyOf(out.array(), out.position()));

        CharsetDecoder decoder = STENO.newDecoder();
        byte[] bytes = Steno.encode("Москва");
        assertEquals("Москва", decoder.decode(ByteBuffer.wrap(bytes)).toString());
        assertEquals("Москва", decoder.decode(ByteBuffer.wrap(bytes)).toString(), "decode(ByteBuffer) resets first");
    }

    @Test
    void encoderCanEncodeEveryChar() {
        CharsetEncoder encoder = STENO.newEncoder();
        assertTrue(encoder.canEncode('\uD800'));
        assertTrue(encoder.canEncode("\uDC00\uD800"));
        assertEquals(3.0f, encoder.maxBytesPerChar());
        assertEquals(2.0f, STENO.newDecoder().maxCharsPerByte());
    }

    @Test
    void stringApis() {
        String text = "Zürich 東京 😀 \uDC00 end\uD83D";
        byte[] bytes = text.getBytes(STENO);
        assertArrayEquals(Steno.encode(text), bytes);
        assertEquals(text, new String(bytes, STENO));
        assertArrayEquals(bytes, toArray(STENO.encode(text)));
    }

    @Test
    void charsetLookup() {
        assertSame(STENO, Charset.forName("x-steno"));
        assertSame(STENO, Charset.forName("X-Steno"));
        assertTrue(Charset.isSupported("x-steno"));
        assertTrue(Charset.availableCharsets().containsKey("x-steno"));
        assertEquals("x-steno", STENO.name());
        assertTrue(STENO.canEncode());
        assertTrue(STENO.contains(java.nio.charset.StandardCharsets.UTF_8));
    }

    @Test
    void writerAndReader(@TempDir Path dir) throws IOException {
        RandomText random = new RandomText(12);
        Random r = random.random();

        for (int i = 0; i < 200; i++) {
            String text = random.next(r.nextInt(300)) + (i % 5 == 0 ? "\uD800" : "");

            ByteArrayOutputStream stream = new ByteArrayOutputStream();

            try (Writer writer = new OutputStreamWriter(stream, STENO)) {
                for (int pos = 0; pos < text.length(); ) {
                    int n = Math.min(text.length() - pos, 1 + r.nextInt(7));
                    writer.write(text, pos, n);
                    pos += n;

                    if (r.nextInt(4) == 0) {
                        writer.flush();
                    }
                }
            }

            byte[] bytes = stream.toByteArray();
            assertArrayEquals(Steno.encode(text), bytes, text);

            StringBuilder read = new StringBuilder();

            try (Reader reader = new InputStreamReader(new TrickleStream(bytes, r), STENO)) {
                char[] buffer = new char[1 + r.nextInt(9)];
                int n;

                while ((n = reader.read(buffer)) >= 0) {
                    read.append(buffer, 0, n);
                }
            }

            assertEquals(text, read.toString());
        }

        Path file = dir.resolve("value.txt");
        String text = "Line 1: Ελληνικά\nLine 2: 日本語のテキスト\nLine 3: 😀\n";

        try (BufferedWriter writer = Files.newBufferedWriter(file, STENO)) {
            writer.write(text);
        }

        assertArrayEquals(Steno.encode(text), Files.readAllBytes(file));
        assertEquals(text, Files.readString(file, STENO));

        try (BufferedReader reader = Files.newBufferedReader(file, STENO)) {
            assertEquals("Line 1: Ελληνικά", reader.readLine());
            assertEquals("Line 2: 日本語のテキスト", reader.readLine());
        }
    }

    @Test
    void readerReplacesInvalidData() throws IOException {
        byte[] bytes = Arrays.copyOf(Steno.encode("abc"), 5);
        bytes[3] = (byte) 0xFF;
        bytes[4] = 0x48;

        try (Reader reader = new InputStreamReader(new ByteArrayInputStream(bytes), STENO)) {
            char[] buffer = new char[16];
            int n = reader.read(buffer);
            assertEquals("abc�", new String(buffer, 0, n));
        }

        assertEquals(
                StenoDecodeException.Reason.UNUSED_LEAD,
                assertThrows(StenoDecodeException.class, () -> Steno.decode(bytes))
                        .getReason());
    }

    // ---------- Helpers ----------

    /**
     * Encodes text in pieces of up to {@code maxPiece} chars into an output buffer of {@code outSize} bytes, drained
     * after each call.
     */
    static byte[] encodeInPieces(String text, Random r, int maxPiece, int outSize, boolean noArrays) {
        CharsetEncoder encoder = STENO.newEncoder();
        ByteArrayOutputStream result = new ByteArrayOutputStream();
        ByteBuffer out = noArrays ? ByteBuffer.allocateDirect(outSize) : ByteBuffer.allocate(outSize);
        char[] chars = text.toCharArray();
        int pos = 0;

        while (true) {
            int n = Math.min(chars.length - pos, 1 + r.nextInt(maxPiece));
            boolean last = pos + n == chars.length;
            CharBuffer in = noArrays ? CharBuffer.wrap(text, pos, pos + n) : CharBuffer.wrap(chars, pos, n);

            while (true) {
                CoderResult cr = encoder.encode(in, out, last);
                drain(out, result);

                if (cr.isUnderflow()) {
                    break;
                }

                assertTrue(cr.isOverflow());
            }

            assertFalse(in.hasRemaining(), "the encoder takes every char");
            pos += n;

            if (last) {
                break;
            }
        }

        while (encoder.flush(out).isOverflow()) {
            drain(out, result);
        }

        drain(out, result);
        return result.toByteArray();
    }

    /**
     * Decodes bytes in pieces of up to {@code maxPiece} bytes into an output buffer of {@code outSize} chars, as a
     * reader does.
     */
    static String decodeInPieces(byte[] bytes, Random r, int maxPiece, int outSize, boolean noArrays) {
        CharsetDecoder decoder = STENO.newDecoder();
        StringBuilder result = new StringBuilder();
        CharBuffer out =
                noArrays ? ByteBuffer.allocateDirect(2 * outSize).asCharBuffer() : CharBuffer.allocate(outSize);
        ByteBuffer in = noArrays ? ByteBuffer.allocateDirect(bytes.length + 8) : ByteBuffer.allocate(bytes.length + 8);
        in.flip();
        int pos = 0;

        while (true) {
            int n = Math.min(bytes.length - pos, 1 + r.nextInt(maxPiece));
            boolean last = pos + n == bytes.length;

            // Keep the bytes that the decoder left, and add the next piece.
            in.compact();
            in.put(bytes, pos, n);
            in.flip();

            while (true) {
                CoderResult cr = decoder.decode(in, out, last);
                drain(out, result);

                if (cr.isUnderflow()) {
                    break;
                }

                assertTrue(cr.isOverflow(), () -> "unexpected " + cr);
            }

            pos += n;

            if (last) {
                break;
            }
        }

        assertFalse(in.hasRemaining());
        assertTrue(decoder.flush(out).isUnderflow());
        drain(out, result);
        return result.toString();
    }

    private static void drain(ByteBuffer out, ByteArrayOutputStream result) {
        out.flip();

        while (out.hasRemaining()) {
            result.write(out.get());
        }

        out.clear();
    }

    private static void drain(CharBuffer out, StringBuilder result) {
        out.flip();
        result.append(out);
        out.clear();
    }

    private static byte[] toArray(ByteBuffer buffer) {
        byte[] bytes = new byte[buffer.remaining()];
        buffer.get(bytes);
        return bytes;
    }

    /** An input stream that returns a few bytes at a time. */
    private static final class TrickleStream extends InputStream {
        private final byte[] bytes;
        private final Random random;
        private int pos;

        TrickleStream(byte[] bytes, Random random) {
            this.bytes = bytes;
            this.random = random;
        }

        @Override
        public int read() {
            return pos < bytes.length ? bytes[pos++] & 0xFF : -1;
        }

        @Override
        public int read(byte[] b, int off, int len) {
            if (len == 0) {
                return 0;
            }

            if (pos == bytes.length) {
                return -1;
            }

            int n = Math.min(Math.min(len, bytes.length - pos), 1 + random.nextInt(5));
            System.arraycopy(bytes, pos, b, off, n);
            pos += n;
            return n;
        }
    }
}
