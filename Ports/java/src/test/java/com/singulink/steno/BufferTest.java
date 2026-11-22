package com.singulink.steno;

import static org.junit.jupiter.api.Assertions.assertArrayEquals;
import static org.junit.jupiter.api.Assertions.assertEquals;
import static org.junit.jupiter.api.Assertions.assertThrows;

import java.nio.BufferOverflowException;
import java.nio.ByteBuffer;
import org.junit.jupiter.api.Test;

/** Destinations of exact size and too small, and the maximum-size helpers. */
class BufferTest {
    @Test
    void exactDestinationWorksAndSmallerFails() {
        RandomText random = new RandomText(20);

        for (int i = 0; i < 500; i++) {
            String text = random.next(random.random().nextInt(100));
            byte[] expected = Steno.encode(text);

            byte[] exact = new byte[expected.length + 3];
            assertEquals(expected.length, Steno.encode(text, exact, 3));
            assertArrayEquals(expected, java.util.Arrays.copyOfRange(exact, 3, exact.length));

            if (expected.length > 0) {
                assertThrows(
                        IndexOutOfBoundsException.class, () -> Steno.encode(text, new byte[expected.length + 2], 3));

                ByteBuffer small = ByteBuffer.allocate(expected.length - 1);
                assertThrows(BufferOverflowException.class, () -> Steno.encode(text, small));
                assertEquals(0, small.position());

                ByteBuffer direct = ByteBuffer.allocateDirect(expected.length - 1);
                assertThrows(BufferOverflowException.class, () -> Steno.encode(text, direct));
                assertEquals(0, direct.position());
            }

            ByteBuffer slice = ByteBuffer.allocate(expected.length + 10)
                    .position(4)
                    .slice()
                    .limit(expected.length);
            assertEquals(expected.length, Steno.encode(text, slice));
            assertEquals(expected.length, slice.position());
        }
    }

    @Test
    void decodeRangesAndBuffers() throws StenoDecodeException {
        byte[] bytes = Steno.encode("Zürich");
        byte[] padded = new byte[bytes.length + 4];
        System.arraycopy(bytes, 0, padded, 2, bytes.length);

        assertEquals("Zürich", Steno.decode(padded, 2, bytes.length));

        ByteBuffer buffer = ByteBuffer.wrap(padded, 2, bytes.length);
        assertEquals("Zürich", Steno.decode(buffer));
        assertEquals(2 + bytes.length, buffer.position());

        ByteBuffer direct = ByteBuffer.allocateDirect(bytes.length).put(bytes).flip();
        assertEquals("Zürich", Steno.decode(direct));
        assertEquals(bytes.length, direct.position());

        ByteBuffer bad = ByteBuffer.wrap(new byte[] {0x48, 0x00});
        StenoDecodeException e = assertThrows(StenoDecodeException.class, () -> Steno.decode(bad));
        assertEquals(0, bad.position(), "a failed decode leaves the position");
        assertEquals(1, e.getOffset());

        assertEquals(
                1,
                assertThrows(StenoDecodeException.class, () -> Steno.decode(new byte[] {0x07, 0x48, 0x00, 0x07}, 1, 2))
                        .getOffset(),
                "the offset counts from the start of the range");
        assertThrows(IndexOutOfBoundsException.class, () -> Steno.decode(bytes, 1, bytes.length));
    }

