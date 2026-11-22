package com.singulink.steno;

import static org.junit.jupiter.api.Assertions.assertArrayEquals;
import static org.junit.jupiter.api.Assertions.assertEquals;
import static org.junit.jupiter.api.Assertions.assertFalse;
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
import java.io.PrintStream;
import java.io.PrintWriter;
import java.io.Reader;
import java.io.Writer;
import java.nio.ByteBuffer;
import java.nio.CharBuffer;
import java.nio.channels.Channels;
import java.nio.charset.CharacterCodingException;
import java.nio.charset.Charset;
import java.nio.charset.CharsetDecoder;
import java.nio.charset.CharsetEncoder;
import java.nio.charset.CoderResult;
import java.nio.charset.CodingErrorAction;
import java.nio.charset.MalformedInputException;
import java.nio.file.Files;
import java.nio.file.Path;
import java.util.Arrays;
import java.util.List;
import java.util.Scanner;
import java.util.SplittableRandom;
import org.junit.jupiter.api.Test;
import org.junit.jupiter.api.io.TempDir;

/** Tests of StenoCharset, its encoder and decoder, and the JDK APIs that use them. */
class CharsetContractTest {
    private static final Charset STENO = StenoCharset.INSTANCE;

    @TempDir
    Path dir;

    // ---------- Buffer factories ----------

    /** Chars in a buffer of one of several kinds: heap at array offset, read-only, wrapping a String, direct-like. */
    static CharBuffer charBuffer(char[] chars, int from, int to, int kind) {
        switch (kind) {
            case 0:
                return CharBuffer.wrap(chars, from, to - from);
            case 1: {
                // Heap with nonzero array offset and nonzero position.
                char[] big = new char[to - from + 10];
                System.arraycopy(chars, from, big, 7, to - from);
                CharBuffer b = CharBuffer.wrap(big, 4, to - from + 3).slice();
                b.position(3);
                return b;
            }
            case 2:
                return CharBuffer.wrap(chars, from, to - from).asReadOnlyBuffer();
            default:
                return CharBuffer.wrap(new String(chars, from, to - from));
        }
    }

    static ByteBuffer byteOut(int size, int kind) {
        switch (kind) {
            case 0:
                return ByteBuffer.allocate(size);
            case 1:
                return ByteBuffer.wrap(new byte[size + 9], 5, size).slice();
            default:
                return ByteBuffer.allocateDirect(size);
        }
    }

    static ByteBuffer byteIn(byte[] bytes, int from, int to, int kind) {
        switch (kind) {
            case 0:
                return ByteBuffer.wrap(bytes, from, to - from);
            case 1: {
                byte[] big = new byte[to - from + 10];
                System.arraycopy(bytes, from, big, 7, to - from);
                ByteBuffer b = ByteBuffer.wrap(big, 4, to - from + 3).slice();
                b.position(3);
                return b;
            }
            case 2:
                return ByteBuffer.wrap(bytes, from, to - from).asReadOnlyBuffer();
            default: {
                ByteBuffer d = ByteBuffer.allocateDirect(to - from);
                d.put(bytes, from, to - from).flip();
                return d;
            }
        }
    }

    static CharBuffer charOut(int size, int kind) {
        switch (kind) {
            case 0:
                return CharBuffer.allocate(size);
            case 1:
                return CharBuffer.wrap(new char[size + 9], 5, size).slice();
            default:
                return ByteBuffer.allocateDirect(2 * size).asCharBuffer();
        }
    }

    // ---------- Encoder ----------

