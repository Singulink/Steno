package com.singulink.steno;

import java.lang.invoke.MethodHandles;
import java.lang.invoke.VarHandle;
import java.nio.ByteOrder;

/**
 * Word-at-a-time ("SIMD within a register") helpers: 8 bytes in a {@code long}, read and written with one load or
 * store.
 *
 * <p>Byte {@code i} of a word is the byte at offset {@code i} of the array (little-endian view), so it is bits
 * {@code 8i..8i+7}. A mask has the high bit of each byte that matches and no other bits.
 */
final class Swar {
    /** Reads and writes 8 bytes of a {@code byte[]} at any offset as a little-endian {@code long}. */
    static final VarHandle LONGS = MethodHandles.byteArrayViewVarHandle(long[].class, ByteOrder.LITTLE_ENDIAN);

    /** The high bit of each byte. */
    static final long HIGH = 0x8080808080808080L;

    private Swar() {}

    /** Repeats a byte value in the 8 bytes of a word. */
    static long repeat(int b) {
        return (b & 0xFFL) * 0x0101010101010101L;
    }

    /** Subtracts each byte of {@code y} from the same byte of {@code x}, modulo 256, with no borrow between bytes. */
    static long sub(long x, long y) {
        return ((x | HIGH) - (y & ~HIGH)) ^ ((x ^ ~y) & HIGH);
    }

    /**
     * Gets the mask of the bytes of {@code x} in {@code lo..end-1}, for a run of at most 128 byte values.
     *
     * <p>A byte {@code b} is in the run when {@code (b - lo) mod 256} is below 128 and {@code (b - end) mod 256} is
     * not: with a run of at most 128 values, that is exactly {@code (b - lo) mod 256 < end - lo}.
     *
     * @param loWord {@code lo} in every byte
     * @param endWord {@code end} in every byte
     */
    static long inRun(long x, long loWord, long endWord) {
        return ~sub(x, loWord) & sub(x, endWord) & HIGH;
    }

    /**
     * Divides a non-negative {@code int} by 248, the base of the trail bytes, with a multiply and a shift.
     *
     * <p>{@code 0x84210843} is {@code ceil(2^39 / 248)}, and {@code 0x84210843 * 248 - 2^39 = 8 <= 2^(39 - 31)}, so the
     * quotient is exact for every non-negative {@code int} (Granlund and Montgomery); a test checks it as well.
     */
    static int div248(int x) {
        return (int) ((x * 0x84210843L) >>> 39);
    }
}
