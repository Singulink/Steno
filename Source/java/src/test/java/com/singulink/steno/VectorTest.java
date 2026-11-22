package com.singulink.steno;

import static org.junit.jupiter.api.Assertions.assertArrayEquals;
import static org.junit.jupiter.api.Assertions.assertEquals;
import static org.junit.jupiter.api.Assertions.assertThrows;
import static org.junit.jupiter.api.Assertions.assertTrue;
import static org.junit.jupiter.api.Assumptions.assumeTrue;

import java.nio.ByteBuffer;
import java.nio.charset.CharacterCodingException;
import java.nio.file.Path;
import java.util.ArrayList;
import java.util.EnumMap;
import java.util.List;
import java.util.Map;
import org.junit.jupiter.api.Test;

/** The test vectors of FORMAT.md section 12: they decide whether this implementation conforms. */
class VectorTest {
    @Test
    void publishedVectors() {
        Map<VectorFile.Kind, Integer> counts = run(VectorFile.read(VectorFile.PUBLISHED), true);

        assertEquals(6798, counts.get(VectorFile.Kind.VALID));
        assertEquals(829, counts.get(VectorFile.Kind.INVALID));
        assertEquals(13, counts.get(VectorFile.Kind.NOT_A_VALUE));
    }

    @Test
    void largeVectors() {
        String file = System.getenv("STENO_LARGE_VECTORS");
        assumeTrue(file != null && !file.isEmpty(), "STENO_LARGE_VECTORS is not set");

        Map<VectorFile.Kind, Integer> counts = run(VectorFile.read(Path.of(file)), false);
        int total = counts.values().stream().mapToInt(Integer::intValue).sum();
        System.out.println("Large vector file: " + total + " vectors passed " + counts);
    }

    private static Map<VectorFile.Kind, Integer> run(List<VectorFile.Vector> vectors, boolean thorough) {
        Map<VectorFile.Kind, Integer> counts = new EnumMap<>(VectorFile.Kind.class);
        List<String> failures = new ArrayList<>();

        for (VectorFile.Vector v : vectors) {
            try {
                switch (v.kind) {
                    case VALID:
                        checkValid(v, thorough);
                        break;
                    case INVALID:
                        checkInvalid(v, thorough);
                        break;
                    default:
                        assertThrows(IllegalArgumentException.class, () -> Steno.encodeCodePoints(v.codePoints));
                        break;
                }

                counts.merge(v.kind, 1, Integer::sum);
            } catch (AssertionError | RuntimeException | StenoDecodeException e) {
                failures.add(v + ": " + e);
            }
        }

        assertTrue(
                failures.isEmpty(),
                failures.size() + " vectors failed, the first ones:\n"
                        + String.join("\n", failures.subList(0, Math.min(20, failures.size()))));
        return counts;
    }

    private static void checkValid(VectorFile.Vector v, boolean thorough) throws StenoDecodeException {
        String text = VectorFile.utf16(v.codePoints);

        assertArrayEquals(v.bytes, Steno.encodeCodePoints(v.codePoints), "encodeCodePoints");
        assertArrayEquals(v.bytes, Steno.encode(text), "encode");
        assertArrayEquals(v.codePoints, Steno.decodeCodePoints(v.bytes), "decodeCodePoints");
        assertEquals(text, Steno.decode(v.bytes), "decode");

        if (thorough) {
            assertArrayEquals(v.bytes, text.getBytes(StenoCharset.INSTANCE), "String.getBytes");
            assertEquals(text, new String(v.bytes, StenoCharset.INSTANCE), "new String");
            assertTrue(
                    Steno.isAsciiText(v.bytes)
                            == text.chars().allMatch(c -> c == 9 || c == 10 || c == 13 || c >= 0x20 && c <= 0x7E),
                    "isAsciiText");
        }
    }

    private static void checkInvalid(VectorFile.Vector v, boolean thorough) throws StenoDecodeException {
        StenoDecodeException e = assertThrows(StenoDecodeException.class, () -> Steno.decode(v.bytes));
        assertEquals(v.reason, e.getReason(), "reason");
        assertEquals(
                v.reason,
                assertThrows(StenoDecodeException.class, () -> Steno.decodeCodePoints(v.bytes))
                        .getReason());

        // The bytes before the code point that breaks the rule are a valid value, and the bytes of the code point read
        // up to the defect are in the input.
        assertTrue(e.getOffset() < v.bytes.length);
        assertTrue(e.getInputLength() >= 1 && e.getOffset() + e.getInputLength() <= v.bytes.length);
        Steno.decode(v.bytes, 0, (int) e.getOffset());

        if (thorough) {
            assertThrows(
                    CharacterCodingException.class,
                    () -> StenoCharset.INSTANCE.newDecoder().decode(ByteBuffer.wrap(v.bytes)));
            String replaced = new String(v.bytes, StenoCharset.INSTANCE);
            assertEquals(
                    Steno.decode(v.bytes, 0, (int) e.getOffset()) + "�",
                    replaced,
                    "new String replaces from the error on");
        }
    }
}