    /** Encodes in random pieces, with random kinds and sizes of buffers, draining the output after each call. */
    static byte[] streamEncode(CharsetEncoder enc, String text, SplittableRandom r) {
        enc.reset();
        char[] chars = text.toCharArray();
        ByteArrayOutputStream result = new ByteArrayOutputStream();
        int outSize = 4 + r.nextInt(8);
        int outKind = r.nextInt(3);
        ByteBuffer out = byteOut(outSize, outKind);
        int pos = 0;
        while (true) {
            int n = Math.min(chars.length - pos, r.nextInt(6) == 0 ? 0 : 1 + r.nextInt(r.nextBoolean() ? 3 : 700));
            boolean last = pos + n == chars.length && r.nextBoolean();
            CharBuffer in = charBuffer(chars, pos, pos + n, r.nextInt(4));
            int inStart = in.position();
            while (true) {
                CoderResult cr = enc.encode(in, out, last);
                out.flip();
                byte[] b = new byte[out.remaining()];
                out.get(b);
                result.write(b, 0, b.length);
                out.clear();
                if (cr.isUnderflow()) break;
                assertTrue(cr.isOverflow(), cr.toString());
            }
            assertFalse(in.hasRemaining());
            pos += in.position() - inStart;
            if (pos == chars.length && (last || r.nextBoolean())) {
                if (!last) {
                    CoderResult cr = enc.encode(CharBuffer.allocate(0), out, true);
                    assertTrue(cr.isUnderflow());
                }
                break;
            }
        }
        while (true) {
            CoderResult cr = enc.flush(out);
            out.flip();
            byte[] b = new byte[out.remaining()];
            out.get(b);
            result.write(b, 0, b.length);
            out.clear();
            if (cr.isUnderflow()) break;
        }
        return result.toByteArray();
    }

    @Test
    void encoderAnySplitAnyBuffer() {
        long seed = OracleOneShotTest.SEED + 10;
        ValueGenerator g = new ValueGenerator(seed);
        CharsetEncoder enc = STENO.newEncoder();
        long start = System.nanoTime();
        int count = 0;
        while (OracleOneShotTest.timeLeft(start)) {
            String text =
                    count % 3 == 0 ? g.units(60) : ValueGenerator.utf16(count % 3 == 1 ? g.value(200) : g.runs(1500));
            byte[] want = OracleCodec.encode(text);
            assertArrayEquals(want, streamEncode(enc, text, g.r), text);
            // The convenience method, twice: each call is one value.
            try {
                ByteBuffer bb = enc.encode(CharBuffer.wrap(text));
                byte[] got = new byte[bb.remaining()];
                bb.get(got);
                assertArrayEquals(want, got);
                bb = STENO.encode(text);
                got = new byte[bb.remaining()];
                bb.get(got);
                assertArrayEquals(want, got);
            } catch (CharacterCodingException e) {
                throw new AssertionError(e);
            }
            count++;
        }
        System.out.println("[fuzz] charset encoder splits: " + count + " values, seed " + seed);
    }

    @Test
    void encoderResetInEveryState() {
        ValueGenerator g = new ValueGenerator(31);
        CharsetEncoder enc = STENO.newEncoder();
        for (int i = 0; i < 2000; i++) {
            String a = ValueGenerator.utf16(g.value(30)) + (i % 2 == 0 ? "\uD83D" : "");
            ByteBuffer out = ByteBuffer.allocate(200);
            enc.reset();
            enc.encode(CharBuffer.wrap(a), out, false);
            String b = ValueGenerator.utf16(g.value(30));
            assertArrayEquals(OracleCodec.encode(b), streamEncode(enc, b, g.r));
        }
        // Illegal orders throw IllegalStateException; a flush on a fresh encoder too (JDK contract).
        CharsetEncoder e = STENO.newEncoder();
        ByteBuffer out = ByteBuffer.allocate(100);
        e.encode(CharBuffer.wrap("ab\uD800"), out, true);
        assertEquals(CoderResult.UNDERFLOW, e.flush(out));
        assertThrows(IllegalStateException.class, () -> e.encode(CharBuffer.wrap("x"), out, false));
        assertEquals(CoderResult.UNDERFLOW, e.flush(out));
        out.flip();
        byte[] got = new byte[out.remaining()];
        out.get(got);
        assertArrayEquals(OracleCodec.encode("ab\uD800"), got);
    }

    @Test
    void encoderFlushIntoTinyBuffers() {
        for (int room = 0; room <= 3; room++) {
            CharsetEncoder e = STENO.newEncoder();
            ByteBuffer big = ByteBuffer.allocate(100);
            String text = "一\uD800"; // ends in a held high surrogate after CJK
            e.encode(CharBuffer.wrap(text), big, true);
            ByteBuffer small = ByteBuffer.allocate(room);
            CoderResult cr = e.flush(small);
            byte[] want = OracleCodec.encode(text);
            int tail = want.length - big.position();
            if (room < tail) {
                assertTrue(cr.isOverflow());
                assertEquals(0, small.position());
                ByteBuffer more = ByteBuffer.allocate(4);
                assertTrue(e.flush(more).isUnderflow());
                assertEquals(tail, more.position());
            } else {
                assertTrue(cr.isUnderflow());
                assertEquals(tail, small.position());
            }
        }
    }