    @Test
    void maximumSizesHoldOnWorstCases() throws StenoDecodeException {
        // Lone surrogates take 3 bytes each and do not change the state: the worst case of 3 bytes per char.
        String lone = "\uDC00".repeat(1000);
        assertEquals(Steno.maxEncodedLength(lone.length()), Steno.encode(lone).length);

        // Code points that switch the main window every time, and lone high surrogates at the end.
        StringBuilder switching = new StringBuilder();

        for (int i = 0; i < 1000; i++) {
            switching.appendCodePoint(0x10000 + 0x1000 * (i % 200) + i % 7);
        }

        switching.append('\uD800');
        byte[] bytes = Steno.encode(switching);
        assertEquals(Steno.encode(switching.toString()).length, bytes.length);
        assertEquals(switching.toString(), Steno.decode(bytes));
        assertEquals(true, bytes.length <= Steno.maxEncodedLength(switching.length()));

        // A supplementary main window: each byte gives 2 chars, the worst case of decoding.
        String emoji = "😀".repeat(1000);
        byte[] emojiBytes = Steno.encode(emoji);
        assertEquals(true, emoji.length() <= Steno.maxDecodedLength(emojiBytes.length));
        assertEquals(2 * (emojiBytes.length - 3) + 2, emoji.length());

        byte[] exact = new byte[Steno.maxEncodedLength(lone.length())];
        assertEquals(exact.length, Steno.encode(lone, exact, 0));

        assertThrows(IllegalArgumentException.class, () -> Steno.maxEncodedLength(-1));
        assertThrows(IllegalArgumentException.class, () -> Steno.maxEncodedLength(Integer.MAX_VALUE / 3 + 1));
        assertThrows(IllegalArgumentException.class, () -> Steno.maxDecodedLength(Integer.MAX_VALUE / 2 + 1));
        assertEquals(0, Steno.encode("").length);
        assertEquals("", Steno.decode(new byte[0]));
    }

    @Test
    void growthStopsAtTheLargestArray() {
        int max = Integer.MAX_VALUE - 8;

        // Growth by half of 1.5 G chars passes the largest array: it stops there, where an int sum would overflow.
        assertEquals(max, Steno.grownLength(1_500_000_000, 2_250_000_000L));
        assertEquals(max, Steno.grownLength(max - 1, (long) max + 100));
        assertEquals(11, Steno.grownLength(10, 5), "at least one more");
        assertEquals(15, Steno.grownLength(10, 15));

        // At the largest array, growing again is out of memory rather than a loop that never ends.
        assertThrows(OutOfMemoryError.class, () -> Steno.grownLength(max, (long) max + 1));
    }

    @Test
    void codePointsThatDoNotFitAreOutOfMemory() {
        // The public method sizes the array for 4 bytes per code point; this seam gives a smaller one, as happens when
        // 4 bytes per code point pass the largest array.
        assertThrows(OutOfMemoryError.class, () -> Steno.encodeCodePoints(new int[] {0x4E2D, 0x1F600}, 3));
        assertArrayEquals(Steno.encode("中"), Steno.encodeCodePoints(new int[] {0x4E2D}, 3));
    }

    @Test
    void codePointApiRejectsWhatIsNotAValue() {
        assertThrows(IllegalArgumentException.class, () -> Steno.encodeCodePoints(new int[] {0xD800, 0xDC00}));
        assertThrows(IllegalArgumentException.class, () -> Steno.encodeCodePoints(new int[] {0x110000}));
        assertThrows(IllegalArgumentException.class, () -> Steno.encodeCodePoints(new int[] {-1}));
        assertArrayEquals(Steno.encode("\uDC00\uD800"), Steno.encodeCodePoints(new int[] {0xDC00, 0xD800}));
        assertArrayEquals(Steno.encode("\uD800a\uDC00"), Steno.encodeCodePoints(new int[] {0xD800, 0x61, 0xDC00}));
    }

    @Test
    void otherCharSequences() {
        String text = "Ελληνικά 😀 текст".repeat(200);
        byte[] expected = Steno.encode(text);
        assertArrayEquals(expected, Steno.encode(new StringBuilder(text)));
        assertArrayEquals(expected, Steno.encode(java.nio.CharBuffer.wrap(text)));
        assertArrayEquals(
                expected,
                Steno.encode(
                        java.nio.CharBuffer.wrap(("xx" + text).toCharArray()).position(2)));
        assertArrayEquals(expected, Steno.encode(new StringBuffer(text)));
    }
}
