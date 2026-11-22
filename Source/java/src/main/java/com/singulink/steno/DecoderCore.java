package com.singulink.steno;

/**
 * Decodes one value, given as bytes in one or more pieces, into UTF-16 code units (FORMAT.md section 9).
 *
 * <p>The bytes of a code point that a piece ends in the middle of are not taken: the caller gives them again at the
 * start of the next piece. Not thread safe.
 */
final class DecoderCore extends CoderState {
    static final int UNDERFLOW = 0;
    static final int OVERFLOW = 1;
    static final int ERROR = 2;

    /** Whether the last code point was a high surrogate code point (for rule 5 of section 10.1). */
    boolean lastHigh;

    /** The number of bytes taken by earlier calls since the start of the value. */
    long consumed;

    /**
     * Where the last call stopped in the source and the destination. On an error, {@link #srcPos} is where the code
     * point starts.
     */
    int srcPos;

    int dstPos;

    /**
     * The error of the last call that returned {@link #ERROR}: its reason and the number of bytes of the code point
     * read up to it.
     */
    StenoDecodeException.Reason errorReason;

    int errorLength;

    /** Whether to decode every code point the general way, without the fast paths (for the differential tests). */
    private final boolean plain;

    DecoderCore() {
        this(false);
    }

    DecoderCore(boolean plain) {
        this.plain = plain;
    }

    void reset() {
        resetState();
        lastHigh = false;
        consumed = 0;
        errorReason = null;
        errorLength = 0;
    }

    /**
     * Decodes {@code src[sp..sl)} into {@code dst[dp..dl)}.
     *
     * @return {@link #UNDERFLOW} when every whole code point was taken (the bytes of an unfinished code point at the
     *     end are left from {@link #srcPos}); {@link #OVERFLOW} when the destination has no room for the next code
     *     point; {@link #ERROR} when the bytes at {@link #srcPos} break a rule of section 10.1.
     */
    int decode(byte[] src, int sp, int sl, char[] dst, int dp, int dl) {
        final int start = sp;
        Layout lay = layout;
        int conf = confidence;
        boolean high = lastHigh;
        int status = UNDERFLOW;
        boolean fast = !plain;

        while (sp < sl) {
            int b = src[sp] & 0xFF;

            if (fast) {
                if (b >= 0x07 && b <= lay.asciiLeadHi) {
                    if (dp >= dl) {
                        status = OVERFLOW;
                        break;
                    }

                    dst[dp++] = (char) (b + 0x19);
                    sp++;
                    high = false;
                    continue;
                }

                if (b >= lay.mainLeadLo && b <= lay.mainLeadHi) {
                    int c = b - lay.mainDelta;

                    if (c < 0x10000) {
                        if (dp >= dl) {
                            status = OVERFLOW;
                            break;
                        }

                        dst[dp++] = (char) c;
                    } else {
                        if (dl - dp < 2) {
                            status = OVERFLOW;
                            break;
                        }

                        dst[dp++] = Character.highSurrogate(c);
                        dst[dp++] = Character.lowSurrogate(c);
                    }

                    sp++;
                    high = false;

                    if (conf < 2) {
                        conf++;
                    }

                    continue;
                }

                if (b >= lay.wideLeadLo && b <= lay.wideLeadHi && sp + 1 < sl) {
                    // Ideographs in the CJK context and Hangul syllables in the Hangul context. Anything else in their
                    // range, a zero byte and an unfinished code point go the general way.
                    int u = src[sp + 1] & 0xFF;
                    int c = lay.wideBase + (b - lay.wideLead) * 248 + u - 8;

                    if (u >= 0x08 && c >= lay.wideCharLo && c <= lay.wideCharHi) {
                        if (dp >= dl) {
                            status = OVERFLOW;
                            break;
                        }

                        dst[dp++] = (char) c;
                        sp += 2;
                        high = false;
                        pending = Layout.NONE;
                        continue;
                    }
                }

                if (lay.cjkChar != null && lay.cjkChar[b] != 0) {
                    // CJK punctuation and the kana of the kana slot in the CJK context.
                    if (dp >= dl) {
                        status = OVERFLOW;
                        break;
                    }

                    dst[dp++] = lay.cjkChar[b];
                    sp++;
                    high = false;
                    pending = Layout.NONE;
                    continue;
                }
            }

            // Any other code point, in the order of checks of section 9.2.
            if (b == 0) {
                status = fail(StenoDecodeException.Reason.ZERO_BYTE, 1);
                break;
            }

            int r = lay.rangeOfLead[b];

            if (r < 0) {
                status = fail(StenoDecodeException.Reason.UNUSED_LEAD, 1);
                break;
            }

            int k = lay.size[r];
            int o = b - lay.lead[r];
            boolean whole = true;

            for (int i = 1; i < k; i++) {
                if (sp + i >= sl) {
                    whole = false;
                    break;
                }

                int u = src[sp + i] & 0xFF;

                if (u < 0x08) {
                    // Rule 1 for 00, rule 6 for 01..07 (section 9.2).
                    status = fail(
                            u == 0 ? StenoDecodeException.Reason.ZERO_BYTE : StenoDecodeException.Reason.INVALID_TRAIL,
                            i + 1);
                    break;
                }

                o = o * 248 + u - 8;
            }

            if (status == ERROR || !whole) {
                // An unfinished code point is left for the next piece.
                break;
            }

            int c = lay.lo[r] + o;

            if (c > lay.hi[r]) {
                status = fail(StenoDecodeException.Reason.PAST_RANGE, k);
                break;
            }

            if (high && c >= 0xDC00 && c <= 0xDFFF) {
                status = fail(StenoDecodeException.Reason.SURROGATE_PAIR, k);
                break;
            }

            if (c < 0x10000) {
                if (dp >= dl) {
                    status = OVERFLOW;
                    break;
                }

                dst[dp++] = (char) c;
            } else {
                if (dl - dp < 2) {
                    status = OVERFLOW;
                    break;
                }

                dst[dp++] = Character.highSurrogate(c);
                dst[dp++] = Character.lowSurrogate(c);
            }

            sp += k;
            high = c >= 0xD800 && c <= 0xDBFF;
            confidence = conf;
            update(c, k);
            lay = layout;
            conf = confidence;
        }

        confidence = conf;
        lastHigh = high;
        consumed += sp - start;
        srcPos = sp;
        dstPos = dp;
        return status;
    }

    private int fail(StenoDecodeException.Reason reason, int length) {
        errorReason = reason;
        errorLength = length;
        return ERROR;
    }

    /**
     * Creates the exception for the last error, whose code point starts {@code offset} bytes after the start of the
     * value.
     */
    StenoDecodeException exception(long offset) {
        return new StenoDecodeException(errorReason, offset, errorLength);
    }
}