    @Test
    void maxBytesPerCharHoldsForOneCall() {
        // The JDK's String.getBytes sizes its array with maxBytesPerChar and throws if one call overflows it.
        String[] worst = {"ༀ", "ကခሀኀ", "\uD800", "\uDBFF\uDBFF\uDBFF", "𠀀\uD800", "￿\uD800", "㄀ꀀ", "a\uDC00"};
        for (String w : worst) {
            for (int rep = 1; rep < 50; rep++) {
                String s = w.repeat(rep);
                assertArrayEquals(OracleCodec.encode(s), s.getBytes(STENO), s);
            }
        }
        assertTrue(STENO.newEncoder().maxBytesPerChar() >= 3.0f);
        assertTrue(STENO.newDecoder().maxCharsPerByte() >= 2.0f);
    }

    // ---------- Decoder ----------

    /** Decodes in random pieces, keeping unread bytes for the next piece, as a JDK reader does. */
    static String streamDecode(CharsetDecoder dec, byte[] bytes, SplittableRandom r, CodingErrorAction action)
            throws CharacterCodingException {
        dec.reset();
        dec.onMalformedInput(action);
        StringBuilder result = new StringBuilder();
        int outSize = 2 + r.nextInt(8);
        CharBuffer out = charOut(outSize, r.nextInt(3));
        int inKind = r.nextInt(4);
        byte[] pending = new byte[0];
        int pos = 0;
        while (true) {
            int n = Math.min(bytes.length - pos, r.nextInt(5) == 0 ? 0 : 1 + r.nextInt(r.nextBoolean() ? 3 : 700));
            byte[] piece = new byte[pending.length + n];
            System.arraycopy(pending, 0, piece, 0, pending.length);
            System.arraycopy(bytes, pos, piece, pending.length, n);
            pos += n;
            boolean last = pos == bytes.length;
            ByteBuffer in = byteIn(piece, 0, piece.length, inKind);
            int inStart = in.position();
            while (true) {
                CoderResult cr = dec.decode(in, out, last);
                out.flip();
                result.append(out);
                out.clear();
                if (cr.isUnderflow()) break;
                if (cr.isError()) cr.throwException();
                assertTrue(cr.isOverflow());
            }
            int used = in.position() - inStart;
            pending = Arrays.copyOfRange(piece, used, piece.length);
            if (last) {
                assertEquals(0, pending.length, "the decoder takes every byte at the end of input");
                break;
            }
        }
        while (true) {
            CoderResult cr = dec.flush(out);
            out.flip();
            result.append(out);
            out.clear();
            if (cr.isUnderflow()) break;
        }
        return result.toString();
    }

    @Test
    void decoderAnySplitAnyBuffer() throws CharacterCodingException {
        long seed = OracleOneShotTest.SEED + 11;
        ValueGenerator g = new ValueGenerator(seed);
        CharsetDecoder dec = STENO.newDecoder();
        long start = System.nanoTime();
        int count = 0;
        while (OracleOneShotTest.timeLeft(start)) {
            String text =
                    count % 3 == 0 ? g.units(60) : ValueGenerator.utf16(count % 3 == 1 ? g.value(200) : g.runs(1500));
            byte[] bytes = OracleCodec.encode(text);
            assertEquals(text, streamDecode(dec, bytes, g.r, CodingErrorAction.REPORT));
            assertEquals(text, dec.decode(ByteBuffer.wrap(bytes)).toString());
            assertEquals(text, STENO.decode(ByteBuffer.wrap(bytes)).toString());
            count++;
        }
        System.out.println("[fuzz] charset decoder splits: " + count + " values, seed " + seed);
    }

