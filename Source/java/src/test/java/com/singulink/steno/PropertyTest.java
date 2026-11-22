package com.singulink.steno;

import static org.junit.jupiter.api.Assertions.assertArrayEquals;
import static org.junit.jupiter.api.Assertions.assertEquals;
import static org.junit.jupiter.api.Assertions.assertTrue;

import java.util.ArrayList;
import java.util.Arrays;
import java.util.List;
import org.junit.jupiter.api.Test;

/** The properties of FORMAT.md section 13 on seeded random text. */
class PropertyTest {
    @Test
    void roundTrip() throws StenoDecodeException {
        RandomText random = new RandomText(1);

        for (int i = 0; i < 3000; i++) {
            String text = i % 3 == 0
                    ? random.nextUnits(random.random().nextInt(40))
                    : random.next(random.random().nextInt(200));
            byte[] bytes = Steno.encode(text);

            assertEquals(text, Steno.decode(bytes));
            assertArrayEquals(bytes, Steno.encodeCodePoints(text.codePoints().toArray()));
            assertArrayEquals(text.codePoints().toArray(), Steno.decodeCodePoints(bytes));
            assertTrue(bytes.length <= Steno.maxEncodedLength(text.length()));
            assertTrue(text.length() <= Steno.maxDecodedLength(bytes.length));

            for (byte b : bytes) {
                assertTrue(b != 0, "no zero byte");
            }
        }
    }

    @Test
    void byteOrderIsCodePointOrder() {
        RandomText random = new RandomText(2);

        for (int i = 0; i < 20000; i++) {
            // Pairs that share a prefix, so that they differ at an interesting point.
            String prefix = random.next(random.random().nextInt(8));
            String x = prefix
                    + (i % 4 == 0
                            ? random.nextUnits(3)
                            : random.next(random.random().nextInt(4)));
            String y = prefix
                    + (i % 5 == 0
                            ? random.nextUnits(3)
                            : random.next(random.random().nextInt(4)));

            int expected = Integer.signum(
                    Arrays.compare(x.codePoints().toArray(), y.codePoints().toArray()));
            assertEquals(
                    expected,
                    Integer.signum(Arrays.compareUnsigned(Steno.encode(x), Steno.encode(y))),
                    () -> x + " / " + y);
            assertEquals(expected, Integer.signum(Steno.compareCodePoints(x, y)), () -> x + " / " + y);
            assertEquals(expected, Integer.signum(Steno.CODE_POINT_ORDER.compare(new StringBuilder(x), y)));
        }
    }

    @Test
    void prefixOfValueGivesPrefixOfBytes() {
        RandomText random = new RandomText(3);

        for (int i = 0; i < 1000; i++) {
            String text = random.next(60);
            byte[] whole = Steno.encode(text);
            int[] codePoints = text.codePoints().toArray();

            for (int n = 0; n <= codePoints.length; n += 1 + n / 8) {
                byte[] part = Steno.encodeCodePoints(Arrays.copyOf(codePoints, n));
                assertArrayEquals(part, Arrays.copyOf(whole, part.length));
            }
        }
    }

    @Test
    void bytesBelow08StartOnlyControlsAndTheSpace() {
        // FORMAT.md section 13.5: 01..07 occur only as the first byte of a code point, of a C0 control or the space.
        RandomText random = new RandomText(5);
        String[] separators = {"\n", "\t", "\r\n", " ", "\u0000", "\u000B", "\u001F", "\n\n"};

        for (int i = 0; i < 400; i++) {
            StringBuilder text = new StringBuilder();

            for (int part = random.random().nextInt(6); part >= 0; part--) {
                text.append(random.next(random.random().nextInt(15)));
                text.append(separators[random.random().nextInt(separators.length)]);
            }

            int[] codePoints = text.codePoints().toArray();
            byte[] bytes = Steno.encode(text);

            // Where each code point starts: the length of the encoding of the code points before it (section 13.2).
            int[] codePointAt = new int[bytes.length];
            Arrays.fill(codePointAt, -1);

            for (int k = 0; k < codePoints.length; k++) {
                codePointAt[Steno.encodeCodePoints(Arrays.copyOf(codePoints, k)).length] = k;
            }

            List<Integer> lineFeeds = new ArrayList<>();
            List<Integer> bytes03 = new ArrayList<>();

            for (int p = 0; p < bytes.length; p++) {
                int b = bytes[p];

                if (b == 0x03) {
                    bytes03.add(p);
                }

                if (codePointAt[p] >= 0 && codePoints[codePointAt[p]] == '\n') {
                    lineFeeds.add(p);
                }

                if (b >= 0x08 || b < 0) {
                    continue;
                }

                assertTrue(codePointAt[p] >= 0, "a byte below 08 inside a code point");
                int c = codePoints[codePointAt[p]];
                int expected = c <= 0x08
                        ? 1
                        : c == 0x09 ? 2 : c == 0x0A ? 3 : c <= 0x0C ? 4 : c == 0x0D ? 5 : c <= 0x1F ? 6 : 7;
                assertTrue(c <= 0x20, "a byte below 08 starts U+" + Integer.toHexString(c));
                assertEquals(expected, b);
            }

            // Splitting the bytes on 03 finds exactly the line feeds.
            assertEquals(lineFeeds, bytes03);
        }
    }

    @Test
    void textThatStaysInOneScript() throws StenoDecodeException {
        // Long runs exercise the paths for 1-byte code points; the sizes are those of FORMAT.md section 1.1.
        assertEquals(1000, Steno.encode("a".repeat(1000)).length);
        assertEquals(1001, Steno.encode("Ж".repeat(1000)).length);
        assertEquals(2001, Steno.encode("日".repeat(1000)).length);
        assertEquals(2001, Steno.encode("한".repeat(1000)).length);
        assertEquals(1002, Steno.encode("😀".repeat(1000)).length);

        String emoji = "😀".repeat(1000);
        assertEquals(emoji, Steno.decode(Steno.encode(emoji)));
    }
}
