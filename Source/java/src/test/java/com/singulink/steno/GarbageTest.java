package com.singulink.steno;

import static org.junit.jupiter.api.Assertions.assertArrayEquals;
import static org.junit.jupiter.api.Assertions.assertEquals;
import static org.junit.jupiter.api.Assertions.assertTrue;

import java.nio.ByteBuffer;
import java.nio.CharBuffer;
import java.util.Arrays;
import java.util.Random;
import org.junit.jupiter.api.Test;

/** Random, cut-off and mutated bytes: each is rejected, or decodes to a value that encodes to the same bytes. */
class GarbageTest {
    @Test
    void randomBytes() {
        Random r = new Random(30);

        for (int i = 0; i < 20000; i++) {
            byte[] bytes = new byte[r.nextInt(24)];
            r.nextBytes(bytes);

            // Bias towards bytes that a decoder accepts more often.
            for (int j = 0; j < bytes.length; j++) {
                if (r.nextInt(3) == 0) {
                    bytes[j] = (byte) (0x07 + r.nextInt(0x5F));
                }
            }

            check(bytes);
        }
    }

    @Test
    void cutAndMutatedBytes() {
        RandomText random = new RandomText(31);
        Random r = random.random();

        for (int i = 0; i < 5000; i++) {
            byte[] valid = Steno.encode(random.next(1 + r.nextInt(40)));

            check(Arrays.copyOf(valid, r.nextInt(valid.length + 1)));

            byte[] mutated = valid.clone();

            for (int n = 1 + r.nextInt(3); n > 0; n--) {
                int at = r.nextInt(mutated.length);
                mutated[at] =
                        r.nextBoolean() ? (byte) r.nextInt(256) : (byte) (mutated[at] + (r.nextBoolean() ? 1 : -1));
            }

            check(mutated);
        }
    }

    /** Checks one byte string through the one-shot decoder and the charset decoder. */
    private static void check(byte[] bytes) {
        String text;

        try {
            text = Steno.decode(bytes);
        } catch (StenoDecodeException e) {
            assertTrue(e.getOffset() >= 0 && e.getOffset() < Math.max(1, bytes.length), VectorFile.hex(bytes));
            CoderResultHolder holder = decodeWithCharset(bytes);
            assertTrue(holder.malformed, "the charset decoder rejects it too: " + VectorFile.hex(bytes));
            return;
        }

        assertArrayEquals(bytes, Steno.encode(text), () -> "re-encoding " + VectorFile.hex(bytes));
        CoderResultHolder holder = decodeWithCharset(bytes);
        assertEquals(text, holder.text);
    }

    private static CoderResultHolder decodeWithCharset(byte[] bytes) {
        CoderResultHolder holder = new CoderResultHolder();
        CharBuffer out = CharBuffer.allocate(2 * bytes.length);
        holder.malformed = StenoCharset.INSTANCE
                .newDecoder()
                .decode(ByteBuffer.wrap(bytes), out, true)
                .isMalformed();
        holder.text = out.flip().toString();
        return holder;
    }

    private static final class CoderResultHolder {
        boolean malformed;
        String text;
    }
}