    @Test
    void decoderOnInvalidInputWithEveryAction() throws CharacterCodingException {
        long seed = OracleOneShotTest.SEED + 12;
        ValueGenerator g = new ValueGenerator(seed);
        CharsetDecoder dec = STENO.newDecoder();
        long start = System.nanoTime();
        int count = 0;
        while (OracleOneShotTest.timeLeft(start)) {
            byte[] bytes = OracleCodec.encode(g.value(40));
            if (bytes.length == 0) continue;
            int at = g.r.nextInt(bytes.length + 1);
            if (at == bytes.length || g.r.nextBoolean()) {
                bytes = Arrays.copyOf(bytes, Math.max(0, at));
            } else {
                bytes[at] = (byte) g.r.nextInt(256);
            }
            OracleCodec.Result want = OracleCodec.decode(bytes);
            String prefix = want.ok() ? want.text() : prefixText(bytes, want.offset);
            // REPORT: throws if and only if invalid.
            try {
                String got = streamDecode(dec, bytes, g.r, CodingErrorAction.REPORT);
                assertTrue(want.ok(), "accepted " + VectorFile.hex(bytes));
                assertEquals(want.text(), got);
            } catch (MalformedInputException e) {
                assertFalse(want.ok(), "rejected " + VectorFile.hex(bytes));
            }
            // IGNORE: the text before the error.
            assertEquals(prefix, streamDecode(dec, bytes, g.r, CodingErrorAction.IGNORE), VectorFile.hex(bytes));
            // REPLACE in one buffer: the text before the error and one U+FFFD.
            String replaced = new String(bytes, STENO);
            assertEquals(want.ok() ? prefix : prefix + "�", replaced, VectorFile.hex(bytes));
            // And through a reader.
            try (Reader reader = new InputStreamReader(new ByteArrayInputStream(bytes), STENO)) {
                StringBuilder sb = new StringBuilder();
                char[] buf = new char[1 + g.r.nextInt(20)];
                int n;
                while ((n = reader.read(buf)) >= 0) sb.append(buf, 0, n);
                assertEquals(want.ok() ? prefix : prefix + "�", sb.toString(), VectorFile.hex(bytes));
            } catch (IOException e) {
                throw new AssertionError(e);
            }
            count++;
        }
        System.out.println("[fuzz] decoder invalid input with REPORT/IGNORE/REPLACE: " + count + ", seed " + seed);
    }

    /** The text of the code points before byte offset {@code end} (the valid prefix). */
    static String prefixText(byte[] bytes, int end) {
        OracleCodec.Result r = OracleCodec.decode(Arrays.copyOf(bytes, end));
        assertTrue(r.ok());
        return r.text();
    }

    @Test
    void decoderResetAfterError() throws CharacterCodingException {
        CharsetDecoder dec = STENO.newDecoder();
        CharBuffer out = CharBuffer.allocate(50);
        CoderResult cr = dec.decode(ByteBuffer.wrap(new byte[] {0x48, 0x00, 0x48}), out, false);
        assertTrue(cr.isMalformed());
        dec.reset();
        assertEquals("ab", dec.decode(ByteBuffer.wrap(Steno.encode("ab"))).toString());
    }

    // ---------- Writers and readers ----------

    /** An output stream that records bytes; writes of any size. */
    @Test
    void writersWithFlushesEverywhere() throws IOException {
        ValueGenerator g = new ValueGenerator(OracleOneShotTest.SEED + 13);
        for (int i = 0; i < 300; i++) {
            String text = i % 2 == 0 ? g.units(80) : ValueGenerator.utf16(g.value(300));
            byte[] want = OracleCodec.encode(text);

            ByteArrayOutputStream bos = new ByteArrayOutputStream();
            try (Writer w = new OutputStreamWriter(bos, STENO)) {
                int pos = 0;
                while (pos < text.length()) {
                    int n = Math.min(text.length() - pos, 1 + g.r.nextInt(5));
                    int how = g.r.nextInt(4);
                    if (how == 0) w.write(text, pos, n);
                    else if (how == 1) w.write(text.toCharArray(), pos, n);
                    else if (how == 2) w.append(new StringBuilder(text), pos, pos + n);
                    else for (int k = 0; k < n; k++) w.write(text.charAt(pos + k));
                    pos += n;
                    w.flush();
                }
            }
            assertArrayEquals(want, bos.toByteArray(), text);

            bos = new ByteArrayOutputStream();
            try (PrintWriter pw = new PrintWriter(new OutputStreamWriter(bos, STENO), true)) {
                for (char c : text.toCharArray()) {
                    pw.print(c);
                    pw.flush();
                }
            }
            assertArrayEquals(want, bos.toByteArray(), text);

            bos = new ByteArrayOutputStream();
            try (PrintStream ps = new PrintStream(bos, true, STENO)) {
                for (int k = 0; k < text.length(); k += 3) {
                    ps.print(text.substring(k, Math.min(text.length(), k + 3)));
                    ps.flush();
                }
            }
            assertArrayEquals(want, bos.toByteArray(), text);
        }
    }

