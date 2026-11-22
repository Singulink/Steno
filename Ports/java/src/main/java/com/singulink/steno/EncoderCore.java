package com.singulink.steno;

/**
 * Encodes one value, given as UTF-16 code units in one or more pieces (FORMAT.md section 8).
 *
 * <p>A high surrogate at the end of a piece is held until the next piece, which may start with its low surrogate, or
 * until {@link #flush}, which encodes it as a code point of its own. Not thread safe.
 */
final class EncoderCore extends CoderState {
    /**
     * The byte of each code point U+0000..U+007F that takes 1 byte in the initial layout and never changes the state,
     * or 0. The bytes of U+0009, U+000A, U+000D and U+0020..U+007E are the same in every layout in which they take 1
     * byte (section 14.1).
     */
    private static final byte[] ASCII = new byte[128];

    static {
        for (int c = 0x20; c <= 0x7E; c++) {
            ASCII[c] = (byte) (c - 0x19);
        }

        ASCII[0x09] = 0x02;
        ASCII[0x0A] = 0x03;
        ASCII[0x0D] = 0x05;
    }

    /** A high surrogate held from the end of the last piece, or 0. */
    char heldHigh;

    /** Where the last call stopped in the source and the destination. */
    int srcPos;

    int dstPos;

    /** Whether to encode every code point the general way, without the fast paths (for the differential tests). */
    private final boolean plain;

    EncoderCore() {
        this(false);
    }

    EncoderCore(boolean plain) {
        this.plain = plain;
    }

    void reset() {
        resetState();
        heldHigh = 0;
    }

    /**
     * Encodes {@code src[sp..sl)} into {@code dst[dp..dl)}.
     *
     * @return {@code true} if every char was taken; {@code false} if the destination has no room for the next code
     *     point. Either way {@link #srcPos} and {@link #dstPos} say how far the call got.
     */
    boolean encode(char[] src, int sp, int sl, byte[] dst, int dp, int dl) {
        if (heldHigh != 0) {
            if (sp == sl) {
                srcPos = sp;
                dstPos = dp;
                return true;
            }

            char c = src[sp];
            boolean pair = Character.isLowSurrogate(c);
            int next = encodeCodePoint(pair ? Character.toCodePoint(heldHigh, c) : heldHigh, dst, dp, dl);

            if (next < 0) {
                srcPos = sp;
                dstPos = dp;
                return false;
            }

            heldHigh = 0;
            dp = next;

            if (pair) {
                sp++;
            }
        }

        Layout lay = layout;
        int conf = confidence;
        boolean done = true;
        boolean fast = !plain;

        while (sp < sl) {
            char c = src[sp];

            if (fast) {
                if (c < 0x80) {
                    int b = ASCII[c];

                    if (b != 0 && (c <= 0x40 || lay.asciiLeadHi == 0x65)) {
                        if (dp >= dl) {
                            done = false;
                            break;
                        }

                        dst[dp++] = (byte) b;
                        sp++;
                        continue;
                    }
                } else if (c >= lay.mainCharLo && c <= lay.mainCharHi) {
                    if (dp >= dl) {
                        done = false;
                        break;
                    }

                    dst[dp++] = (byte) (c + lay.mainDelta);
                    sp++;

                    if (conf < 2) {
                        conf++;
                    }

                    continue;
                } else if (c >= lay.wideCharLo && c <= lay.wideCharHi) {
                    // Ideographs in the CJK context and Hangul syllables in the Hangul context.
                    if (dl - dp < 2) {
                        done = false;
                        break;
                    }

                    int o = c - lay.wideBase;
                    int q = Swar.div248(o);
                    dst[dp] = (byte) (lay.wideLead + q);
                    dst[dp + 1] = (byte) (o - q * 248 + 8);
                    dp += 2;
                    sp++;
                    pending = Layout.NONE;
                    continue;
                } else if (lay.cjkByte != null && c >= 0x3000 && c <= 0x30FF && lay.cjkByte[c - 0x3000] != 0) {
                    // CJK punctuation and the kana of the kana slot in the CJK context.
                    if (dp >= dl) {
                        done = false;
                        break;
                    }

                    dst[dp++] = lay.cjkByte[c - 0x3000];
                    sp++;
                    pending = Layout.NONE;
                    continue;
                }
            }

            int cp = c;
            int units = 1;

            if (Character.isHighSurrogate(c)) {
                if (sp + 1 == sl) {
                    // Wait for the next piece, which may start with the low surrogate.
                    heldHigh = c;
                    sp++;
                    break;
                }

                char d = src[sp + 1];

                if (Character.isLowSurrogate(d)) {
                    cp = Character.toCodePoint(c, d);
                    units = 2;
                }
            }

            confidence = conf;
            int next = encodeCodePoint(cp, dst, dp, dl);

            if (next < 0) {
                done = false;
                break;
            }

            dp = next;
            sp += units;
            lay = layout;
            conf = confidence;
        }

        confidence = conf;
        srcPos = sp;
        dstPos = dp;
        return done;
    }

    /**
     * Encodes a held high surrogate as a code point of its own.
     *
     * @return {@code false} if the destination has no room for it; {@link #dstPos} says how far the call got.
     */
    boolean flush(byte[] dst, int dp, int dl) {
        if (heldHigh != 0) {
            int next = encodeCodePoint(heldHigh, dst, dp, dl);

            if (next < 0) {
                dstPos = dp;
                return false;
            }

            heldHigh = 0;
            dp = next;
        }

        dstPos = dp;
        return true;
    }

    /**
     * Encodes one code point in the layout of the state and updates the state (sections 8.2 and 7).
     *
     * @return the position after the bytes, or -1 if the destination has no room for them (the state is then
     *     unchanged).
     */
    int encodeCodePoint(int c, byte[] dst, int dp, int dl) {
        Layout lay = layout;
        int r = lay.find(c);
        int k = lay.size[r];

        if (dl - dp < k) {
            return -1;
        }

        int o = c - lay.lo[r];
        int lead = lay.lead[r];

        switch (k) {
            case 1:
                dst[dp] = (byte) (lead + o);
                break;
            case 2: {
                int q = Swar.div248(o);
                dst[dp] = (byte) (lead + q);
                dst[dp + 1] = (byte) (o - q * 248 + 8);
                break;
            }
            case 3: {
                int t = Swar.div248(o);
                int q = Swar.div248(t);
                dst[dp] = (byte) (lead + q);
                dst[dp + 1] = (byte) (t - q * 248 + 8);
                dst[dp + 2] = (byte) (o - t * 248 + 8);
                break;
            }
            default: {
                // A size 4 range has one lead byte: every offset is below 248^3, since U+10FFFF is.
                int t = Swar.div248(o);
                int q = Swar.div248(t);
                dst[dp] = (byte) lead;
                dst[dp + 1] = (byte) (q + 8);
                dst[dp + 2] = (byte) (t - q * 248 + 8);
                dst[dp + 3] = (byte) (o - t * 248 + 8);
                break;
            }
        }

        update(c, k);
        return dp + k;
    }
}
