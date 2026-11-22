package com.singulink.steno;

import static org.junit.jupiter.api.Assertions.assertEquals;
import static org.junit.jupiter.api.Assertions.assertFalse;
import static org.junit.jupiter.api.Assertions.assertThrows;
import static org.junit.jupiter.api.Assertions.assertTrue;

import java.io.BufferedReader;
import java.io.IOException;
import java.io.Writer;
import java.nio.charset.Charset;
import java.nio.charset.StandardCharsets;
import java.nio.file.Files;
import java.nio.file.Path;
import java.util.ArrayList;
import java.util.Arrays;
import java.util.List;
import org.junit.jupiter.api.Test;
import org.junit.jupiter.api.io.TempDir;

/** The examples of README.md, as they are written there. Keep the two in step. */
class ReadmeExamplesTest {
    @Test
    void encodeAndDecode() throws StenoDecodeException {
        byte[] bytes = Steno.encode("Москва");
        String text = Steno.decode(bytes);

        assertEquals(7, bytes.length); // UTF-8 takes 12
        assertEquals(12, "Москва".getBytes(StandardCharsets.UTF_8).length);
        assertEquals("Москва", text);
    }

    @Test
    void order() {
        byte[] zug = Steno.encode("Zug");
        byte[] zurich = Steno.encode("Zürich");

        assertTrue(Arrays.compareUnsigned(zug, zurich) < 0); // the bytes of "Zug" sort first
        assertTrue(Steno.compareCodePoints("Zug", "Zürich") < 0); // the same order for strings in memory

        List<String> names = new ArrayList<>(List.of("すごい😀", "すごい！"));
        names.sort(Steno.CODE_POINT_ORDER);
        assertEquals(List.of("すごい！", "すごい😀"), names); // String.compareTo would put the emoji first
    }

    @Test
    void callersBuffer() throws StenoDecodeException {
        String text = "Ελληνικά";
        byte[] buffer = new byte[Steno.maxEncodedLength(text.length())];
        int length = Steno.encode(text, buffer, 0);
        String back = Steno.decode(buffer, 0, length);

        assertEquals(9, length);
        assertEquals(text, back);
    }

    @Test
    void errors() {
        byte[] bad = {0x48, 0x00};

        try {
            Steno.decode(bad);
        } catch (StenoDecodeException e) {
            assertEquals(StenoDecodeException.Reason.ZERO_BYTE, e.getReason());
            assertEquals("zero-byte", e.getReason().specName());
            assertEquals(1, e.getOffset());
            return;
        }

        throw new AssertionError("not rejected");
    }

    @Test
    void lenientDecodingReplaces() {
        byte[] bad = {0x48, 0x00};
        assertEquals("a�", new String(bad, StenoCharset.INSTANCE));
    }

    @Test
    void charsetAndStreams(@TempDir Path dir) throws IOException {
        Charset steno = Charset.forName("x-steno"); // or StenoCharset.INSTANCE

        byte[] bytes = "Grüße".getBytes(steno);
        assertEquals("Grüße", new String(bytes, steno));

        Path path = dir.resolve("notes.steno");

        try (Writer writer = Files.newBufferedWriter(path, steno)) {
            writer.write("日本語のテキスト\n");
            writer.write("Line two\n");
        }

        try (BufferedReader reader = Files.newBufferedReader(path, steno)) {
            assertEquals("日本語のテキスト", reader.readLine());
            assertEquals("Line two", reader.readLine());
        }
    }

    @Test
    void codePoints() throws StenoDecodeException {
        byte[] bytes = Steno.encodeCodePoints(new int[] {0x61, 0xD800, 0x62, 0xDC00});
        assertEquals("48F8C52049F8C940", VectorFile.hex(bytes));
        assertEquals(4, Steno.decodeCodePoints(bytes).length);

        assertThrows(
                IllegalArgumentException.class,
                () -> Steno.encodeCodePoints(new int[] {0xD800, 0xDC00})); // not a value
    }

    @Test
    void asciiText() {
        assertTrue(Steno.isAsciiText(Steno.encode("Hello, world!\n")));
        assertFalse(Steno.isAsciiText(Steno.encode("Grüße")));
    }
}