    /** An input stream that gives at most {@code step} bytes per read, and lies about available() if asked. */
    static final class Trickle extends InputStream {
        final byte[] b;
        final int step;
        final boolean claimAvailable;
        int pos;

        Trickle(byte[] b, int step, boolean claimAvailable) {
            this.b = b;
            this.step = step;
            this.claimAvailable = claimAvailable;
        }

        @Override
        public int read() {
            return pos < b.length ? b[pos++] & 0xFF : -1;
        }

        @Override
        public int read(byte[] dst, int off, int len) {
            if (len == 0) return 0;
            if (pos >= b.length) return -1;
            int n = Math.min(Math.min(len, step), b.length - pos);
            System.arraycopy(b, pos, dst, off, n);
            pos += n;
            return n;
        }

        @Override
        public int available() {
            return claimAvailable ? 1 : Math.min(step, b.length - pos);
        }
    }

    @Test
    void readersWithEveryReadPattern() throws IOException {
        ValueGenerator g = new ValueGenerator(OracleOneShotTest.SEED + 14);
        for (int i = 0; i < 400; i++) {
            String text = i % 2 == 0 ? g.units(80) : ValueGenerator.utf16(g.value(400));
            byte[] bytes = OracleCodec.encode(text);
            int step = 1 + g.r.nextInt(i % 3 == 0 ? 3 : 9000);
            boolean lie = g.r.nextBoolean();
            try (Reader reader = new InputStreamReader(new Trickle(bytes, step, lie), STENO)) {
                StringBuilder sb = new StringBuilder();
                int mode = g.r.nextInt(3);
                if (mode == 0) {
                    int c;
                    while ((c = reader.read()) >= 0) sb.append((char) c);
                } else {
                    char[] buf = new char[1 + g.r.nextInt(mode == 1 ? 2 : 50)];
                    int n;
                    while ((n = reader.read(buf, 0, 1 + g.r.nextInt(buf.length))) >= 0) sb.append(buf, 0, n);
                }
                assertEquals(text, sb.toString(), "step " + step + " lie " + lie + " mode " + mode);
            }
        }
    }

    @Test
    void truncatedStreamIsReportedByReaders() throws IOException {
        // "Ми" then a lone lead byte 67: in the Cyrillic layout 67 starts a 2-byte code point (truncated); in the
        // initial layout 67 is U+00C0. A reader that lost the state would give "МиÀ".
        byte[] bytes = {(byte) 0xE9, (byte) 0xF4, (byte) 0xA2, 0x67};
        assertEquals(
                StenoDecodeException.Reason.TRUNCATED,
                assertThrows(StenoDecodeException.class, () -> Steno.decode(bytes))
                        .getReason());
        for (int step = 1; step <= 4; step++) {
            for (boolean lie : new boolean[] {false, true}) {
                try (Reader reader = new InputStreamReader(new Trickle(bytes, step, lie), STENO)) {
                    StringBuilder sb = new StringBuilder();
                    int c;
                    while ((c = reader.read()) >= 0) sb.append((char) c);
                    assertEquals("Ми�", sb.toString(), "step " + step + " lie " + lie);
                }
                try (Reader reader = new InputStreamReader(new Trickle(bytes, step, lie), STENO.newDecoder())) {
                    StringBuilder sb = new StringBuilder();
                    assertThrows(MalformedInputException.class, () -> {
                        int c;
                        while ((c = reader.read()) >= 0) sb.append((char) c);
                    });
                }
            }
        }
        Path file = dir.resolve("t.txt");
        Files.write(file, bytes);
        assertThrows(MalformedInputException.class, () -> Files.readAllLines(file, STENO));
        assertThrows(CharacterCodingException.class, () -> Files.readString(file, STENO));
        try (BufferedReader r = Files.newBufferedReader(file, STENO)) {
            assertThrows(MalformedInputException.class, r::readLine);
        }
    }

