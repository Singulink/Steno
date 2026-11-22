package com.singulink.steno;

/**
 * The per-thread working state of the one-shot methods for short values: an encoder, a decoder and their buffers, so
 * that a short value costs no allocation but its result.
 *
 * <p>Each thread gets its own, of about 3 KB, on its first short value. Every use resets the encoder or decoder when it
 * ends, so that between calls the scratch holds no layout but the initial one. A call that finds it in use (a
 * {@link CharSequence} whose {@code charAt} encodes another value) gets {@code null} and allocates its own state.
 */
final class Scratch {
    /** The largest number of chars that the scratch encodes, and of bytes that it decodes. */
    static final int CHARS = 256;

    static final int BYTES = 256;

    private static final ThreadLocal<Scratch> CURRENT = ThreadLocal.withInitial(Scratch::new);

    final EncoderCore encoder = new EncoderCore();
    final DecoderCore decoder = new DecoderCore();
    final char[] chars = new char[CHARS];

    /** Room for the worst case: 3 bytes per char (FORMAT.md section 1.3). */
    final byte[] bytes = new byte[3 * CHARS];

    /** Room for the worst case: 2 chars per byte. */
    final char[] decoded = new char[2 * BYTES];

    boolean inUse;

    private Scratch() {}

    /** Gets the scratch of this thread and marks it in use, or returns {@code null} if it is in use already. */
    static Scratch take() {
        Scratch scratch = CURRENT.get();

        if (scratch.inUse) {
            return null;
        }

        scratch.inUse = true;
        return scratch;
    }
}