    /**
     * With REPLACE, a reader gives the text before the error and one U+FFFD, however the stream delivers the bytes:
     * after invalid data the decoder drops every later byte until it is reset.
     */
    @Test
    void replacementDoesNotDependOnReadSizes() throws IOException {
        byte[] bytes = {0x48, 0x48, 0x00, 0x48, 0x48, 0x48};
        assertEquals("aa�", new String(bytes, STENO));
        for (int step = 1; step <= 6; step++) {
            try (Reader reader = new InputStreamReader(new Trickle(bytes, step, false), STENO)) {
                StringBuilder sb = new StringBuilder();
                int c;
                while ((c = reader.read()) >= 0) sb.append((char) c);
                assertEquals("aa�", sb.toString(), "stream that gives " + step + " bytes per read");
            }
        }
    }

    private static int lineCount(String text) {
        return (int) text.chars().filter(c -> c == '\n').count();
    }

    @Test
    void filesScannerAndChannels() throws IOException {
        ValueGenerator g = new ValueGenerator(OracleOneShotTest.SEED + 15);
        for (int i = 0; i < 100; i++) {
            StringBuilder sb = new StringBuilder();
            int lines = 1 + g.r.nextInt(30);
            for (int k = 0; k < lines; k++) {
                sb.append(ValueGenerator.utf16(g.value(200))
                                .replace('\n', ' ')
                                .replace('\r', ' ')
                                .replace('\u0085', ' ')
                                .replace(' ', ' ')
                                .replace(' ', ' '))
                        .append('\n');
            }
            String text = sb.toString();
            byte[] want = OracleCodec.encode(text);
            Path file = dir.resolve("f" + i + ".txt");
            try (BufferedWriter w = Files.newBufferedWriter(file, STENO)) {
                for (String line : text.split("\n", -1)) {
                    w.write(line);
                    w.flush();
                    w.newLine();
                }
            }
            // newLine writes the platform separator; compare with what was written.
            String written = Files.readString(file, STENO);
            assertArrayEquals(OracleCodec.encode(written), Files.readAllBytes(file));
            Files.writeString(file, text, STENO);
            assertArrayEquals(want, Files.readAllBytes(file));
            assertEquals(text, Files.readString(file, STENO));
            // The text ends with a line feed: its lines are the pieces between line feeds, empty ones included (split
            // without a limit would drop empty lines at the end).
            List<String> expected = Arrays.asList(text.split("\n", -1)).subList(0, lineCount(text));
            assertEquals(expected, Files.readAllLines(file, STENO));
            try (Scanner s = new Scanner(file, STENO)) {
                for (String line : expected) assertEquals(line, s.nextLine());
            }
            // Scanner's own token rules over a trickling stream: the same tokens as with UTF-8.
            try (Scanner s = new Scanner(new ByteArrayInputStream(want), STENO);
                    Scanner u = new Scanner(new java.io.StringReader(text))) {
                s.useDelimiter("\n");
                u.useDelimiter("\n");
                while (u.hasNext()) assertEquals(u.next(), s.next());
                assertFalse(s.hasNext());
            }
            try (Reader r = Channels.newReader(Files.newByteChannel(file), STENO)) {
                StringBuilder got = new StringBuilder();
                char[] buf = new char[7];
                int n;
                while ((n = r.read(buf)) >= 0) got.append(buf, 0, n);
                assertEquals(text, got.toString());
            }
            try (Writer w = Channels.newWriter(
                    Files.newByteChannel(
                            file,
                            java.nio.file.StandardOpenOption.WRITE,
                            java.nio.file.StandardOpenOption.TRUNCATE_EXISTING),
                    STENO)) {
                for (int k = 0; k < text.length(); k += 2) {
                    w.write(text, k, Math.min(2, text.length() - k));
                    w.flush();
                }
            }
            assertArrayEquals(want, Files.readAllBytes(file));
        }
    }
}
